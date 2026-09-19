using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LanProxy.Proxy;

/// <summary>
/// 局域网 HTTP + SOCKS5 代理服务器。
/// 纯本地转发：仅把客户端请求转发到目标服务器，不含任何远程代理/翻墙逻辑。
/// </summary>
public class ProxyServer
{
    private readonly ConcurrentDictionary<string, ClientSession> _sessions = new();
    private TcpListener? _httpListener;
    private TcpListener? _socksListener;
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }
    public ICollection<ClientSession> Sessions => _sessions.Values;

    public event Action<ClientSession>? SessionConnected;
    public event Action<ClientSession>? SessionDisconnected;
    public event Action<string>? Log;

    public void Start(string ip, int httpPort, int socksPort)
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            if (httpPort > 0)
            {
                _httpListener = new TcpListener(IPAddress.Parse(ip), httpPort);
                _httpListener.Start();
            }
            if (socksPort > 0)
            {
                _socksListener = new TcpListener(IPAddress.Parse(ip), socksPort);
                _socksListener.Start();
            }
        }
        catch
        {
            _httpListener?.Stop();
            _socksListener?.Stop();
            _httpListener = null;
            _socksListener = null;
            throw;
        }

        IsRunning = true;
        if (_httpListener != null)
        {
            Log?.Invoke($"HTTP 代理已监听 {ip}:{httpPort}");
            _ = Task.Run(() => AcceptLoop(_httpListener, "HTTP", HandleHttpAsync, token));
        }
        if (_socksListener != null)
        {
            Log?.Invoke($"SOCKS5 代理已监听 {ip}:{socksPort}");
            _ = Task.Run(() => AcceptLoop(_socksListener, "SOCKS5", HandleSocksAsync, token));
        }
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _cts?.Cancel();
        _httpListener?.Stop();
        _httpListener = null;
        _socksListener?.Stop();
        _socksListener = null;
        foreach (var s in _sessions.Values)
        {
            try { s.Client.Close(); } catch { }
        }
        _sessions.Clear();
        Log?.Invoke("代理已停止");
    }

    private async Task AcceptLoop(TcpListener listener, string protocol,
        Func<TcpClient, ClientSession, CancellationToken, Task> handler, CancellationToken token)
    {
        while (IsRunning && !token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch
            {
                break;
            }

            var remote = client.Client.RemoteEndPoint as IPEndPoint;
            var session = new ClientSession(Guid.NewGuid().ToString("N"),
                remote?.Address?.ToString() ?? "未知", protocol)
            {
                Client = client
            };
            _sessions[session.Id] = session;
            SessionConnected?.Invoke(session);
            _ = RunSessionAsync(client, session, handler, token);
        }
    }

    private async Task RunSessionAsync(TcpClient client, ClientSession session,
        Func<TcpClient, ClientSession, CancellationToken, Task> handler, CancellationToken token)
    {
        try
        {
            await handler(client, session, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log?.Invoke($"{session.Protocol} 连接异常：{ex.Message}");
        }
        finally
        {
            try { client.Close(); } catch { }
            _sessions.TryRemove(session.Id, out _);
            SessionDisconnected?.Invoke(session);
        }
    }

    // ================= HTTP 代理 =================

    private async Task HandleHttpAsync(TcpClient client, ClientSession session, CancellationToken token)
    {
        var stream = client.GetStream();
        stream.ReadTimeout = 30000;

        var read = await ReadHttpHeaderAsync(stream, token);
        if (read == null) return;
        var (header, extra) = read.Value;
        if (header.Length == 0) return;

        string headerText = Encoding.ASCII.GetString(header);
        var lines = headerText.Split("\r\n");
        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2) return;

        string method = requestLine[0];
        string target = requestLine[1];
        bool isConnect = method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase);

        string host;
        int port;
        if (isConnect)
        {
            (host, port) = ParseHostPort(target, 443);
        }
        else if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(target);
            host = uri.Host;
            port = uri.Port;
            // 字节级改写请求行为 origin-form（保留其余头部字节，避免中文 header 乱码）
            string newFirst = $"{method} {uri.PathAndQuery} HTTP/1.1";
            int firstCrlf = IndexOfFirstCrlf(header);
            if (firstCrlf > 0)
            {
                var newLine = Encoding.ASCII.GetBytes(newFirst);
                var rest = header.AsSpan(firstCrlf).ToArray();
                header = newLine.Concat(rest).ToArray();
            }
        }
        else
        {
            var hostHeader = lines.FirstOrDefault(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase));
            if (hostHeader == null) return;
            (host, port) = ParseHostPort(hostHeader[5..].Trim(), 80);
        }

        if (string.IsNullOrEmpty(host)) return;

        TcpClient targetClient;
        try
        {
            targetClient = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(15000);
            await targetClient.ConnectAsync(host, port, cts.Token);
        }
        catch
        {
            return;
        }

        using (targetClient)
        {
            var targetStream = targetClient.GetStream();
            if (isConnect)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), token);
                if (extra.Length > 0)
                {
                    session.AddUpload(extra.Length);
                    await targetStream.WriteAsync(extra, token);
                }
                await RelayAsync(stream, targetStream, session, token);
            }
            else
            {
                session.AddUpload(header.Length + extra.Length);
                await targetStream.WriteAsync(header, token);
                if (extra.Length > 0) await targetStream.WriteAsync(extra, token);
                await RelayAsync(stream, targetStream, session, token);
            }
        }
    }

    private static async Task<(byte[] header, byte[] extra)?> ReadHttpHeaderAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new List<byte>(8192);
        var chunk = new byte[4096];
        while (buffer.Count < 64 * 1024)
        {
            int n;
            try { n = await stream.ReadAsync(chunk, token); }
            catch { return null; }
            if (n <= 0) return buffer.Count == 0 ? null : (buffer.ToArray(), Array.Empty<byte>());

            buffer.AddRange(chunk.AsSpan(0, n).ToArray());
            int idx = IndexOfHeaderEnd(buffer);
            if (idx >= 0)
            {
                var header = buffer.Take(idx + 4).ToArray();
                var extra = buffer.Skip(idx + 4).ToArray();
                return (header, extra);
            }
        }
        return (buffer.ToArray(), Array.Empty<byte>());
    }

    private static int IndexOfHeaderEnd(List<byte> data)
    {
        for (int i = 0; i + 3 < data.Count; i++)
            if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                return i;
        return -1;
    }

    private static int IndexOfFirstCrlf(byte[] data)
    {
        for (int i = 0; i + 1 < data.Length; i++)
            if (data[i] == 13 && data[i + 1] == 10)
                return i;
        return -1;
    }

    // ================= SOCKS5 代理 =================

    private async Task HandleSocksAsync(TcpClient client, ClientSession session, CancellationToken token)
    {
        var stream = client.GetStream();
        stream.ReadTimeout = 30000;

        // 1. 握手：VER=5, NMETHODS, METHODS[]
        var ver = await ReadExactAsync(stream, 1, token);
        if (ver.Length < 1 || ver[0] != 5) return;
        var nMethods = await ReadExactAsync(stream, 1, token);
        if (nMethods.Length < 1) return;
        await ReadExactAsync(stream, nMethods[0], token);
        await stream.WriteAsync(new byte[] { 5, 0 }, token); // 无认证方式

        // 2. 请求：VER=5, CMD, RSV=0, ATYP, DST.ADDR, DST.PORT
        var head = await ReadExactAsync(stream, 4, token);
        if (head.Length < 4) return;
        byte cmd = head[1];
        byte atyp = head[3];

        string host;
        switch (atyp)
        {
            case 1: // IPv4
                var ip4 = await ReadExactAsync(stream, 4, token);
                if (ip4.Length < 4) return;
                host = new IPAddress(ip4).ToString();
                break;
            case 3: // 域名
                var len = await ReadExactAsync(stream, 1, token);
                if (len.Length < 1) return;
                var domain = await ReadExactAsync(stream, len[0], token);
                if (domain.Length < len[0]) return;
                host = Encoding.UTF8.GetString(domain);
                break;
            case 4: // IPv6
                var ip6 = await ReadExactAsync(stream, 16, token);
                if (ip6.Length < 16) return;
                host = new IPAddress(ip6).ToString();
                break;
            default:
                return;
        }

        var portBytes = await ReadExactAsync(stream, 2, token);
        if (portBytes.Length < 2) return;
        int port = (portBytes[0] << 8) | portBytes[1];

        if (cmd != 1) // 仅支持 CONNECT
        {
            await WriteSocksReply(stream, 7);
            return;
        }

        try
        {
            var targetClient = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(15000);
            await targetClient.ConnectAsync(host, port, cts.Token);

            using (targetClient)
            {
                await WriteSocksReply(stream, 0);
                await RelayAsync(stream, targetClient.GetStream(), session, token);
            }
        }
        catch
        {
            try { await WriteSocksReply(stream, 5); } catch { }
        }
    }

    private static async Task WriteSocksReply(NetworkStream stream, byte code)
    {
        byte[] reply = { 5, code, 0, 1, 0, 0, 0, 0, 0, 0 };
        await stream.WriteAsync(reply);
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken token)
    {
        var buf = new byte[count];
        int got = 0;
        while (got < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(got, count - got), token);
            if (n <= 0) break;
            got += n;
        }
        return buf.AsSpan(0, got).ToArray();
    }

    // ================= 双向转发与流量统计 =================

    private static async Task RelayAsync(NetworkStream a, NetworkStream b, ClientSession session, CancellationToken token)
    {
        var t1 = PumpAsync(a, b, session.AddUpload, token);
        var t2 = PumpAsync(b, a, session.AddDownload, token);
        await Task.WhenAny(t1, t2);
        // 任一方向结束即关闭整个隧道（由调用方 Dispose TcpClient 关闭底层连接）
    }

    private static async Task PumpAsync(NetworkStream from, NetworkStream to, Action<long> count, CancellationToken token)
    {
        var buf = new byte[16384];
        try
        {
            while (true)
            {
                int n = await from.ReadAsync(buf, token);
                if (n <= 0) break;
                count(n);
                await to.WriteAsync(buf.AsMemory(0, n), token);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private static (string host, int port) ParseHostPort(string s, int defaultPort)
    {
        s = s.Trim();
        if (s.StartsWith("[")) // IPv6: [::1]:8080
        {
            int close = s.IndexOf(']');
            if (close > 0)
            {
                string h = s[1..close];
                string rest = s[(close + 1)..];
                int p = rest.StartsWith(":") && int.TryParse(rest[1..], out var pp) ? pp : defaultPort;
                return (h, p);
            }
        }
        int idx = s.LastIndexOf(':');
        if (idx > 0 && !s.Contains("://"))
        {
            string h = s[..idx];
            if (int.TryParse(s[(idx + 1)..], out var p)) return (h, p);
        }
        return (s, defaultPort);
    }
}
