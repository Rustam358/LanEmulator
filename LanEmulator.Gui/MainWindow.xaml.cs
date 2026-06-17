using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
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

    public MainWindow()
    {
        InitializeComponent();
        // Start LAN discovery in background
        _ = DiscoverLanServersAsync();
    }

    // ════════════════════════════════════════════════════════
    // Window chrome
    // ════════════════════════════════════════════════════════

    private void TitleBar_Drag(object s, MouseButtonEventArgs e)
    { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }

    private void BtnMinimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void BtnClose_Click(object s, RoutedEventArgs e) => Application.Current.Shutdown();

    // ════════════════════════════════════════════════════════
    // Page navigation
    // ════════════════════════════════════════════════════════

    private void ShowWelcome() { WelcomePage.Visibility = Visibility.Visible; LobbyPage.Visibility = Visibility.Collapsed; }
    private void ShowLobby() { WelcomePage.Visibility = Visibility.Collapsed; LobbyPage.Visibility = Visibility.Visible; }

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
            _engine.OnLog += OnEngineLog;
            _engine.OnStateChanged += OnStateChanged;
            _engine.OnPeerJoined += OnPeerJoined;
            _engine.OnPeerLeft += OnPeerLeft;
            _engine.OnRoomCreated += OnRoomCreated;
            _chatTimer.Tick += ChatTimer_Tick;
            _chatTimer.Start();

            // Show lobby
            ShowLobby();
            TxtLobbyStatus.Text = "Starting server…";
            TxtLobbyRoom.Text = $"Room: {_engine.RoomId}";

            // Connect + VPN
            _engine.Configure(1, _engine.RoomId, gamePath);
            await _engine.ConnectAsync(_engine.ServerUrl);
            await _engine.RunGoldbergAsync();
            _engine.StartVpn();
            _engine.LaunchGame();

            OnStateChanged("running", _engine.MyVirtualIP);
            AddPlayerToList($"[{_engine.MyVirtualIP}] {Environment.MachineName} (you) \U0001F451", true);
        }
        catch (Exception ex)
        {
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

            _engine.Configure(1, roomDlg.Answer, gamePath);
            _engine.OnLog += OnEngineLog;
            _engine.OnStateChanged += OnStateChanged;
            _engine.OnPeerJoined += OnPeerJoined;
            _engine.OnPeerLeft += OnPeerLeft;
            _engine.OnRoomCreated += OnRoomCreated;
            _chatTimer.Tick += ChatTimer_Tick;
            _chatTimer.Start();

            ShowLobby();
            TxtLobbyStatus.Text = "Connecting…";
            TxtLobbyRoom.Text = $"Room: {roomDlg.Answer}";

            await _engine.ConnectAsync(serverUrl);
            await _engine.RunGoldbergAsync();
            _engine.StartVpn();
            _engine.LaunchGame();

            OnStateChanged("running", _engine.MyVirtualIP);
            AddPlayerToList($"[{_engine.MyVirtualIP}] {Environment.MachineName} (you)", false);
        }
        catch (Exception ex)
        {
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

    private void BtnLaunch_Click(object s, RoutedEventArgs e) => _engine.LaunchGame();

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
        var item = new TextBlock
        {
            Text = $"[{time}] {player}: {text}",
            Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 1, 0, 1)
        };
        LstChat.Items.Add(item);
        LstChat.ScrollIntoView(LstChat.Items[^1]);
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
        await _engine.SendChatAsync(text);
    }

    // ════════════════════════════════════════════════════════
    // Disconnect
    // ════════════════════════════════════════════════════════

    private async void BtnDisconnect_Click(object s, RoutedEventArgs e)
    {
        await _engine.ShutdownAsync();
        ShowWelcome();
        ResetWelcomeButtons();
        LstPlayers.Items.Clear();
        LstChat.Items.Clear();
        LstLog.Items.Clear();
        TxtDiscovery.Text = "\U0001F50D  Scanning LAN for servers…";
        _ = DiscoverLanServersAsync();
    }

    private async void Window_Closing(object? s, CancelEventArgs e)
    {
        if (_engine.IsRunning)
        {
            e.Cancel = true;
            await _engine.ShutdownAsync();
            Application.Current.Shutdown();
        }
    }

    // ════════════════════════════════════════════════════════
    // Engine events
    // ════════════════════════════════════════════════════════

    private void OnEngineLog(LogEntry e) => Dispatcher.Invoke(() =>
    {
        var brush = e.Level switch
        {
            LogLevel.Error => new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)),
            LogLevel.Warn => new SolidColorBrush(Color.FromRgb(0xF9, 0xE2, 0xAF)),
            LogLevel.Ok => new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)),
            LogLevel.Info => new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)),
            LogLevel.PeerJoin => new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)),
            LogLevel.PeerLeft => new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)),
            _ => new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4))
        };
        var item = new TextBlock
        {
            Text = $"{e.Time:HH:mm:ss}  {e.Message}",
            Foreground = brush,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Margin = new Thickness(0, 1, 0, 1)
        };
        LstLog.Items.Add(item);
        LstLog.ScrollIntoView(LstLog.Items[^1]);
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

    /// <summary>Log a message before engine starts (welcome page).</summary>
    private void LogStatic(LogLevel level, string msg) => Dispatcher.Invoke(() =>
    {
        var brush = level switch
        {
            LogLevel.Error => new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)),
            LogLevel.Warn => new SolidColorBrush(Color.FromRgb(0xF9, 0xE2, 0xAF)),
            LogLevel.Ok => new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)),
            LogLevel.Info => new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)),
            _ => new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4))
        };
        var item = new TextBlock
        {
            Text = $"{DateTime.Now:HH:mm:ss}  {msg}",
            Foreground = brush,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Margin = new Thickness(0, 1, 0, 1)
        };
        LstLog.Items.Add(item);
        LstLog.ScrollIntoView(LstLog.Items[^1]);
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
