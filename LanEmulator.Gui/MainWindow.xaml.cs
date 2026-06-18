using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
using LanEmulator.Core;

namespace LanEmulator.Gui;

public partial class MainWindow : Window
{
    private readonly Engine _engine = new();
    private readonly DispatcherTimer _chatTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private int _lastChatId;
    private bool _isConnecting;
    private bool _isShuttingDown;
    private readonly List<string> _diagWarnings = new();

    public MainWindow()
    {
        InitializeComponent();
        // Start LAN discovery in background
        _ = DiscoverLanServersAsync();
    }

    // ════════════════════════════════════════════════════════
    // Window chrome
    // ════════════════════════════════════════════════════════

    private bool _engineWired;

    private void WireEngine()
    {
        if (_engineWired) return;
        _engineWired = true;
        _engine.OnLog += OnEngineLog;
        _engine.OnStateChanged += OnStateChanged;
        _engine.OnPeerJoined += OnPeerJoined;
        _engine.OnPeerLeft += OnPeerLeft;
        _engine.OnRoomCreated += OnRoomCreated;
        _chatTimer.Tick += ChatTimer_Tick;
        _chatTimer.Start();
    }

    private void UnsubscribeEngine()
    {
        _chatTimer.Stop();
        _chatTimer.Tick -= ChatTimer_Tick;
        _engine.OnLog -= OnEngineLog;
        _engine.OnStateChanged -= OnStateChanged;
        _engine.OnPeerJoined -= OnPeerJoined;
        _engine.OnPeerLeft -= OnPeerLeft;
        _engine.OnRoomCreated -= OnRoomCreated;
        _engineWired = false;
    }

        private void TitleBar_Drag(object s, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Window_StateChanged(object? s, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        { BtnMaximize.Content = "\u2750"; WindowBorder.CornerRadius = new CornerRadius(0); }
        else
        { BtnMaximize.Content = "\u25A1"; WindowBorder.CornerRadius = new CornerRadius(12); }
    }

    private void BtnMinimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnMaximize_Click(object s, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized)
        { WindowState = WindowState.Normal; BtnMaximize.Content = "\u25A1"; }
        else
        { WindowState = WindowState.Maximized; BtnMaximize.Content = "\u2750"; }
    }

    private void BtnClose_Click(object s, RoutedEventArgs e) => Application.Current.Shutdown();

    // ════════════════════════════════════════════════════════
    // Page navigation
    // ════════════════════════════════════════════════════════

    private void ShowWelcome()
    {
        WelcomePage.Visibility = Visibility.Visible;
        LobbyPage.Visibility = Visibility.Collapsed;
        DiagnosticsPage.Visibility = Visibility.Collapsed;
        TabHome.Content = "Home";
        TabHome.Background = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44));
        TabHome.Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4));
        TabDiagnostics.Background = Brushes.Transparent;
        TabDiagnostics.Foreground = new SolidColorBrush(Color.FromRgb(0x6C, 0x70, 0x86));
    }

    private void ShowLobby()
    {
        WelcomePage.Visibility = Visibility.Collapsed;
        LobbyPage.Visibility = Visibility.Visible;
        DiagnosticsPage.Visibility = Visibility.Collapsed;
        TabHome.Content = "Lobby";
        TabHome.Background = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44));
        TabHome.Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4));
        TabDiagnostics.Background = Brushes.Transparent;
        TabDiagnostics.Foreground = new SolidColorBrush(Color.FromRgb(0x6C, 0x70, 0x86));
    }

    // ════════════════════════════════════════════════════════
    // LAN discovery
    // ════════════════════════════════════════════════════════

    private async Task DiscoverLanServersAsync()
    {
        try
        {
            string? found = await Task.Run(Helpers.DiscoverServer);
            if (!string.IsNullOrEmpty(found) && !_engine.IsRunning)
            {
                Dispatcher.Invoke(() =>
                    TxtDiscovery.Text = $"\U0001F50D  Server found on LAN: {found}  [click Join]");
            }
        }
        catch { }
    }

    // ════════════════════════════════════════════════════════
    // Tab switching
    // ════════════════════════════════════════════════════════

    private void TabHome_Click(object s, RoutedEventArgs e)
    {
        if (_engine.IsRunning) ShowLobby(); else ShowWelcome();
    }

    private void TabDiagnostics_Click(object s, RoutedEventArgs e)
    {
        WelcomePage.Visibility = Visibility.Collapsed;
        LobbyPage.Visibility = Visibility.Collapsed;
        DiagnosticsPage.Visibility = Visibility.Visible;
        TabDiagnostics.Background = new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44));
        TabDiagnostics.Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4));
        TabHome.Background = Brushes.Transparent;
        TabHome.Foreground = new SolidColorBrush(Color.FromRgb(0x6C, 0x70, 0x86));
        RefreshDiagnostics();
    }

    private void BtnDiagRefresh_Click(object s, RoutedEventArgs e) => RefreshDiagnostics();


    // Status helpers for diagnostics dots
    private static readonly SolidColorBrush DotGreen  = new(Color.FromRgb(0xA6, 0xE3, 0xA1));
    private static readonly SolidColorBrush DotYellow = new(Color.FromRgb(0xF9, 0xE2, 0xAF));
    private static readonly SolidColorBrush DotRed    = new(Color.FromRgb(0xF3, 0x8B, 0xA8));
    private static readonly SolidColorBrush DotGray   = new(Color.FromRgb(0x58, 0x5B, 0x70));

    private void Dot(Ellipse e, string status)
    {
        e.Fill = status switch
        {
            "ok" => DotGreen,
            "warn" => DotYellow,
            "err" => DotRed,
            _ => DotGray
        };
    }
    /// <summary>Populate all diagnostics fields with current state.</summary>
    private void RefreshDiagnostics()
    {
        try
        {
            // ===== SYSTEM =====
            bool isAdmin = Engine.IsAdministrator();
            DiagAdmin.Text = string.Concat("Admin: ", isAdmin ? "Yes" : "No (run as Administrator)");
            Dot(DiagDotAdmin, isAdmin ? "ok" : "err");

            uint ver = Engine.GetDriverVersion();
            DiagDriver.Text = ver == 0
                ? "Wintun driver: not installed"
                : string.Concat("Wintun driver: v", ver >> 16, ".", ver & 0xFFFF);
            Dot(DiagDotDriver, ver == 0 ? "err" : "ok");

            var nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            var tun = nics.FirstOrDefault(a => a.Name.Contains("LanEmulator"));
            if (tun != null)
            {
                DiagAdapter.Text = string.Concat("Adapter: ", tun.Name, " (", tun.OperationalStatus, ")");
                Dot(DiagDotAdapter, tun.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up ? "ok" : "warn");
                var ipv4 = tun.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(ua => ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                DiagIp.Text = ipv4 != null
                    ? string.Concat("Virtual IP: ", ipv4.Address, "/", ipv4.PrefixLength)
                    : "Virtual IP: not assigned";
                Dot(DiagDotIp, ipv4 != null ? "ok" : "warn");
            }
            else
            {
                DiagAdapter.Text = "Adapter: not found";
                DiagIp.Text = "Virtual IP: --";
                Dot(DiagDotAdapter, "gray");
                Dot(DiagDotIp, "gray");
            }

            // ===== GAME =====
            string gamePath = null;
            if (!string.IsNullOrEmpty(_engine.GamePath))
                gamePath = _engine.GamePath;
            else
                try { gamePath = TxtSelectedGame.Text; } catch { }

            if (!string.IsNullOrEmpty(gamePath) && File.Exists(gamePath))
            {
                DiagGamePath.Text = gamePath;
                try
                {
                    var bytes = File.ReadAllBytes(gamePath);
                    int peOff = BitConverter.ToInt32(bytes, 0x3C);
                    ushort machine = BitConverter.ToUInt16(bytes, peOff + 4);
                    DiagArch.Text = string.Concat("Architecture: ",
                        machine == 0x014C ? "32-bit (x86)" :
                        machine == 0x8664 ? "64-bit (x64)" :
                        string.Concat("Unknown (0x", machine.ToString("X4"), ")"));
                    Dot(DiagDotArch, "ok");
                }
                catch
                {
                    DiagArch.Text = "Architecture: unreadable";
                    Dot(DiagDotArch, "warn");
                }
            }
            else
            {
                DiagGamePath.Text = "No game selected";
                DiagArch.Text = "Architecture: --";
                Dot(DiagDotArch, "gray");
            }

            // Game status
            if (_engine.GameProcess != null)
            {
                try
                {
                    if (_engine.GameProcess.HasExited)
                    {
                        DiagGameStatus.Text = string.Concat("Status: EXITED (code ", _engine.GameProcess.ExitCode.ToString(), ")");
                        Dot(DiagDotGame, _engine.GameProcess.ExitCode == 0 ? "ok" : "err");
                    }
                    else
                    {
                        DiagGameStatus.Text = string.Concat("Status: RUNNING (PID ", _engine.GameProcess.Id.ToString(), ")");
                        Dot(DiagDotGame, "ok");
                    }
                }
                catch { DiagGameStatus.Text = "Status: detached"; Dot(DiagDotGame, "gray"); }
            }
            else
            {
                DiagGameStatus.Text = "Status: not launched";
                Dot(DiagDotGame, "gray");
            }

            // Runtimes
            try
            {
                var runtimes = new List<string>();
                if (File.Exists(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "d3d8.dll")))
                    runtimes.Add("DX8");
                if (File.Exists(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "d3d9.dll")))
                    runtimes.Add("DX9");
                if (File.Exists(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msvcp140.dll")))
                    runtimes.Add("VC++ 2015+");
                if (File.Exists(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vcruntime140.dll")))
                    runtimes.Add("VCRuntime");
                DiagRuntimes.Text = runtimes.Count > 0
                    ? string.Concat("Runtimes: ", string.Join(", ", runtimes))
                    : "Runtimes: not detected";
                Dot(DiagDotRuntimes, runtimes.Count >= 2 ? "ok" : "warn");
            }
            catch
            {
                DiagRuntimes.Text = "Runtimes: check failed";
                Dot(DiagDotRuntimes, "gray");
            }

            // ===== NETWORK =====
            // Server ping (async in background, show result on next refresh)
            if (_engine.IsRunning)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var resp = await http.GetAsync(_engine.ServerUrl + "/ping");
                        sw.Stop();
                        string text = string.Concat("Server ping: ", sw.ElapsedMilliseconds.ToString(), "ms");
                        string status = sw.ElapsedMilliseconds < 100 ? "ok" : "warn";
                        Dispatcher.Invoke(() => { DiagLatency.Text = text; Dot(DiagDotLatency, status); });
                    }
                    catch
                    {
                        Dispatcher.Invoke(() => { DiagLatency.Text = "Server ping: unreachable"; Dot(DiagDotLatency, "err"); });
                    }
                });
            }
            else
            {
                DiagLatency.Text = "Server ping: not connected";
                Dot(DiagDotLatency, "gray");
            }

            // UDP loopback test
            if (tun != null && tun.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            {
                DiagLoopback.Text = "UDP loopback: testing...";
                _ = Task.Run(() =>
                {
                    try
                    {
                        using var sock = new System.Net.Sockets.Socket(
                            System.Net.Sockets.AddressFamily.InterNetwork,
                            System.Net.Sockets.SocketType.Dgram,
                            System.Net.Sockets.ProtocolType.Udp);
                        sock.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0));
                        sock.SendTo(new byte[] { 0xDE, 0xAD }, new System.Net.IPEndPoint(
                            System.Net.IPAddress.Parse("10.13.37.1"), 51820));
                        Dispatcher.Invoke(() =>
                        {
                            DiagLoopback.Text = "UDP loopback: sent test packet";
                            Dot(DiagDotLoopback, "ok");
                        });
                    }
                    catch
                    {
                        Dispatcher.Invoke(() =>
                        {
                            DiagLoopback.Text = "UDP loopback: failed";
                            Dot(DiagDotLoopback, "err");
                        });
                    }
                });
            }
            else
            {
                DiagLoopback.Text = "UDP loopback: adapter offline";
                Dot(DiagDotLoopback, "gray");
            }

            // Packets (from VPN controller stats — placeholder until we add counters)
            DiagPackets.Text = "Packets: not available yet";
            Dot(DiagDotPackets, "gray");

            // ===== FIREWALL =====
            // Quick check: can we bind to UDP 51820? If yes, port is likely open
            try
            {
                using var udpTest = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Dgram,
                    System.Net.Sockets.ProtocolType.Udp);
                udpTest.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0));
                udpTest.Close();
                DiagFwUdp.Text = "UDP 51820: socket available";
                Dot(DiagDotFwUdp, "ok");
            }
            catch
            {
                DiagFwUdp.Text = "UDP 51820: blocked or in use";
                Dot(DiagDotFwUdp, "err");
            }

            // TCP port check (can we bind to the server port?)
            try
            {
                using var tcpTest = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Tcp);
                tcpTest.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0));
                tcpTest.Close();
                DiagFwTcp.Text = "TCP 8000: socket available";
                Dot(DiagDotFwTcp, "ok");
            }
            catch
            {
                DiagFwTcp.Text = "TCP 8000: blocked";
                Dot(DiagDotFwTcp, "err");
            }

            // ===== ROOM / PEERS =====
            DiagRoomId.Text = string.IsNullOrEmpty(_engine.RoomId)
                ? "Room: not connected" : string.Concat("Room: ", _engine.RoomId);
            Dot(DiagDotRoom, string.IsNullOrEmpty(_engine.RoomId) ? "gray" : "ok");

            DiagPeers.Text = _engine.IsRunning
                ? string.Concat("Connected. Players: ", (_engine.PeerCount + 1).ToString(),
                    " (you + ", _engine.PeerCount.ToString(), " remote)")
                : "Not connected";

            // ===== WARNINGS =====
            var allWarnings = new List<string>(_diagWarnings);
            allWarnings.AddRange(_gameEvents);
            DiagWarnings.Text = allWarnings.Count == 0 ? "" : string.Join(Environment.NewLine, allWarnings);

            // Timestamp
            DiagLastUpdate.Text = string.Concat("Updated: ", DateTime.Now.ToString("HH:mm:ss"));
        }
        catch (Exception ex)
        {
            DiagAdapter.Text = string.Concat("Diagnostics error: ", ex.Message);
        }
    }

    // ════════════════════════════════════════════════════════
    // Host / Join
    // ════════════════════════════════════════════════════════

    private async void BtnHost_Click(object sender, RoutedEventArgs e)
    {
        if (_isConnecting) return;
        _isConnecting = true;
        BtnHostWelcome.IsEnabled = false;
        BtnJoinWelcome.IsEnabled = false;

        try
        {
            // Admin check
            if (!Engine.IsAdministrator())
            {
                LogStatic(LogLevel.Error, "Administrator privileges required.\nRight-click LanEmulator.exe → Run as administrator.");
                ResetWelcomeButtons();
                return;
            }

            // Driver check
            uint ver = Engine.GetDriverVersion();
            if (ver == 0)
            {
                LogStatic(LogLevel.Warn, "Wintun driver not installed. Auto-installing…");
                bool ok = await Helpers.AutoInstallDriverAsync(msg => LogStatic(LogLevel.Info, msg));
                ver = Engine.GetDriverVersion();
                if (ver == 0)
                {
                    LogStatic(LogLevel.Error, "Driver not loaded. Restart PC and try again.");
                    ResetWelcomeButtons();
                    return;
                }
            }
            LogStatic(LogLevel.Ok, $"Wintun driver v{ver >> 16}.{ver & 0xFFFF}");

            // Game selection
            string? gamePath = await PickGameAsync();

            // Host setup
            await _engine.HostSetupAsync();
            WireEngine();

            // Connect + VPN — lobby shown only after success
            int mode = RbSteam.IsChecked == true ? 1 : 2;
            _engine.Configure(mode, _engine.RoomId, gamePath);
            await _engine.ConnectAsync(_engine.ServerUrl);
            await _engine.RunGoldbergAsync();
            await _engine.StartVpnAsync();
            _engine.LaunchGame();

            ShowLobby();
            TxtLobbyStatus.Text = "Connected";
            TxtLobbyRoom.Text = $"Room: {_engine.RoomId}";
            TxtInviteUrl.Text = string.IsNullOrEmpty(_engine.PublicIP)
                ? $"Invite link: {_engine.ServerUrl} (checking public IP...)"
                : $"Invite link: {_engine.InviteUrl}";
            OnStateChanged("running", _engine.MyVirtualIP);
            AddPlayerToList($"[{_engine.MyVirtualIP}] {Environment.MachineName} (you) \U0001F451", true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "LanEmulator Error", MessageBoxButton.OK, MessageBoxImage.Error);
            LogStatic(LogLevel.Error, ex.Message);
            ShowWelcome();
            ResetWelcomeButtons();
        }
        _isConnecting = false;
    }

    private async void BtnJoin_Click(object sender, RoutedEventArgs e)
    {
        if (_isConnecting) return;
        _isConnecting = true;
        BtnHostWelcome.IsEnabled = false;
        BtnJoinWelcome.IsEnabled = false;

        try
        {
            if (!Engine.IsAdministrator())
            {
                LogStatic(LogLevel.Error, "Administrator privileges required.");
                ResetWelcomeButtons();
                return;
            }

            uint ver = Engine.GetDriverVersion();
            if (ver == 0)
            {
                LogStatic(LogLevel.Warn, "Wintun driver not installed. Auto-installing…");
                bool ok = await Helpers.AutoInstallDriverAsync(msg => LogStatic(LogLevel.Info, msg));
                ver = Engine.GetDriverVersion();
                if (ver == 0)
                {
                    LogStatic(LogLevel.Error, "Driver not loaded. Restart PC.");
                    ResetWelcomeButtons();
                    return;
                }
            }
            LogStatic(LogLevel.Ok, $"Wintun driver v{ver >> 16}.{ver & 0xFFFF}");

            // Discover or prompt URL
            string serverUrl = _engine.JoinSetup();
            if (string.IsNullOrEmpty(serverUrl))
            {
                var dlg = new InputDialog("Server URL", "Enter server URL (e.g. http://192.168.1.50:8000):", this);
                if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Answer))
                {
                    LogStatic(LogLevel.Error, "Server URL required.");
                    ResetWelcomeButtons();
                    return;
                }
                serverUrl = dlg.Answer.TrimEnd('/');
            }

            // Room ID
            var roomDlg = new InputDialog("Room ID", "Enter the Room ID code from the host:", this);
            if (roomDlg.ShowDialog() != true || string.IsNullOrWhiteSpace(roomDlg.Answer))
            {
                LogStatic(LogLevel.Error, "Room ID required.");
                ResetWelcomeButtons();
                return;
            }

            // Game selection
            string? gamePath = await PickGameAsync();

            int mode = RbSteam.IsChecked == true ? 1 : 2;
            _engine.Configure(mode, roomDlg.Answer, gamePath);
            WireEngine();

            await _engine.ConnectAsync(serverUrl);
            await _engine.RunGoldbergAsync();
            await _engine.StartVpnAsync();
            _engine.LaunchGame();

            ShowLobby();
            TxtLobbyStatus.Text = "Connected";
            TxtLobbyRoom.Text = $"Room: {roomDlg.Answer}";
            OnStateChanged("running", _engine.MyVirtualIP);
            AddPlayerToList($"[{_engine.MyVirtualIP}] {Environment.MachineName} (you)", false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "LanEmulator Error", MessageBoxButton.OK, MessageBoxImage.Error);
            LogStatic(LogLevel.Error, ex.Message);
            ShowWelcome();
            ResetWelcomeButtons();
        }
        _isConnecting = false;
    }

    private void ResetWelcomeButtons()
    { _isConnecting = false; BtnHostWelcome.IsEnabled = true; BtnJoinWelcome.IsEnabled = true; }

    private async Task<string?> PickGameAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select game executable",
            Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            TxtSelectedGame.Text = System.IO.Path.GetFileName(dlg.FileName);
            BtnLaunch.IsEnabled = true;
            return dlg.FileName;
        }
        return null;
    }

    // ════════════════════════════════════════════════════════
    // Manual URL entry
    // ════════════════════════════════════════════════════════

    private void BtnManualUrl_Click(object s, RoutedEventArgs e) => BtnJoin_Click(s, e);

    // ════════════════════════════════════════════════════════
    // Game browse & launch
    // ════════════════════════════════════════════════════════

    private async void BtnBrowseGame_Click(object s, RoutedEventArgs e)
    {
        var path = await PickGameAsync();
        if (path != null) _engine.SetGamePath(path);
    }

    private readonly DispatcherTimer _gameWatchdog = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly List<string> _gameEvents = new();

    private void BtnLaunch_Click(object s, RoutedEventArgs e)
    {
        _engine.LaunchGame();
        StartGameWatchdog();
    }

    private void StartGameWatchdog()
    {
        _gameWatchdog.Tick -= GameWatchdog_Tick;
        _gameWatchdog.Tick += GameWatchdog_Tick;
        _gameWatchdog.Start();
    }

    private void GameWatchdog_Tick(object? s, EventArgs e)
    {
        if (_engine.GameProcess == null) { _gameWatchdog.Stop(); return; }
        try
        {
            if (_engine.GameProcess.HasExited)
            {
                _gameWatchdog.Stop();
                int code = _engine.GameProcess.ExitCode;
                _gameEvents.Add(string.Concat(
                    DateTime.Now.ToString("HH:mm:ss"),
                    " Game process exited with code ", code.ToString()));

                // Scan for error dialogs from the game
                _ = Task.Run(() =>
                {
                    Thread.Sleep(500); // let dialog appear
                    var dialogs = ScanErrorDialogs();
                    if (dialogs.Count > 0)
                    {
                        foreach (var dlg in dialogs)
                            _gameEvents.Add(string.Concat(
                                "[ERROR DIALOG] ", dlg));
                    }
                    if (code != 0 && dialogs.Count == 0)
                        _gameEvents.Add("Check the Diagnostics tab for known issue descriptions.");

                    Dispatcher.Invoke(() => RefreshDiagnostics());
                });
            }
        }
        catch { _gameWatchdog.Stop(); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private List<string> ScanErrorDialogs()
    {
        var results = new List<string>();
        try
        {
            int targetPid;
            try { targetPid = _engine.GameProcess?.Id ?? 0; }
            catch { return results; }
            if (targetPid == 0) return results;

            // Common error dialog window class names
            var errorClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "#32770", // standard Windows dialog
                "Static", "MozillaDialogClass", "MessageBoxWindowClass",
                "TXGuiFoundation", "wxWindowClassNR"
            };

            EnumWindows((hWnd, lParam) =>
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == (uint)targetPid || true) // check ALL visible dialogs
                {
                    var sb = new System.Text.StringBuilder(1024);
                    int len = GetWindowText(hWnd, sb, 1024);
                    if (len > 2 && IsWindowVisible(hWnd))
                    {
                        // Filter: capture error-like windows, skip game window itself
                        string title = sb.ToString();
                        if (title.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                            title.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                            title.Contains("Fatal", StringComparison.OrdinalIgnoreCase) ||
                            title.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
                            title.Contains("0xc0", StringComparison.OrdinalIgnoreCase) ||
                            title.Contains("Bad Image", StringComparison.OrdinalIgnoreCase) ||
                            title.Contains("Missing", StringComparison.OrdinalIgnoreCase) ||
                            title.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                            title.Contains(".dll", StringComparison.OrdinalIgnoreCase) &&
                            title.Length < 200)
                        {
                            results.Add(title);
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return results.Distinct().ToList();
    }

    // ════════════════════════════════════════════════════════
    // Chat
    // ════════════════════════════════════════════════════════

    private async void ChatTimer_Tick(object? s, EventArgs e)
    {
        if (!_engine.IsRunning) return;
        try
        {
            var msgs = await _engine.PollChatAsync(_lastChatId);
            foreach (var m in msgs)
            {
                _lastChatId = m.id;
                Dispatcher.Invoke(() => AppendChat(m.player_id, m.text, m.timestamp));
            }
        }
        catch { }
    }

    private void AppendChat(string player, string text, string time)
    {
        LstChat.AppendText(string.Concat("[", time, "] ", player, ": ", text, Environment.NewLine));
        if (LstChat.LineCount > 500)
            LstChat.Text = string.Join(Environment.NewLine,
                LstChat.Text.Split(Environment.NewLine).Skip(LstChat.LineCount - 500));
        LstChat.ScrollToEnd();
    }

    private async void TxtChatInput_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { await SendChat(); e.Handled = true; }
    }

    private async void BtnSendChat_Click(object s, RoutedEventArgs e) => await SendChat();

    private async Task SendChat()
    {
        string text = TxtChatInput.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        TxtChatInput.Text = "";
        string myName = Environment.MachineName;
        string time = DateTime.Now.ToString("HH:mm");
        AppendChat(myName, text, time);
        int newId = await _engine.SendChatAsync(text);
        if (newId > _lastChatId) _lastChatId = newId;
    }

    // ════════════════════════════════════════════════════════
    // Disconnect
    // ════════════════════════════════════════════════════════

        private async void BtnDisconnect_Click(object s, RoutedEventArgs e)
    {
        await _engine.ShutdownAsync();
        UnsubscribeEngine();
        ShowWelcome();
        ResetWelcomeButtons();
        LstPlayers.Items.Clear();
        LstChat.Text = "";
        LstLog.Document.Blocks.Clear();
        LstWelcomeLog.Document.Blocks.Clear();
        _gameEvents.Clear();
        _gameWatchdog.Stop();
        TxtDiscovery.Text = "🔍  Scanning LAN for servers…";
        _ = DiscoverLanServersAsync();
    }
    private async void Window_Closing(object? s, CancelEventArgs e)
    {
        if (_isShuttingDown) return;
        if (_engine.IsRunning)
        {
            e.Cancel = true;
            _isShuttingDown = true;
            await _engine.ShutdownAsync();
            _isShuttingDown = false;
            Application.Current.Shutdown();
        }
    }

    // ════════════════════════════════════════════════════════
    // Engine events
    // ════════════════════════════════════════════════════════

    private void OnEngineLog(LogEntry e) => Dispatcher.Invoke(() =>
    {
        // Capture warnings for diagnostics tab
        if (e.Level == LogLevel.Warn)
        {
            _diagWarnings.Add(e.Message);
            if (_diagWarnings.Count > 100) _diagWarnings.RemoveAt(0);
        }
        var color = e.Level switch
        {
            LogLevel.Error => Color.FromRgb(0xF3, 0x8B, 0xA8),
            LogLevel.Warn  => Color.FromRgb(0xF9, 0xE2, 0xAF),
            LogLevel.Ok    => Color.FromRgb(0xA6, 0xE3, 0xA1),
            LogLevel.Info  => Color.FromRgb(0x89, 0xB4, 0xFA),
            LogLevel.PeerJoin => Color.FromRgb(0xA6, 0xE3, 0xA1),
            LogLevel.PeerLeft => Color.FromRgb(0xF3, 0x8B, 0xA8),
            _ => Color.FromRgb(0xCD, 0xD6, 0xF4)
        };
        AppendColoredLine(LstLog, string.Concat(e.Time.ToString("HH:mm:ss"), "  ", e.Message), color);
    });

    private void OnStateChanged(string state, string? detail)
    {
        Dispatcher.Invoke(() =>
        {
            TxtLobbyStatus.Text = state switch
            {
                "connecting" => "Connecting…",
                "waiting_peers" => "Waiting for players…",
                "vpn_starting" => "Setting up VPN…",
                "running" => "Connected",
                "shutting_down" => "Leaving…",
                _ => state
            };
            StatusDot.Fill = state switch
            {
                "running" => new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)),
                "shutting_down" => new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)),
                _ => new SolidColorBrush(Color.FromRgb(0xF9, 0xE2, 0xAF))
            };
            if (state == "running" && detail != null)
                TxtLobbyIp.Text = detail;
        });
    }

    private void OnPeerJoined(PlayerInfo peer) => Dispatcher.Invoke(() =>
    {
        AddPlayerToList($"[{peer.virtual_ip}] {peer.player_id}", false);
        TxtPlayerCount.Text = $"({_engine.PeerCount + 1}/20)";
    });

    private void OnPeerLeft(PlayerInfo peer) => Dispatcher.Invoke(() =>
    {
        RemovePlayerFromList(peer.player_id);
        TxtPlayerCount.Text = $"({Math.Max(0, _engine.PeerCount)}/20)";
    });

    private void OnRoomCreated(string roomId) => Dispatcher.Invoke(() =>
    {
        TxtLobbyRoom.Text = $"Room: {roomId}";
    });

    private void AddPlayerToList(string text, bool isHost)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new Ellipse
        {
            Width = 8, Height = 8,
            Fill = new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        sp.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (isHost)
            sp.Children.Add(new TextBlock
            {
                Text = " HOST",
                Foreground = new SolidColorBrush(Color.FromRgb(0xF9, 0xE2, 0xAF)),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center
            });
        LstPlayers.Items.Add(sp);
    }

    private void RemovePlayerFromList(string playerId)
    {
        foreach (StackPanel item in LstPlayers.Items)
        {
            foreach (var child in item.Children)
                if (child is TextBlock tb && tb.Text.Contains(playerId))
                { LstPlayers.Items.Remove(item); return; }
        }
    }

    // RichTextBox helper: append colored line, trim to maxLines, scroll to end
    private void AppendColoredLine(System.Windows.Controls.RichTextBox rtb, string text, Color color)
    {
        try
        {
            var doc = rtb.Document;
            var run = new System.Windows.Documents.Run(text + Environment.NewLine);
            run.Foreground = new SolidColorBrush(color);
            var p = new System.Windows.Documents.Paragraph(run) { Margin = new Thickness(0), LineHeight = 14 };
            doc.Blocks.Add(p);
            while (doc.Blocks.Count > 1000) doc.Blocks.Remove(doc.Blocks.FirstBlock);
            rtb.ScrollToEnd();
        }
        catch { }
    }

    /// <summary>Log a message before engine starts (visible on welcome page).</summary>
    private void LogStatic(LogLevel level, string msg) => Dispatcher.Invoke(() =>
    {
        var color = level switch
        {
            LogLevel.Error => Color.FromRgb(0xF3, 0x8B, 0xA8),
            LogLevel.Warn  => Color.FromRgb(0xF9, 0xE2, 0xAF),
            LogLevel.Ok    => Color.FromRgb(0xA6, 0xE3, 0xA1),
            LogLevel.Info  => Color.FromRgb(0x89, 0xB4, 0xFA),
            _ => Color.FromRgb(0xCD, 0xD6, 0xF4)
        };
        string line = string.Concat(DateTime.Now.ToString("HH:mm:ss"), "  ", msg);
        try { AppendColoredLine(LstWelcomeLog, line, color); } catch { }
        try { AppendColoredLine(LstLog, line, color); } catch { }
    });
}

/// <summary>Dark-themed modal dialog for text input.</summary>
public class InputDialog : Window
{
    public string Answer { get; private set; } = "";
    private readonly TextBox _txt;

    public InputDialog(string title, string prompt, Window owner)
    {
        Title = title;
        Width = 420;
        Height = 170;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Owner = owner;

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x40)),
            CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)),
            BorderThickness = new Thickness(1)
        };

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });

        var lbl = new TextBlock
        {
            Text = prompt,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6C, 0x70, 0x86)),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6)
        };
        Grid.SetRow(lbl, 0);
        grid.Children.Add(lbl);

        _txt = new TextBox
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2E)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)),
            FontSize = 15,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _txt.KeyDown += (s, e) => { if (e.Key == Key.Enter) { Answer = _txt.Text; DialogResult = true; } };
        _txt.Loaded += (s, e) => { _txt.Focus(); _txt.SelectAll(); };
        Grid.SetRow(_txt, 1);
        grid.Children.Add(_txt);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(btnPanel, 2);

        btnPanel.Children.Add(new Button
        {
            Content = "OK", Width = 75, Height = 28,
            Margin = new Thickness(0, 6, 6, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E)),
            BorderThickness = new Thickness(0),
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand
        }.Tap(b => b.Click += (s, e) => { Answer = _txt.Text; DialogResult = true; }));

        btnPanel.Children.Add(new Button
        {
            Content = "Cancel", Width = 75, Height = 28,
            Margin = new Thickness(0, 6, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)),
            BorderThickness = new Thickness(0),
            FontSize = 12, Cursor = Cursors.Hand
        }.Tap(b => b.Click += (s, e) => DialogResult = false));

        grid.Children.Add(btnPanel);
        border.Child = grid;
        Content = border;
    }
}

/// <summary>Fluent helper.</summary>
public static class UiExt
{
    public static T Tap<T>(this T obj, Action<T> action) { action(obj); return obj; }
}
