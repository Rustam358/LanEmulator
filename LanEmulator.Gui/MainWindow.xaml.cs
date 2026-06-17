using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using LanEmulator.Core;

namespace LanEmulator.Gui;

public partial class MainWindow : Window
{
    private readonly Engine _engine = new();
    private readonly DispatcherTimer _chatTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private int _lastChatId;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"LanEmulator v{Engine.Version}";

        // Wire engine events
        _engine.OnLog += OnEngineLog;
        _engine.OnStateChanged += OnStateChanged;
        _engine.OnPeerJoined += OnPeerJoined;
        _engine.OnPeerLeft += OnPeerLeft;
        _engine.OnRoomCreated += OnRoomCreated;

        _chatTimer.Tick += ChatTimer_Tick;
        _chatTimer.Start();
    }

    // ═══════════════════════════════════════
    // Engine events → UI
    // ═══════════════════════════════════════

    private void OnEngineLog(LogEntry e)
    {
        Dispatcher.Invoke(() =>
        {
            var brush = e.Level switch
            {
                LogLevel.Error => new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)),
                LogLevel.Warn => new SolidColorBrush(Color.FromRgb(0xF9, 0xE2, 0xAF)),
                LogLevel.Ok => new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)),
                LogLevel.Info => new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)),
                LogLevel.PeerJoin => new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)),
                LogLevel.PeerLeft => new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)),
                LogLevel.Chat => new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)),
                _ => new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4))
            };

            LstLog.Items.Add(new { Time = e.Time.ToString("HH:mm:ss"), e.Message, Color = brush });
            LstLog.ScrollIntoView(LstLog.Items[^1]);
        });
    }

    private void OnStateChanged(string state, string? detail)
    {
        Dispatcher.Invoke(() =>
        {
            TxtStatus.Text = state switch
            {
                "host_setup" => "Starting server…",
                "join_setup" => "Scanning LAN…",
                "connecting" => $"Connecting to {detail}…",
                "waiting_peers" => "Waiting for players…",
                "vpn_starting" => "Setting up VPN…",
                "running" => $"Connected ({detail})",
                "shutting_down" => "Shutting down…",
                _ => state
            };

            StatusDot.Fill = state switch
            {
                "running" => new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)),
                "shutting_down" => new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)),
                _ => new SolidColorBrush(Color.FromRgb(0xF9, 0xE2, 0xAF))
            };

            bool connected = state == "running";
            BtnHost.IsEnabled = !connected;
            BtnJoin.IsEnabled = !connected;
            RbSteam.IsEnabled = !connected;
            RbLan.IsEnabled = !connected;
            TxtGamePath.IsEnabled = !connected;
            BtnBrowse.IsEnabled = !connected;
            BtnDisconnect.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            TxtChatInput.IsEnabled = connected;
            BtnSendChat.IsEnabled = connected;
        });
    }

    private void OnPeerJoined(PlayerInfo peer)
    {
        Dispatcher.Invoke(() =>
        {
            LstPlayers.Items.Add($"[{peer.virtual_ip}] {peer.player_id}");
            if (_engine.IsHost)
                TxtServerUrl.Text = $"Room: {_engine.RoomId}  |  Server: {_engine.ServerUrl}";
        });
    }

    private void OnPeerLeft(PlayerInfo peer)
    {
        Dispatcher.Invoke(() =>
        {
            var item = LstPlayers.Items.Cast<string>()
                .FirstOrDefault(s => s.Contains(peer.player_id));
            if (item != null) LstPlayers.Items.Remove(item);
        });
    }

    private void OnRoomCreated(string roomId)
    {
        Dispatcher.Invoke(() =>
        {
            TxtRoomId.Text = roomId;
            TxtStatus.Text = "Room created — waiting…";
        });
    }

    // ═══════════════════════════════════════
    // Chat timer
    // ═══════════════════════════════════════

    private async void ChatTimer_Tick(object? sender, EventArgs e)
    {
        if (!_engine.IsRunning) return;
        try
        {
            var msgs = await _engine.PollChatAsync(_lastChatId);
            foreach (var m in msgs)
            {
                _lastChatId = m.id;
                Dispatcher.Invoke(() =>
                {
                    var display = $"[{m.timestamp}] {m.player_id}: {m.text}";
                    LstChat.Items.Add(display);
                    LstChat.ScrollIntoView(LstChat.Items[^1]);
                });
            }
        }
        catch { /* network hiccup */ }
    }

    private async void TxtChatInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            await SendChat();
            e.Handled = true;
        }
    }

    private async void BtnSendChat_Click(object sender, RoutedEventArgs e) => await SendChat();

    private async Task SendChat()
    {
        string text = TxtChatInput.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        TxtChatInput.Text = "";
        string myName = Environment.MachineName;
        string time = DateTime.Now.ToString("HH:mm");

        LstChat.Items.Add($"[{time}] {myName}: {text}");
        LstChat.ScrollIntoView(LstChat.Items[^1]);

        await _engine.SendChatAsync(text);
    }

    // ═══════════════════════════════════════
    // Button handlers
    // ═══════════════════════════════════════

    private async void BtnHost_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BtnHost.IsEnabled = false;
            BtnJoin.IsEnabled = false;

            // Check admin
            if (!Engine.IsAdministrator())
            {
                LogStatic(LogLevel.Error, "Administrator privileges required. Restart as admin.");
                BtnHost.IsEnabled = true;
                BtnJoin.IsEnabled = true;
                return;
            }

            // Check Wintun driver
            uint ver = Engine.GetDriverVersion();
            if (ver == 0)
            {
                LogStatic(LogLevel.Warn, "Wintun driver not installed. Attempting auto-install…");
                bool ok = await Helpers.AutoInstallDriverAsync(msg => LogStatic(LogLevel.Info, msg));
                ver = Engine.GetDriverVersion();
                if (ver == 0)
                {
                    LogStatic(LogLevel.Error, "Driver not loaded. Restart PC and try again.");
                    BtnHost.IsEnabled = true;
                    BtnJoin.IsEnabled = true;
                    return;
                }
            }
            LogStatic(LogLevel.Ok, $"Wintun driver v{ver >> 16}.{ver & 0xFFFF}");

            // Host setup
            await _engine.HostSetupAsync();

            // Continue to connect
            await ConnectAndStart();
        }
        catch (Exception ex)
        {
            LogStatic(LogLevel.Error, ex.Message);
            BtnHost.IsEnabled = true;
            BtnJoin.IsEnabled = true;
        }
    }

    private async void BtnJoin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            BtnHost.IsEnabled = false;
            BtnJoin.IsEnabled = false;

            if (!Engine.IsAdministrator())
            {
                LogStatic(LogLevel.Error, "Administrator privileges required.");
                BtnHost.IsEnabled = true;
                BtnJoin.IsEnabled = true;
                return;
            }

            uint ver = Engine.GetDriverVersion();
            if (ver == 0)
            {
                LogStatic(LogLevel.Warn, "Wintun driver not installed. Attempting auto-install…");
                bool ok = await Helpers.AutoInstallDriverAsync(msg => LogStatic(LogLevel.Info, msg));
                ver = Engine.GetDriverVersion();
                if (ver == 0)
                {
                    LogStatic(LogLevel.Error, "Driver not loaded. Restart PC and try again.");
                    BtnHost.IsEnabled = true;
                    BtnJoin.IsEnabled = true;
                    return;
                }
            }
            LogStatic(LogLevel.Ok, $"Wintun driver v{ver >> 16}.{ver & 0xFFFF}");

            // Discover server
            string serverUrl = _engine.JoinSetup();
            if (string.IsNullOrEmpty(serverUrl))
            {
                // Prompt user
                var dialog = new InputDialog("Server URL", "Enter server URL (e.g. http://192.168.1.50:8000):");
                if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Answer))
                {
                    LogStatic(LogLevel.Error, "Server URL required to join.");
                    BtnHost.IsEnabled = true;
                    BtnJoin.IsEnabled = true;
                    return;
                }
                serverUrl = dialog.Answer.TrimEnd('/');
            }

            // Prompt for Room ID
            var roomDialog = new InputDialog("Room ID", "Enter Room ID code:");
            if (roomDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(roomDialog.Answer))
            {
                LogStatic(LogLevel.Error, "Room ID is required.");
                BtnHost.IsEnabled = true;
                BtnJoin.IsEnabled = true;
                return;
            }

            _engine.Configure(
                RbSteam.IsChecked == true ? 1 : 2,
                roomDialog.Answer,
                string.IsNullOrWhiteSpace(TxtGamePath.Text) ? null : TxtGamePath.Text);

            TxtServerUrl.Text = serverUrl;
            await ConnectAndStart();
        }
        catch (Exception ex)
        {
            LogStatic(LogLevel.Error, ex.Message);
            BtnHost.IsEnabled = true;
            BtnJoin.IsEnabled = true;
        }
    }

    private async Task ConnectAndStart()
    {
        // If host, configure + start VPN directly
        if (_engine.IsHost)
        {
            _engine.Configure(
                RbSteam.IsChecked == true ? 1 : 2,
                _engine.RoomId,
                string.IsNullOrWhiteSpace(TxtGamePath.Text) ? null : TxtGamePath.Text);

            await _engine.ConnectAsync(_engine.ServerUrl);
            _engine.RunGoldberg();
            _engine.StartVpn();
            _engine.LaunchGame();

            TxtMyIp.Text = $"My IP: {_engine.MyVirtualIP}";
            TxtServerUrl.Text = $"Room: {_engine.RoomId}  |  Server: {_engine.ServerUrl}";
        }
        else
        {
            await _engine.ConnectAsync(TxtServerUrl.Text);
            _engine.RunGoldberg();
            _engine.StartVpn();
            _engine.LaunchGame();

            TxtMyIp.Text = $"My IP: {_engine.MyVirtualIP}";
            TxtRoomId.Text = _engine.RoomId;
        }

        // Add myself to players list
        LstPlayers.Items.Add($"[{_engine.MyVirtualIP}] {Environment.MachineName} (you)");
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select game executable",
            Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
            TxtGamePath.Text = dialog.FileName;
    }

    private async void BtnDisconnect_Click(object sender, RoutedEventArgs e)
    {
        await _engine.ShutdownAsync();
        ResetUi();
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_engine.IsRunning)
        {
            e.Cancel = true;
            await _engine.ShutdownAsync();
            Application.Current.Shutdown();
        }
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        TxtGamePath.IsEnabled = RbSteam.IsChecked == true && !_engine.IsRunning;
        BtnBrowse.IsEnabled = TxtGamePath.IsEnabled;
    }

    private void ResetUi()
    {
        TxtStatus.Text = "Not connected";
        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xF9, 0xE2, 0xAF));
        TxtRoomId.Text = "";
        TxtServerUrl.Text = "";
        TxtMyIp.Text = "";
        LstPlayers.Items.Clear();
        LstChat.Items.Clear();
        BtnHost.IsEnabled = true;
        BtnJoin.IsEnabled = true;
        BtnDisconnect.Visibility = Visibility.Collapsed;
        TxtChatInput.IsEnabled = false;
        BtnSendChat.IsEnabled = false;
        RbSteam.IsEnabled = true;
        RbLan.IsEnabled = true;
        TxtGamePath.IsEnabled = RbSteam.IsChecked == true;
        BtnBrowse.IsEnabled = TxtGamePath.IsEnabled;
    }

    /// <summary>Log a message directly (before engine is started).</summary>
    private void LogStatic(LogLevel level, string msg)
    {
        Dispatcher.Invoke(() =>
        {
            var brush = level switch
            {
                LogLevel.Error => new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8)),
                LogLevel.Warn => new SolidColorBrush(Color.FromRgb(0xF9, 0xE2, 0xAF)),
                LogLevel.Ok => new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1)),
                LogLevel.Info => new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)),
                _ => new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4))
            };
            LstLog.Items.Add(new { Time = DateTime.Now.ToString("HH:mm:ss"), Message = msg, Color = brush });
            LstLog.ScrollIntoView(LstLog.Items[^1]);
        });
    }
}

/// <summary>Simple modal dialog for text input.</summary>
public class InputDialog : Window
{
    public string Answer { get; private set; } = "";
    private readonly TextBox _txt;

    public InputDialog(string title, string prompt)
    {
        Title = title;
        Width = 400;
        Height = 170;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E));
        Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4));
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize = 13;

        var grid = new Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });

        var lbl = new Label
        {
            Content = prompt,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6C, 0x70, 0x86)),
            FontSize = 12
        };
        Grid.SetRow(lbl, 0);
        grid.Children.Add(lbl);

        _txt = new TextBox
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2E)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)),
            FontSize = 14
        };
        _txt.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) { Answer = _txt.Text; DialogResult = true; } };
        _txt.Loaded += (s, e) => _txt.Focus();
        Grid.SetRow(_txt, 1);
        grid.Children.Add(_txt);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(btnPanel, 2);

        var okBtn = new Button
        {
            Content = "OK",
            Width = 70,
            Margin = new Thickness(0, 6, 6, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x2E))
        };
        okBtn.Click += (s, e) => { Answer = _txt.Text; DialogResult = true; };
        btnPanel.Children.Add(okBtn);

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Width = 70,
            Margin = new Thickness(0, 6, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4))
        };
        cancelBtn.Click += (s, e) => { DialogResult = false; };
        btnPanel.Children.Add(cancelBtn);

        grid.Children.Add(btnPanel);
        Content = grid;
    }
}
