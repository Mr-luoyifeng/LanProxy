using System.ComponentModel;
using System.Net.Sockets;

namespace LanProxy.Proxy;

/// <summary>单个客户端代理会话，统计上下行流量</summary>
public class ClientSession : INotifyPropertyChanged
{
    public string Id { get; }
    public string ClientIp { get; }
    public string Protocol { get; }
    public TcpClient Client { get; set; } = null!;

    private long _uploadBytes;
    private long _downloadBytes;

    public ClientSession(string id, string clientIp, string protocol)
    {
        Id = id;
        ClientIp = clientIp;
        Protocol = protocol;
    }

    public long UploadBytes => Interlocked.Read(ref _uploadBytes);
    public long DownloadBytes => Interlocked.Read(ref _downloadBytes);
    public string UploadText => FormatBytes(UploadBytes);
    public string DownloadText => FormatBytes(DownloadBytes);

    public void AddUpload(long n) => Interlocked.Add(ref _uploadBytes, n);
    public void AddDownload(long n) => Interlocked.Add(ref _downloadBytes, n);

    /// <summary>UI 线程每秒调用，刷新绑定显示</summary>
    public void RaiseStatsChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UploadText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DownloadText)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        return $"{bytes / 1024.0 / 1024.0:0.00} MB";
    }
}
