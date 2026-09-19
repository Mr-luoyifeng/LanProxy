using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LanProxy.Proxy;
using OxyPlot;
using OxyPlot.Series;

namespace LanProxy;

public partial class MainWindow : Window
{
    private readonly ProxyServer _server = new();
    private readonly ObservableCollection<ClientSession> _clientList = new();
    private readonly ObservableCollection<AdapterItem> _adapterList = new();
    private readonly DispatcherTimer _timer;
    private readonly bool _autoStart;
    private readonly string? _logFile;

    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private bool _allowClose;

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
        CboAdapter.ItemsSource = _adapterList;

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
        _adapterList.Clear();
        // 首选：自动（监听所有网卡，最稳妥）
        _adapterList.Add(new AdapterItem
        {
            Display = "自动（所有网卡 0.0.0.0）",
            SubLine = "监听全部网卡，局域网设备任选本机 IP 接入",
            Ip = IPAddress.Any,
            IsUp = true
        });

        // GUID -> NetworkInterface 映射（用于取 IPv4）
        var nicByGuid = new Dictionary<string, NetworkInterface>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            nicByGuid[ni.Id.ToUpper()] = ni;

        // 与系统「网络连接」面板(ncpa.cpl)一致：只列出 NetConnectionID 非空的适配器
        var items = new List<(string connId, string desc, bool up, IPAddress? ip)>();
        using (var searcher = new ManagementObjectSearcher(
                   "SELECT NetConnectionID, Name, GUID, NetConnectionStatus FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL"))
        {
            foreach (ManagementObject mo in searcher.Get())
            {
                string connId = mo["NetConnectionID"]?.ToString() ?? "";
                string desc = mo["Name"]?.ToString() ?? connId;
                string? guid = mo["GUID"]?.ToString();
                bool up = Convert.ToInt32(mo["NetConnectionStatus"] ?? 0) == 2;

                IPAddress? ip = null;
                if (guid != null && nicByGuid.TryGetValue(guid.ToUpper(), out var ni))
                {
                    foreach (var a in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork
                            && !IPAddress.IsLoopback(a.Address)
                            && !a.Address.ToString().StartsWith("169.254."))
                        {
                            ip = a.Address;
                            break;
                        }
                    }
                }
                items.Add((connId, desc, up, ip));
            }
        }

        // 已连接在前、未连接在后；真实网卡在前、虚拟在后
        var ordered = items
            .OrderByDescending(x => x.up)
            .ThenBy(x => IsVirtualAdapter(x.desc) ? 1 : 0)
            .ToList();

        foreach (var (connId, desc, up, ip) in ordered)
        {
            string tag = IsVirtualAdapter(desc) ? "（虚拟）" : "";
            string sub = up
                ? (ip != null ? $"{desc}{tag}  ·  {ip}" : $"{desc}{tag}  ·  无可用 IPv4")
                : $"{desc}{tag}  ·  未连接";
            _adapterList.Add(new AdapterItem
            {
                Display = connId,
                SubLine = sub,
                Ip = ip,
                IsUp = up
            });
        }
        CboAdapter.SelectedIndex = 0;
    }

    /// <summary>识别明显的虚拟/特殊适配器</summary>
    private static bool IsVirtualAdapter(string desc)
    {
        var text = desc.ToLowerInvariant();
        string[] keys =
        {
            "vmware", "virtualbox", "virtual", "hyper-v", "vethernet", "wsl",
            "bluetooth", "wi-fi direct", "hosted network", "tap", "tun",
            "hamachi", "zerotier", "vpn", "ndis", "virtual adapter",
            "host-only", "loopback", "tunnel", "security tunnel"
        };
        foreach (var k in keys)
            if (text.Contains(k)) return true;
        return false;
    }

    /// <summary>当前选中网卡的可监听 IP；null = 未连接且无 IP</summary>
    private string? GetSelectedIp()
    {
        if (CboAdapter.SelectedIndex >= 0 && CboAdapter.SelectedIndex < _adapterList.Count)
            return _adapterList[CboAdapter.SelectedIndex].Ip?.ToString();
        return "0.0.0.0";
    }

    /// <summary>复制/展示用的局域网可达 IP（选中“自动”时取第一个实际 IP）</summary>
    private string GetLanIp()
    {
        var sel = CboAdapter.SelectedIndex >= 0 && CboAdapter.SelectedIndex < _adapterList.Count
            ? _adapterList[CboAdapter.SelectedIndex] : null;
        if (sel?.Ip != null && !sel.Ip.Equals(IPAddress.Any) && !IPAddress.IsLoopback(sel.Ip))
            return sel.Ip.ToString();
        foreach (var a in _adapterList)
            if (a.Ip != null && !a.Ip.Equals(IPAddress.Any) && !IPAddress.IsLoopback(a.Ip))
                return a.Ip.ToString();
        return "127.0.0.1";
    }

    // ================= 按钮 =================

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        var sel = CboAdapter.SelectedIndex >= 0 && CboAdapter.SelectedIndex < _adapterList.Count
            ? _adapterList[CboAdapter.SelectedIndex] : null;
        if (sel == null) return;
        if (!sel.IsUp || sel.Ip == null)
        {
            AppendLog("当前网卡未连接，无可复制 IP，请选择已连接网卡");
            return;
        }
        var ip = sel.Ip.Equals(IPAddress.Any) ? GetLanIp() : sel.Ip.ToString();
        try
        {
            System.Windows.Clipboard.SetText(ip);
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

        var ip = GetSelectedIp();
        if (ip == null)
        {
            AppendLog("当前网卡未连接，无法监听，请选择已连接网卡或“自动”");
            return;
        }

        try
        {
            _server.Start(ip, httpPort, socksPort);
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

    private void ChkTray_Toggled(object sender, RoutedEventArgs e)
    {
        // 勾选后立即显示托盘图标；取消勾选则隐藏
        if (ChkTray.IsChecked == true)
            EnsureTrayIcon();
        else if (_notifyIcon != null)
            _notifyIcon.Visible = false;
    }

    // ================= 托盘 =================

    protected override void OnClosing(CancelEventArgs e)
    {
        if (ChkTray.IsChecked == true && !_allowClose)
        {
            // 托盘模式：关闭主界面 -> 隐藏到托盘继续运行
            e.Cancel = true;
            EnsureTrayIcon();
            Hide();
            _notifyIcon?.ShowBalloonTip(1500, "LanProxy", "已最小化到托盘，双击图标恢复主界面", System.Windows.Forms.ToolTipIcon.Info);
            return;
        }
        _notifyIcon?.Dispose();
        _notifyIcon = null;
        base.OnClosing(e);
    }

    private void EnsureTrayIcon()
    {
        if (_notifyIcon != null) { _notifyIcon.Visible = true; return; }
        _notifyIcon = new System.Windows.Forms.NotifyIcon();
        try
        {
            var sri = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
            if (sri != null) _notifyIcon.Icon = new System.Drawing.Icon(sri.Stream);
        }
        catch
        {
            _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
        }
        _notifyIcon.Text = "LanProxy 局域网代理服务器";
        _notifyIcon.Visible = true;
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示主界面", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        _notifyIcon.ContextMenuStrip = menu;
    }

    private void ShowMainWindow()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
    }

    private void ExitApp()
    {
        _allowClose = true;
        Dispatcher.Invoke(Close);
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

/// <summary>网卡下拉项数据</summary>
public class AdapterItem
{
    public string Display { get; set; } = "";
    public string SubLine { get; set; } = "";
    public IPAddress? Ip { get; set; }
    public bool IsUp { get; set; }
}
