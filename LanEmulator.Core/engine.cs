namespace LanEmulator.Core;

// ============================================================================
// Engine -- lifecycle manager for the virtual LAN emulator.
//
// Structure:
//   Types         -- records, enums, delegates
//   Events        -- OnLog, OnStateChanged, OnPeerJoined/Left
//   Properties    -- RoomId, ServerUrl, MyVirtualIP, IsHost, etc.
//   Lifecycle     -- HostSetupAsync -> JoinSetup -> Configure -> ConnectAsync -> StartVpn -> ShutdownAsync
//   Goldberg      -- RunGoldbergAsync (auto-patch steam_api64.dll)
//   VPN           -- StartVpn (UDP socket, hole punch, Wintun adapter, packet pumps)
//   Game          -- SetGamePath, LaunchGame
//   Chat          -- PollChatAsync, SendChatAsync
//   Utilities     -- IsAdministrator, GetDriverVersion, GenerateRoomId, GetLocalIP
// ============================================================================



public delegate void LogHandler(LogEntry entry);
public delegate void StateHandler(string state, string? detail);
public delegate void PeerHandler(PlayerInfo peer);
public delegate void ChatHandler(string player, string text, string timestamp);

/// <summary>
/// Central engine for the Wintun LAN Emulator.
/// Replaces the old top-level Program.cs -- exposes events for GUI.
/// </summary>
public class Engine : IEngine
{
    public event LogHandler? OnLog;
    public event StateHandler? OnStateChanged;
    public event PeerHandler? OnPeerJoined;
    public event PeerHandler? OnPeerLeft;
    public event Action<string>? OnRoomCreated;

    public string RoomId { get; private set; } = "";
    public string ServerUrl { get; private set; } = "";
    public string MyVirtualIP { get; private set; } = "10.13.37.1";
    public int PeerCount { get; private set; }
    public int Mode { get; private set; }
    public bool IsHost { get; private set; }
    public bool IsRunning { get; private set; }
    public string? GamePath { get; private set; }
    public string? GameDir { get; private set; }
    public Process? GameProcess { get; private set; }

    public const string Version = "1.2.0";
    public const string AdapterName = "LanEmulatorTun";
    public const string AdapterMask = "255.255.255.0";
    public const int PrefixLength = 24;
    public const int UdpPort = 51820;
    public const int DiscoveryPort = 51821;
    public const int ServerHttpPort = 8000;
    public const int MaxPollRetries = 60;
    public const int PollIntervalMs = 2000;
    public const int AdapterReopenDelayMs = 500;

    // ── Internal state ──────────────────────────────────────
    private readonly List<PlayerInfo> _peers = new();
    private readonly object _peerLock = new();
    private readonly Dictionary<string, IPEndPoint> _ipToPeer = new();

    private HttpClient? _http;
    private LanServer? _server;
    private CancellationTokenSource? _cts;
    private UdpClient? _udp;
    private Thread? _pumpNetToTun, _pumpTunToNet;
    private Task? _keepaliveTask;

    // ── Wintun handles ──────────────────────────────────────
    private IntPtr _adapter, _session;

    private void Log(LogLevel level, string msg) =>
        OnLog?.Invoke(new LogEntry { Level = level, Message = msg });

    // ════════════════════════════════════════════════════════
    // Public API
    // ════════════════════════════════════════════════════════

    /// <summary>True if current process has admin rights.</summary>/// <summary>Check if running with Administrator privileges.</summary>
    /// <summary>True if current process has admin rights.</summary>
    public static bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Check Wintun driver version. 0 = not installed.</summary>
    public static uint GetDriverVersion() => WintunInterop.WintunGetRunningDriverVersion();

    /// <summary>Generate a readable 6-char Room ID.</summary>/// <summary>Generate a random 6-character alphanumeric room ID.</summary>
    /// <summary>Generate a readable 6-char Room ID.</summary>
    public static string GenerateRoomId()
    {
        const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
        var rng = Random.Shared;
        return new string(Enumerable.Range(0, 6).Select(_ => chars[rng.Next(chars.Length)]).ToArray());
    }

    /// <summary>Auto-detect local LAN IPv4 address.</summary>/// <summary>Get the best local IPv4 address for LAN communication.</summary>
    /// <summary>Auto-detect local LAN IPv4 address.</summary>
    public static string GetLocalIP()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                if (ip.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip.Address))
                    return ip.Address.ToString();
        }
        return "127.0.0.1";
    }

    // ════════════════════════════════════════════════════════
    // Setup phases (called sequentially by GUI)
    // ════════════════════════════════════════════════════════

    /// <summary>Phase 1: Start built-in C# HTTP server (host only). No Python required.</summary>/// <summary>Start built-in HTTP signaling server and open firewall rules.</summary>
    /// <summary>Phase 1: Start built-in C# HTTP server (host only). No Python required.</summary>
    public Task HostSetupAsync()
    {
        IsHost = true;
        RoomId = GenerateRoomId();
        OnRoomCreated?.Invoke(RoomId);
        Log(LogLevel.Info, $"Room ID: {RoomId}");

        Log(LogLevel.Info, "Starting built-in signaling server...");
        try
        {
            _server = new LanServer(ServerHttpPort);
            _server.Start();
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Server start failed: {ex.Message}");
            throw;
        }

        string localIp = GetLocalIP();
        ServerUrl = $"http://{localIp}:{ServerHttpPort}";
        Log(LogLevel.Ok, $"Server at {ServerUrl}");

        // Firewall
        try { Helpers.RunSilent("netsh", $"advfirewall firewall add rule name=\"LanEmulator Server\" dir=in action=allow protocol=TCP localport={ServerHttpPort}"); } catch (Exception ex) { Log(LogLevel.Warn, $"Firewall TCP: {ex.Message}"); }
        try { Helpers.RunSilent("netsh", $"advfirewall firewall add rule name=\"LanEmulator UDP\" dir=in action=allow protocol=UDP localport={UdpPort}"); } catch (Exception ex) { Log(LogLevel.Warn, $"Firewall UDP: {ex.Message}"); }
        // UPnP proxy (best-effort)
        try { Helpers.RunSilent("netsh", $"interface portproxy add v4tov4 listenport={ServerHttpPort} connectaddress=127.0.0.1 connectport={ServerHttpPort}"); } catch (Exception ex) { Log(LogLevel.Warn, $"UPnP proxy: {ex.Message}"); }

        return Task.CompletedTask;
    }

    /// <summary>Phase 1b: Join -- discover or enter server URL.</summary>/// <summary>Discover LAN servers via UDP broadcast, or accept CLI-provided URL.</summary>
    /// <summary>Phase 1b: Join -- discover or enter server URL.</summary>
    public string JoinSetup(string? cliUrl = null)
    {
        IsHost = false;
        OnStateChanged?.Invoke("join_setup", null);

        if (!string.IsNullOrWhiteSpace(cliUrl))
            return cliUrl.TrimEnd('/');

        Log(LogLevel.Info, "Scanning LAN for servers…");
        string? found = Helpers.DiscoverServer();
        if (found != null)
        {
            Log(LogLevel.Ok, $"Found server: {found}");
            return found;
        }
        return ""; // GUI will prompt user
    }

    /// <summary>Phase 2: Set mode, room, game path.</summary>/// <summary>Set game mode (Steam+Goldberg / Pure LAN), room ID, and optional game path.</summary>
    /// <summary>Phase 2: Set mode, room, game path.</summary>
    public void Configure(int mode, string roomId, string? gamePath = null)
    {
        Mode = mode;
        RoomId = roomId;
        GamePath = gamePath;
        if (gamePath != null)
        {
            GamePath = Path.GetFullPath(gamePath);
            GameDir = Path.GetDirectoryName(GamePath);
        }
        Log(LogLevel.Ok, $"Mode: {(mode == 1 ? "Steam + Goldberg" : "Pure LAN")}");
    }

    /// <summary>Phase 3: Connect to server, register, poll peers, set up VPN.</summary>/// <summary>Connect to signaling server, register, poll for peers, set up VPN routing.</summary>
    /// <summary>Phase 3: Connect to server, register, poll peers, set up VPN.</summary>
    public async Task ConnectAsync(string serverUrl)
    {
        ServerUrl = serverUrl.TrimEnd('/');
        OnStateChanged?.Invoke("connecting", ServerUrl);

        _http = new HttpClient { BaseAddress = new Uri(ServerUrl) };
        _http.Timeout = TimeSpan.FromSeconds(10);

        // Register
        string myId = Environment.MachineName;
        Log(LogLevel.Info, $"Connecting to {ServerUrl}…");
        try
        {
            var regReq = new { player_id = myId, room_id = RoomId, udp_port = UdpPort };
            var regResp = await _http.PostAsJsonAsync("/register", regReq);
            regResp.EnsureSuccessStatusCode();
            var reg = await regResp.Content.ReadFromJsonAsync<RegisterResponse>();
            MyVirtualIP = reg?.virtual_ip ?? "10.13.37.1";
            Log(LogLevel.Ok, $"Registered as '{myId}'");
            Log(LogLevel.Ok, $"Assigned VPN IP: {MyVirtualIP}");
        }
        catch (Exception ex)
        {
            throw new Exception($"Cannot reach server: {ServerUrl}\n{ex.Message}");
        }

        // Poll for peers (host skips -- starts VPN immediately, keepalive handles joins)
        if (!IsHost)
        {
            OnStateChanged?.Invoke("waiting_peers", null);
            int retries = 0;
            const int maxRetries = 60;

            while (_peers.Count == 0)
            {
                try
                {
                    var poll = await _http.GetFromJsonAsync<PollResponse>($"/poll?room_id={Uri.EscapeDataString(RoomId)}");
                    if (poll is { status: "ready", players: not null })
                    {
                        var found = poll.players.FindAll(p =>
                            !string.Equals(p.player_id, myId, StringComparison.OrdinalIgnoreCase));
                        if (found.Count > 0)
                        {
                            Log(LogLevel.Ok, $"{found.Count} peer(s) connected");
                            lock (_peerLock)
                            {
                                _peers.AddRange(found);
                                _ipToPeer.Clear();
                                foreach (var p in _peers)
                                    _ipToPeer[p.virtual_ip] = new IPEndPoint(IPAddress.Parse(p.ip), p.udp_port);
                            }
                            PeerCount = found.Count;
                            foreach (var p in found)
                                OnPeerJoined?.Invoke(p);
                        }
                    }
                    retries = 0;
                }
                catch (HttpRequestException)
                {
                    retries++;
                    if (retries >= maxRetries)
                        throw new Exception($"Server unreachable after {maxRetries * 2}s");
                }

                if (_peers.Count == 0)
                    await Task.Delay(PollIntervalMs);
            }
        }
    }

    // ════════════════════════════════════════════════════════
    // Phase 4: Goldberg patch + VPN setup
    // ════════════════════════════════════════════════════════
        /// <summary>Auto-detect Steam AppID and deploy Goldberg emulator DLL and INI config.</summary>

    public async Task RunGoldbergAsync()
    {
        if (Mode != 1 || GameDir == null) return;

        string baseDir = AppContext.BaseDirectory;
        string goldbergSrc = Path.Combine(baseDir, "goldberg", "steam_api64.dll");
        string targetDll = Path.Combine(GameDir, "steam_api64.dll");
        string settingsDst = Path.Combine(GameDir, "steam_settings");

        string? appId = Goldberg.AutoDetectAppId(GameDir, GamePath!);
        if (string.IsNullOrEmpty(appId) || appId == "0")
        {
            Log(LogLevel.Info, "Searching Steam for AppID…");
            string gameName = Path.GetFileName(GameDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            try
            {
                var sid = await Goldberg.SteamSearchAppIdAsync(gameName);
                appId = (!string.IsNullOrEmpty(sid) && sid != "0") ? sid : "0";
            }
            catch { appId = "0"; }
        }
        Log(LogLevel.Ok, $"Steam AppID: {appId}");

        // Backup original
        string backupPath = targetDll + ".bak";
        if (File.Exists(targetDll) && !File.Exists(backupPath))
        { File.Move(targetDll, backupPath); Log(LogLevel.Ok, "Original DLL backed up"); }
        else if (File.Exists(backupPath)) Log(LogLevel.Info, "Backup exists, replacing…");

        // Deploy Goldberg DLL
        if (File.Exists(goldbergSrc))
        { File.Copy(goldbergSrc, targetDll, true); Log(LogLevel.Ok, "Goldberg DLL deployed"); }
        else Log(LogLevel.Warn, "Goldberg DLL not found");

        // INI config
        string firstPeerVPN;
        lock (_peerLock) { firstPeerVPN = _peers.Count > 0 ? _peers[0].virtual_ip : "10.13.37.2"; }
        File.WriteAllText(Path.Combine(GameDir, "GoldbergSteamEmu.ini"), $"[Networking]\nip={firstPeerVPN}\n");
        Log(LogLevel.Ok, $"Goldberg INI -> peer: {firstPeerVPN}");

        // steam_settings
        string settingsSrc = Path.Combine(baseDir, "goldberg", "steam_settings");
        if (Directory.Exists(settingsSrc))
        {
            Directory.CreateDirectory(settingsDst);
            foreach (string f in Directory.GetFiles(settingsSrc, "*", SearchOption.AllDirectories))
            {
                string rel = f[(settingsSrc.Length + 1)..];
                string dst = Path.Combine(settingsDst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(f, dst, true);
            }
        }
        else Directory.CreateDirectory(settingsDst);
        File.WriteAllText(Path.Combine(settingsDst, "steam_appid.txt"), appId);
        Log(LogLevel.Ok, "steam_settings/ deployed");
    }

    /// <summary>Phase 5: Create adapter, UDP, pumps, keepalive.</summary>/// <summary>Create UDP socket, hole-punch peers, initialize Wintun adapter, start packet pumps.</summary>
    /// <summary>Phase 5: Create adapter, UDP, pumps, keepalive.</summary>
    public void StartVpn()
    {
        OnStateChanged?.Invoke("vpn_starting", null);

        // UDP socket
        _udp = new UdpClient(UdpPort);
        _udp.Client.ReceiveBufferSize = 4 * 1024 * 1024;
        _udp.Client.SendBufferSize = 4 * 1024 * 1024;
        Log(LogLevel.Ok, $"UDP socket: 0.0.0.0:{UdpPort}");

        // Hole punching
        Log(LogLevel.Info, "Hole punching…");
        byte[] holePunchData = new byte[] { 0x00 };
        List<PlayerInfo> snapshot;
        lock (_peerLock) { snapshot = new List<PlayerInfo>(_peers); }

        foreach (var p in snapshot)
        {
            try
            {
                var ep = new IPEndPoint(IPAddress.Parse(p.ip), p.udp_port);
                for (int i = 0; i < 10; i++)
                    _udp.Send(holePunchData, holePunchData.Length, ep);
            }
            catch (Exception ex) { Log(LogLevel.Warn, $"Hole punch: {ex.Message}"); }
            Thread.Sleep(100);
        }
        Log(LogLevel.Ok, $"Hole punch -> {snapshot.Count} peer(s)");

        // Wintun adapter
        IntPtr existing = WintunInterop.WintunOpenAdapter(AdapterName);
        if (existing != IntPtr.Zero)
        { WintunInterop.WintunCloseAdapter(existing); Thread.Sleep(AdapterReopenDelayMs); }

        _adapter = WintunInterop.WintunCreateAdapter(AdapterName, "LanEmulator", IntPtr.Zero);
        if (_adapter == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            string msg = $"CreateAdapter failed. Win32 error {err}: {Helpers.Win32Msg(err)}";
            if (WintunInterop.WintunGetRunningDriverVersion() == 0)
                msg += "\nDriver not loaded -- restart your PC and try again.";
            throw new Exception(msg);
        }

        if (!WintunInterop.WintunGetAdapterLUID(_adapter, out ulong luid))
        { WintunInterop.WintunCloseAdapter(_adapter); throw new Exception("GetAdapterLUID failed"); }

        string ifAlias = Helpers.GetInterfaceAlias(luid);
        Helpers.RunNetsh($"interface ip set address name=\"{ifAlias}\" source=static addr={MyVirtualIP} mask={AdapterMask}");
        Helpers.RunNetsh($"interface set interface name=\"{ifAlias}\" admin=enabled");
        Log(LogLevel.Ok, $"Adapter '{AdapterName}': {MyVirtualIP}/{PrefixLength}");

        // Routes
        lock (_peerLock)
        { foreach (var p in _peers) Helpers.RunRoute($"add {p.virtual_ip} mask 255.255.255.255 {MyVirtualIP} metric 1"); }
        Log(LogLevel.Ok, $"Routes: {snapshot.Count} peer(s)");

        // Session + pumps
        const uint RingCapacity = 0x400000;
        _session = WintunInterop.WintunStartSession(_adapter, RingCapacity);
        if (_session == IntPtr.Zero) throw new Exception("StartSession failed");

        _cts = new CancellationTokenSource();

        _pumpNetToTun = new Thread(() => Pumps.PumpNetworkToTun(_udp, _session, _cts.Token)) { Name = "Net->Tun", IsBackground = true };
        _pumpTunToNet = new Thread(() => Pumps.PumpTunToNet(_udp, _session, _ipToPeer, _peerLock, _cts.Token)) { Name = "Tun->Net", IsBackground = true };
        _pumpNetToTun.Start(); _pumpTunToNet.Start();
        Log(LogLevel.Ok, "Packet pumps running");

        _keepaliveTask = KeepAlive.RunAsync(_http!, RoomId, UdpPort, _peers, _ipToPeer, _peerLock, _cts.Token,
            (p) => { PeerCount = _peers.Count; OnPeerJoined?.Invoke(p); Log(LogLevel.PeerJoin, $"{p.player_id} VPN: {p.virtual_ip}"); },
            (p) => { PeerCount = _peers.Count; OnPeerLeft?.Invoke(p); Log(LogLevel.PeerLeft, $"{p.player_id} left"); });

        IsRunning = true;
        OnStateChanged?.Invoke("running", MyVirtualIP);
    }

    public void SetGamePath(string path)
    {
        GamePath = Path.GetFullPath(path);
        GameDir = Path.GetDirectoryName(GamePath);
    }

    /// <summary>Launch the game executable (Steam mode only).</summary>/// <summary>Launch the configured game executable.</summary>
    /// <summary>Launch the game executable (Steam mode only).</summary>
    public void LaunchGame()
    {
        if (Mode != 1 || GamePath == null) return;
        try
        {
            GameProcess = Process.Start(new ProcessStartInfo(GamePath) { WorkingDirectory = GameDir, UseShellExecute = false });
            Log(LogLevel.Ok, $"Game launched (PID {GameProcess?.Id})");
        }
        catch (Exception ex) { Log(LogLevel.Warn, $"Game launch failed: {ex.Message}"); }
    }

    /// <summary>Subscribe to chat messages from the server.</summary>/// <summary>Poll server for new chat messages since lastId.</summary>
    /// <summary>Subscribe to chat messages from the server.</summary>
    public async Task<List<ChatMessage>> PollChatAsync(int lastId)
    {
        if (_http == null) return new();
        try
        {
            var msgs = await _http.GetFromJsonAsync<List<ChatMessage>>(
                $"/chat/poll?room_id={Uri.EscapeDataString(RoomId)}&last_id={lastId}");
            return msgs ?? new();
        }
        catch (Exception ex) { Log(LogLevel.Warn, $"Chat poll: {ex.Message}"); return new(); }
    }

    /// <summary>Send a chat message. Returns server-assigned message id, or -1.</summary>/// <summary>Send a chat message to all peers in the room. Returns server-assigned message ID.</summary>
    /// <summary>Send a chat message. Returns server-assigned message id, or -1.</summary>
    public async Task<int> SendChatAsync(string text)
    {
        if (_http == null) return -1;
        try
        {
            var msg = new { room_id = RoomId, player_id = Environment.MachineName, text };
            var resp = await _http.PostAsJsonAsync("/chat", msg);
            if (resp.IsSuccessStatusCode)
            {
                var result = await resp.Content.ReadFromJsonAsync<ChatSendResponse>();
                return result?.id ?? -1;
            }
        }
        catch { }
        return -1;
    }

    /// <summary>Shutdown everything gracefully.</summary>/// <summary>Gracefully stop VPN, kill game process, clean up all resources.</summary>
    /// <summary>Shutdown everything gracefully.</summary>
    public async Task ShutdownAsync()
    {
        IsRunning = false;
        OnStateChanged?.Invoke("shutting_down", null);

        _cts?.Cancel();

        // Leave server
        if (_http != null)
        {
            try { await _http.PostAsync($"/leave?room_id={Uri.EscapeDataString(RoomId)}&player_id={Uri.EscapeDataString(Environment.MachineName)}", null); }
            catch (Exception ex) { Log(LogLevel.Warn, $"Leave server: {ex.Message}"); }
        }
        _http?.Dispose(); _http = null;

        // Kill game
        if (GameProcess is { HasExited: false })
        {
            try { GameProcess.Kill(true); GameProcess.WaitForExit(5000); }
            catch (Exception ex) { Log(LogLevel.Warn, $"Game kill: {ex.Message}"); }
        }

        // Wait for keepalive
        if (_keepaliveTask != null)
        {
            try { await _keepaliveTask; } catch (Exception ex) { Log(LogLevel.Warn, $"Keepalive shutdown: {ex.Message}"); }
        }

        _pumpNetToTun?.Join(3000);
        _pumpTunToNet?.Join(3000);

        // Wintun cleanup
        if (_session != IntPtr.Zero) { WintunInterop.WintunEndSession(_session); _session = IntPtr.Zero; }
        if (_adapter != IntPtr.Zero) { WintunInterop.WintunCloseAdapter(_adapter); _adapter = IntPtr.Zero; }

        _udp?.Dispose();

        // Routes
        lock (_peerLock) { foreach (var p in _peers) Helpers.RunRouteSilent($"delete {p.virtual_ip}"); }

        // Firewall
        Helpers.RunSilent("netsh", "advfirewall firewall delete rule name=\"LanEmulator Server\"");
        Helpers.RunSilent("netsh", "advfirewall firewall delete rule name=\"LanEmulator UDP\"");

        // Stop built-in server
        if (_server != null)
        {
            try { await _server.StopAsync(); } catch (Exception ex) { Log(LogLevel.Warn, $"Server stop: {ex.Message}"); }
            _server.Dispose();
            _server = null;
        }

        Log(LogLevel.Ok, "Shutdown complete");
    }
}
