using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
using LanProxy.Proxy;
using OxyPlot;
using OxyPlot.Series;

namespace LanProxy;

public partial class MainWindow : Window
{
    private readonly ProxyServer _server = new();
    private readonly ObservableCollection<ClientSession> _clientList = new();
    private readonly List<IPAddress> _ipList = new();
    private readonly DispatcherTimer _timer;
    private readonly bool _autoStart;
    private readonly string? _logFile;

    private readonly PlotModel _plotModel;
    private readonly LineSeries _upSeries;
    private readonly LineSeries _downSeries;
    private const int MaxPoints = 60;
    private int _pointIndex;
    private long _lastUp;
    private long _lastDown;
    private DateTime _lastTick;

    public MainWindow()
    {
        InitializeComponent();

        // 实时流量曲线
        _upSeries = new LineSeries
        {
            Title = "上行 (KB/s)",
            Color = OxyColors.Red,
            StrokeThickness = 1.5
        };
        _downSeries = new LineSeries
        {
            Title = "下行 (KB/s)",
            Color = OxyColors.Blue,
            StrokeThickness = 1.5
        };
        _plotModel = new PlotModel
        {
            Title = "实时流量 (KB/s)"
        };
        _plotModel.Series.Add(_upSeries);
        _plotModel.Series.Add(_downSeries);
        PlotTraffic.Model = _plotModel;

        GridClients.ItemsSource = _clientList;

        LoadAdapters();

        // 代理事件 -> UI 线程
        _server.Log += msg => Dispatcher.BeginInvoke(() => AppendLog(msg));
        _server.SessionConnected += s => Dispatcher.BeginInvoke(() =>
        {
            _clientList.Add(s);
            AppendLog($"新连接  {s.ClientIp}  [{s.Protocol}]");
        });
        _server.SessionDisconnected += s => Dispatcher.BeginInvoke(() =>
        {
            _clientList.Remove(s);
            AppendLog($"断开    {s.ClientIp}  [{s.Protocol}]");
        });

        _lastTick = DateTime.Now;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        // 命令行快捷启动：LanProxy.exe --autostart [httpPort] [socksPort]
        var args = Environment.GetCommandLineArgs();
        _autoStart = args.Length > 1 && args[1].Equals("--autostart", StringComparison.OrdinalIgnoreCase);
        if (_autoStart)
        {
            _logFile = Path.Combine(AppContext.BaseDirectory, "lanproxy-autostart.log");
            if (args.Length > 2 && int.TryParse(args[2], out var p1) && p1 > 0) TxtHttpPort.Text = p1.ToString();
            if (args.Length > 3 && int.TryParse(args[3], out var p2) && p2 > 0) TxtSocksPort.Text = p2.ToString();
            AppendLog($"命令行参数: {string.Join(" ", args)}");
            BtnStart_Click(null, null);
        }
    }

    // ================= 网卡 =================

    private void LoadAdapters()
    {
        CboAdapter.Items.Clear();
        _ipList.Clear();
        // 首选：自动（监听所有网卡，最稳妥）
        _ipList.Add(IPAddress.Any);
        CboAdapter.Items.Add("自动（所有网卡 0.0.0.0）");
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ip in ni.GetIPProperties().UnicastAddresses)
            {
                if (ip.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip.Address))
                {
                    _ipList.Add(ip.Address);
                    CboAdapter.Items.Add($"{ni.Name}  ({ip.Address})");
                    break; // 每个网卡取一个 IPv4
                }
            }
        }
        CboAdapter.SelectedIndex = 0;
    }

    private string GetSelectedIp()
    {
        if (CboAdapter.SelectedIndex >= 0 && CboAdapter.SelectedIndex < _ipList.Count)
            return _ipList[CboAdapter.SelectedIndex].ToString();
        return "0.0.0.0";
    }

    /// <summary>复制/展示用的局域网可达 IP（选中“自动”时取第一个实际 IP）</summary>
    private string GetLanIp()
    {
        var ip = GetSelectedIp();
        if (ip != "0.0.0.0") return ip;
        foreach (var a in _ipList)
            if (!a.Equals(IPAddress.Any) && !IPAddress.IsLoopback(a))
                return a.ToString();
        return "127.0.0.1";
    }

    // ================= 按钮 =================

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        var ip = GetLanIp();
        try
        {
            Clipboard.SetText(ip);
            AppendLog($"已复制 IP：{ip}（HTTP 端口 {TxtHttpPort.Text.Trim()} / SOCKS5 端口 {TxtSocksPort.Text.Trim()}）");
        }
        catch (Exception ex)
        {
            AppendLog($"复制失败：{ex.Message}");
        }
    }

    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TxtHttpPort.Text.Trim(), out var httpPort) || httpPort < 1 || httpPort > 65535)
        {
            AppendLog("HTTP 端口无效");
            return;
        }
        if (!int.TryParse(TxtSocksPort.Text.Trim(), out var socksPort) || socksPort < 1 || socksPort > 65535)
        {
            AppendLog("SOCKS5 端口无效");
            return;
        }

        try
        {
            _server.Start(GetSelectedIp(), httpPort, socksPort);
            BtnStart.IsEnabled = false;
            BtnStop.IsEnabled = true;
            TxtStatus.Text = $"运行中  {GetLanIp()}   HTTP:{httpPort}   SOCKS5:{socksPort}";
            _lastTick = DateTime.Now;
            _lastUp = 0;
            _lastDown = 0;
        }
        catch (Exception ex)
        {
            AppendLog($"启动失败：{ex.Message}");
        }
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _server.Stop();
        BtnStart.IsEnabled = true;
        BtnStop.IsEnabled = false;
        TxtStatus.Text = "已停止";
    }

    private void ChkLog_Toggled(object sender, RoutedEventArgs e)
    {
        if (LogPanel == null) return; // XAML 加载期间 IsChecked 初始化会提前触发
        LogPanel.Visibility = ChkLog.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================= 每秒刷新 =================

    private void Timer_Tick(object? sender, EventArgs e)
    {
        long up = 0, down = 0;
        foreach (var s in _server.Sessions)
        {
            up += s.UploadBytes;
            down += s.DownloadBytes;
        }

        var now = DateTime.Now;
        double sec = (now - _lastTick).TotalSeconds;
        if (sec <= 0) sec = 0.001;
        double upKbs = Math.Max(0, (up - _lastUp) / 1024.0 / sec);
        double downKbs = Math.Max(0, (down - _lastDown) / 1024.0 / sec);
        _lastUp = up;
        _lastDown = down;
        _lastTick = now;

        _pointIndex++;
        _upSeries.Points.Add(new DataPoint(_pointIndex, upKbs));
        _downSeries.Points.Add(new DataPoint(_pointIndex, downKbs));
        while (_upSeries.Points.Count > MaxPoints) _upSeries.Points.RemoveAt(0);
        while (_downSeries.Points.Count > MaxPoints) _downSeries.Points.RemoveAt(0);
        _plotModel.InvalidatePlot(true);

        // 刷新连接列表显示
        foreach (var s in _clientList) s.RaiseStatsChanged();

        TxtTotal.Text = $"上行 {ClientSession.FormatBytes(up)} · 下行 {ClientSession.FormatBytes(down)}";
    }

    private void AppendLog(string msg)
    {
        TxtLog.AppendText($"{DateTime.Now:HH:mm:ss}  {msg}\n");
        TxtLog.ScrollToEnd();
        if (_logFile != null)
        {
            try { File.AppendAllText(_logFile, $"{DateTime.Now:HH:mm:ss}  {msg}\n"); }
            catch { }
        }
    }
}
