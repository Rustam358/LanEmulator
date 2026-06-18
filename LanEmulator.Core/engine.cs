namespace LanEmulator.Core;

/// <summary>
/// Central engine for the Wintun LAN Emulator.
/// Coordinates IPeerRegistry, ISignalingServer, and IVpnController.
/// Replaces the old top-level Program.cs -- exposes events for GUI.
/// </summary>
public class Engine : IEngine
{
    // ── Services ──────────────────────────────────────────
    private readonly Interfaces.IPeerRegistry _peerReg;
    private readonly Interfaces.ISignalingServer _signaling;
    private readonly Interfaces.IVpnController _vpn;

    // ── Events ────────────────────────────────────────────
    public event LogHandler OnLog;
    public event StateHandler OnStateChanged;
    public event PeerHandler OnPeerJoined;
    public event PeerHandler OnPeerLeft;
    public event Action<string>? OnRoomCreated;

    // ── Properties ────────────────────────────────────────
    public string RoomId { get; private set; } = "";
    public string ServerUrl { get; private set; } = "";
    public string MyVirtualIP { get; private set; } = "10.13.37.1";
    public int PeerCount => _peerReg.Count;
    public int Mode { get; private set; }
    public bool IsHost { get; private set; }
    public bool IsRunning { get; private set; }
    public string? GamePath { get; private set; }
    public string? GameDir { get; private set; }
    public Process? GameProcess { get; private set; }

    // ── Constants ─────────────────────────────────────────
    public const string Version = "1.3.2";
    public const string AdapterName = "LanEmulatorTun";
    public const string AdapterMask = "255.255.255.0";
    public const int PrefixLength = 24;
    public const int UdpPort = 51820;
    public const int DiscoveryPort = 51821;
    public const int ServerHttpPort = 8000;
    public const int MaxPollRetries = 60;
    public const int PollIntervalMs = 2000;
    public const int AdapterReopenDelayMs = 500;

    // ── Internal state ────────────────────────────────────
    private HttpClient? _http;
    private CancellationTokenSource? _cts;
    private Task? _keepaliveTask;

    // ── Construction ──────────────────────────────────────
    public Engine()
    {
        _peerReg = new Services.PeerRegistry();
        _signaling = new Services.SignalingServer();
        _vpn = new Services.VpnController();

        _peerReg.PeerAdded += p => OnPeerJoined?.Invoke(p);
        _peerReg.PeerRemoved += p => OnPeerLeft?.Invoke(p);
    }

    private void Log(LogLevel level, string msg) =>
        OnLog?.Invoke(new LogEntry { Level = level, Message = msg });

    // ════════════════════════════════════════════════════════
    // Public API
    // ════════════════════════════════════════════════════════

    /// <summary>True if current process has admin rights.</summary>
    public static bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Check Wintun driver version. 0 = not installed.</summary>
    public static uint GetDriverVersion() => WintunInterop.WintunGetRunningDriverVersion();

    /// <summary>Generate a readable 6-char Room ID.</summary>
    public static string GenerateRoomId()
    {
        const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
        var rng = Random.Shared;
        return new string(Enumerable.Range(0, 6).Select(_ => chars[rng.Next(chars.Length)]).ToArray());
    }

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

    /// <summary>Phase 1: Start built-in C# HTTP server (host only).</summary>
    public Task HostSetupAsync()
    {
        IsHost = true;
        RoomId = GenerateRoomId();
        OnRoomCreated?.Invoke(RoomId);
        Log(LogLevel.Info, $"Room ID: {RoomId}");

        Log(LogLevel.Info, "Starting built-in signaling server...");
        try
        {
            _signaling.Start(ServerHttpPort);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Server start failed: {ex.Message}");
            throw;
        }

        string localIp = GetLocalIP();
        ServerUrl = $"http://{localIp}:{ServerHttpPort}";
        Log(LogLevel.Ok, $"Server at {ServerUrl}");

        _signaling.AddFirewallRules(ServerHttpPort, UdpPort);
        return Task.CompletedTask;
    }

    /// <summary>Phase 1b: Join -- discover or enter server URL.</summary>
    public string JoinSetup(string? cliUrl = null)
    {
        IsHost = false;
        OnStateChanged?.Invoke("join_setup", null);

        if (!string.IsNullOrWhiteSpace(cliUrl))
            return cliUrl.TrimEnd('/');

        Log(LogLevel.Info, "Scanning LAN for servers:");
        string? found = _signaling.Discover(GetLocalIP(), DiscoveryPort);
        if (found != null)
        {
            Log(LogLevel.Ok, $"Found server: {found}");
            return found;
        }
        return ""; // GUI will prompt user
    }

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

    /// <summary>Phase 3: Connect to server, register, poll peers, set up VPN.</summary>
    public async Task ConnectAsync(string serverUrl)
    {
        ServerUrl = serverUrl.TrimEnd('/');
        OnStateChanged?.Invoke("connecting", ServerUrl);

        _http = new HttpClient { BaseAddress = new Uri(ServerUrl) };
        _http.Timeout = TimeSpan.FromSeconds(10);
        _signaling.HttpClient = _http;

        string myId = Environment.MachineName;
        Log(LogLevel.Info, $"Connecting to {ServerUrl}:");

        try
        {
            MyVirtualIP = await _signaling.RegisterAsync(myId, RoomId, UdpPort);
            Log(LogLevel.Ok, $"Registered as '{myId}'");
            Log(LogLevel.Ok, $"Assigned VPN IP: {MyVirtualIP}");
        }
        catch (Exception ex)
        {
            throw new Exception($"Cannot reach server: {ServerUrl}\n{ex.Message}");
        }

        // Poll for peers (host skips -- starts VPN immediately)
        if (!IsHost)
        {
            OnStateChanged?.Invoke("waiting_peers", null);
            int retries = 0;

            while (_peerReg.Count == 0)
            {
                try
                {
                    var found = await _signaling.PollAsync(RoomId, myId);
                    if (found is { Count: > 0 })
                    {
                        _peerReg.AddOrUpdate(found, myId);
                    }
                    retries = 0;
                }
                catch (HttpRequestException)
                {
                    retries++;
                    if (retries >= MaxPollRetries)
                        throw new Exception($"Server unreachable after {MaxPollRetries * 2}s");
                }

                if (_peerReg.Count == 0)
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
            Log(LogLevel.Info, "Searching Steam for AppID:");
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
        else if (File.Exists(backupPath)) Log(LogLevel.Info, "Backup exists, replacing:");

        // Deploy Goldberg DLL
        if (File.Exists(goldbergSrc))
        { File.Copy(goldbergSrc, targetDll, true); Log(LogLevel.Ok, "Goldberg DLL deployed"); }
        else Log(LogLevel.Warn, "Goldberg DLL not found");

        // INI config
        var peers = _peerReg.Peers;
        string firstPeerVPN = peers.Count > 0 ? peers[0].virtual_ip : "10.13.37.2";
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

    /// <summary>Phase 5: Create adapter, UDP, pumps, keepalive.</summary>
    public async Task StartVpnAsync()
    {
        OnStateChanged?.Invoke("vpn_starting", null);

        await Task.Run(() => _vpn.Start(AdapterName, MyVirtualIP, AdapterMask, PrefixLength, UdpPort, _peerReg));
        Log(LogLevel.Ok, $"UDP socket: 0.0.0.0:{UdpPort}");
        Log(LogLevel.Ok, $"Hole punch -> {_peerReg.Count} peer(s)");
        Log(LogLevel.Ok, $"Adapter '{AdapterName}': {MyVirtualIP}/{PrefixLength}");
        Log(LogLevel.Ok, $"Routes: {_peerReg.Count} peer(s)");
        Log(LogLevel.Ok, "Packet pumps running");

        _cts = new CancellationTokenSource();
        _keepaliveTask = KeepAlive.RunAsync(_http!, RoomId, UdpPort, _peerReg, _cts.Token,
            (p) => Log(LogLevel.PeerJoin, $"{p.player_id} VPN: {p.virtual_ip}"),
            (p) => Log(LogLevel.PeerLeft, $"{p.player_id} left"));

        IsRunning = true;
        OnStateChanged?.Invoke("running", MyVirtualIP);
    }

    public void SetGamePath(string path)
    {
        GamePath = Path.GetFullPath(path);
        GameDir = Path.GetDirectoryName(GamePath);
        ValidateGameDirectory();
    }


    /// <summary>Scan game directory for common compatibility issues.</summary>
    public void ValidateGameDirectory()
    {
        if (string.IsNullOrEmpty(GameDir) || !Directory.Exists(GameDir)) return;
        try
        {
            foreach (var file in Directory.GetFiles(GameDir, "*.dll"))
            {
                try
                {
                    var bytes = File.ReadAllBytes(file);
                    if (bytes.Length < 0x40) continue;
                    // MZ signature
                    if (bytes[0] != 'M' || bytes[1] != 'Z')
                    {
                        var name = Path.GetFileName(file);
                        Log(LogLevel.Warn, string.Concat("Game dir: '", name, "' is not a valid DLL - may cause launch errors"));
                        continue;
                    }
                    int peOff = BitConverter.ToInt32(bytes, 0x3C);
                    if (peOff < 64 || peOff + 6 > bytes.Length) continue;
                    // PE signature
                    if (bytes[peOff] != 'P' || bytes[peOff+1] != 'E') continue;
                    ushort machine = BitConverter.ToUInt16(bytes, peOff + 4);
                    if (machine == 0x8664) // AMD64
                    {
                        var name = Path.GetFileName(file);
                        Log(LogLevel.Warn, string.Concat("64-bit DLL in game folder: '", name,
                            "' - may crash 32-bit games (0xc000007b). Remove or use Pure LAN mode."));
                    }
                }
                catch { /* skip */ }
            }
            // Goldberg leftovers
            bool gDll = File.Exists(Path.Combine(GameDir, "steam_api64.dll"));
            bool gBak = File.Exists(Path.Combine(GameDir, "steam_api64.dll.bak"));
            bool gIni = File.Exists(Path.Combine(GameDir, "GoldbergSteamEmu.ini"));
            bool gDir = Directory.Exists(Path.Combine(GameDir, "steam_settings"));
            if (gDll || gBak || gIni || gDir)
                Log(LogLevel.Warn, "Goldberg emulator leftovers in game folder. For Pure LAN, remove these files.");
        }
        catch (Exception ex) { Log(LogLevel.Warn, string.Concat("Game scan: ", ex.Message)); }
    }

        /// <summary>Launch the game executable (Steam mode only).</summary>
    public void LaunchGame()
    {
        if (GamePath == null) return;
        try
        {
            GameProcess = Process.Start(new ProcessStartInfo(GamePath!)
            {
                WorkingDirectory = GameDir,
                UseShellExecute = true
            });
            Log(LogLevel.Ok, string.Concat("Game launched (PID ", GameProcess?.Id, ")"));
        }
        catch (Win32Exception ex)
        {
            Log(LogLevel.Warn, string.Concat("Game launch failed (Windows error): ", ex.Message));
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warn, string.Concat("Game launch failed: ", ex.Message));
        }
    }
    public async Task<List<ChatMessage>> PollChatAsync(int lastId)
    {
        try { return await _signaling.PollChatAsync(lastId, RoomId); }
        catch (Exception ex) { Log(LogLevel.Warn, $"Chat poll: {ex.Message}"); return new(); }
    }

    /// <summary>Send a chat message. Returns server-assigned message id, or -1.</summary>
    public async Task<int> SendChatAsync(string text)
    {
        try { return await _signaling.SendChatAsync(RoomId, Environment.MachineName, text); }
        catch { return -1; }
    }

    /// <summary>Shutdown everything gracefully.</summary>
    public async Task ShutdownAsync()
    {
        IsRunning = false;
        OnStateChanged?.Invoke("shutting_down", null);

        _cts?.Cancel();

        // Leave server
        try { await _signaling.LeaveAsync(RoomId, Environment.MachineName); }
        catch (Exception ex) { Log(LogLevel.Warn, $"Leave server: {ex.Message}"); }

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
            try { await _keepaliveTask; }
            catch (Exception ex) { Log(LogLevel.Warn, $"Keepalive shutdown: {ex.Message}"); }
        }

        await _vpn.StopAsync(AdapterName);

        // Routes
        foreach (var p in _peerReg.Peers)
            Helpers.RunRouteSilent($"delete {p.virtual_ip}");

        _signaling.RemoveFirewallRules();
        await _signaling.StopAsync();

        Log(LogLevel.Ok, "Shutdown complete");
    }
}
