# LanProxy 局域网代理服务器

免费、开源（MIT）、Windows 免安装、中文图形界面的 HTTP / SOCKS5 局域网共享代理服务器。
纯本地转发，不含任何远程代理、订阅、翻墙逻辑。

## 功能

- 网卡下拉：与系统「网络连接」面板（ncpa.cpl）同一套过滤规则，只列出用户可见的网卡；显示连接名（粗体）+ 型号/IP（灰字），虚拟网卡标注「虚拟」，未连接灰色；默认监听所有网卡 0.0.0.0
- HTTP 端口、SOCKS5 端口可自定义（默认 8080 / 1080）
- 【复制当前网卡 IP】一键复制当前选中网卡的 IP 到剪贴板
- 启动 / 停止代理
- 实时流量曲线（上行/下行 KB/s，保留最近 60 秒）
- 客户端连接列表（客户端 IP、协议、已上传、已下载）
- 运行日志面板（可勾选隐藏）
- **托盘显示**：勾选后关闭主界面，程序最小化到系统托盘继续运行（双击托盘图标恢复，托盘右键可退出）
- 状态栏显示总上下行流量
- 程序自带图标（exe / 窗口 / 任务栏 / 托盘）

## 使用方法

1. 编译得到 `LanProxy.exe` 后双击启动（无需安装，Windows 10/11 直接运行；编译方法见下方「源码与重新编译」）。
2. 点【启动代理】。
3. 在其他设备（手机/平板/电脑）的 Wi-Fi 代理设置中填写：
   - 代理地址：本机局域网 IP（【复制当前网卡 IP】一键复制）
   - 端口：8080（HTTP）或 1080（SOCKS5）
4. 停止时点【停止代理】。

> 托盘：勾选左侧「托盘显示」后，点窗口右上角关闭按钮不会退出程序，而是最小化到系统托盘继续代理；双击托盘图标恢复主界面，托盘右键菜单可「退出」。

> 本机自测：浏览器代理设置里填 `127.0.0.1:8080` 也可使用本机代理。

### 防火墙放行（局域网设备连不上时必做）

Windows 防火墙默认会拦截入站连接。放行方法：

```powershell
# 以管理员身份运行 PowerShell
netsh advfirewall firewall add rule name="LanProxy HTTP" dir=in action=allow protocol=TCP localport=8080
netsh advfirewall firewall add rule name="LanProxy SOCKS" dir=in action=allow protocol=TCP localport=1080
```

### 命令行快捷启动（可选）

```powershell
LanProxy.exe --autostart 8080 1080
```

启动后自动监听并代理，日志同时写入同目录 `lanproxy-autostart.log`。

## 源码与重新编译

源码位于 `LanProxy` 目录（WPF / C# / .NET 9，依赖 OxyPlot.Wpf）。

重新编译：

```powershell
# 需要 .NET 9 SDK
cd LanProxy
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

产物在 `bin\Release\net9.0-windows\win-x64\publish\LanProxy.exe`。

## 代码结构

```
LanProxy/
├── App.xaml / App.xaml.cs        # 应用入口
├── MainWindow.xaml               # 界面（仿 CCProxy 布局）
├── MainWindow.xaml.cs            # 界面逻辑、流量曲线、连接列表
└── Proxy/
    ├── ProxyServer.cs            # HTTP + SOCKS5 代理核心（自研 Socket 实现）
    └── ClientSession.cs          # 会话流量统计
```

## 许可证

MIT License，代码为原创，可自由使用、修改、分发。
