using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// ═══════════════════════════════════════════════════════════════
// Wintun Virtual LAN Emulator v1.0.0
// .NET 8 | x64 only | Auto-elevation | Built-in server
// ═══════════════════════════════════════════════════════════════

const string Version = "1.0.0";
const string AdapterName = "LanEmulatorTun";
const string AdapterMask = "255.255.255.0";
const int    PrefixLength = 24;
const int    UdpPort = 51820;
const int    DiscoveryPort = 51821;
const int    ServerHttpPort = 8000;

// ── 1. Auto-elevate ──────────────────────────────────────────
if (!IsAdministrator())
{
    Console.WriteLine("[*] Requesting administrator privileges…");
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
            UseShellExecute = true,
            Verb = "runas"
        };
        Process.Start(psi);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[FATAL] Administrator privileges required. ({ex.Message})");
    }
    return 1;
}

// ── 2. Banner ─────────────────────────────────────────────────
Console.WriteLine($"=== LanEmulator v{Version} — Virtual LAN for Gaming ===");
Console.WriteLine($"    UDP data : 0.0.0.0:{UdpPort}");
Console.WriteLine($"    Discovery: UDP {DiscoveryPort} (broadcast)");
Console.WriteLine();

// ── 3. Help flag ──────────────────────────────────────────────
if (args.Length > 0 && (args[0] == "--help" || args[0] == "-h" || args[0] == "/?"))
{
    Console.WriteLine("Usage: LanEmulator.exe [server_url]");
    Console.WriteLine("  server_url  Signaling server address (optional — auto-detected on LAN)");
    Console.WriteLine("Examples:");
    Console.WriteLine("  LanEmulator.exe                  (auto-detect or host)");
    Console.WriteLine("  LanEmulator.exe http://1.2.3.4:8000");
    return 0;
}

// ── 4. Wintun driver — auto-install if missing ────────────────
uint ver = WintunGetRunningDriverVersion();
if (ver == 0)
{
    Console.WriteLine("[*]   Wintun driver not installed — attempting auto-install…");

    string msiUrl = "https://download.wireguard.com/windows-client/wireguard-amd64-1.1.msi";
    string msiPath = Path.Combine(Path.GetTempPath(), "wintun-driver.msi");

    try
    {
        // Download WireGuard MSI (includes Wintun driver)
        Console.Write($"[*]   Downloading driver (~6 MB)…");
        using (var hc = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
        {
            var msiBytes = await hc.GetByteArrayAsync(msiUrl);
            await File.WriteAllBytesAsync(msiPath, msiBytes);
        }
        Console.WriteLine(" OK");

        // Install silently
        Console.Write("[*]   Installing Wintun driver…");
        var installPsi = new ProcessStartInfo("msiexec",
            $"/i \"{msiPath}\" /quiet /norestart")
        { UseShellExecute = false, CreateNoWindow = true };
        var installProc = Process.Start(installPsi)!;
        await installProc.WaitForExitAsync();
        Console.WriteLine($" OK (exit {installProc.ExitCode})");

        // Cleanup MSI
        try { File.Delete(msiPath); } catch { }

        // Verify driver loaded
        ver = WintunGetRunningDriverVersion();
        if (ver == 0)
        {
            Console.Error.WriteLine("[FATAL] Wintun driver still not available after install.");
            Console.Error.WriteLine("        Please restart your PC and try again.");
            return 9;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"\n[FATAL] Could not install Wintun driver: {ex.Message}");
        Console.Error.WriteLine("        Download manually: https://www.wintun.net/");
        try { File.Delete(msiPath); } catch { }
        return 9;
    }
}
Console.WriteLine($"[OK]   Wintun driver v{ver >> 16}.{ver & 0xFFFF}");

// ── 5. Verify bundled files ───────────────────────────────────
string baseDir = AppContext.BaseDirectory;
string wintunPath = Path.Combine(baseDir, "wintun.dll");
if (!File.Exists(wintunPath))
{
    Console.Error.WriteLine($"[FATAL] wintun.dll not found at: {wintunPath}");
    return 2;
}

// ── 6. Host or Join? ──────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("Host a room, or join a friend's room?");
Console.WriteLine("  [h] Host (run server + create room)");
Console.WriteLine("  [j] Join (connect to existing room)");
Console.Write("> ");
char hostChoice = Console.ReadKey(true).KeyChar;
Console.WriteLine(hostChoice);

bool isHost = hostChoice == 'h' || hostChoice == 'H' || hostChoice == 'ы' || hostChoice == 'Ы';

string? signalServerUrl = null;
Process? serverProcess = null;

if (isHost)
{
    // ── 7. Start embedded Python server ────────────────────────
    Console.WriteLine();
    Console.WriteLine("─── Host Setup ────────────────────────────────────────");

    // Check Python availability
    string? pythonPath = FindPython();
    if (pythonPath == null)
    {
        Console.Error.WriteLine("[FATAL] Python not found. Install from https://python.org");
        Console.Error.WriteLine("        Then: pip install fastapi uvicorn");
        return 10;
    }

    // Check dependencies
    string serverPy = Path.Combine(baseDir, "server.py");
    if (!File.Exists(serverPy))
    {
        Console.Error.WriteLine("[FATAL] server.py not found next to executable.");
        return 11;
    }

    // Install deps if needed
    try
    {
        Console.Write("[*]   Checking Python dependencies…");
        var checkProc = Process.Start(new ProcessStartInfo(pythonPath, "-c \"import fastapi, uvicorn\"")
        {
            UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true
        });
        checkProc!.WaitForExit(5000);
        if (checkProc.ExitCode != 0)
        {
            Console.Write(" installing…");
            var installProc = Process.Start(new ProcessStartInfo(pythonPath, "-m pip install fastapi uvicorn -q")
            {
                UseShellExecute = false, CreateNoWindow = true
            });
            installProc!.WaitForExit(30_000);
        }
        Console.WriteLine(" OK");
    }
    catch { Console.WriteLine(" (skipped)"); }

    // Start server
    serverProcess = Process.Start(new ProcessStartInfo(pythonPath,
        $"-m uvicorn server:app --host 0.0.0.0 --port {ServerHttpPort}")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = baseDir,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    });
    Console.WriteLine($"[OK]   Server started on 0.0.0.0:{ServerHttpPort}");
    Thread.Sleep(2000); // wait for uvicorn to boot

    // AUTO-DETECT LOCAL IP
    string localIP = GetLocalIP();
    signalServerUrl = $"http://{localIP}:{ServerHttpPort}";
    Console.WriteLine($"[OK]   Your LAN IP: {localIP}");
    Console.WriteLine($"       Server URL : {signalServerUrl}");

    // ── 8. Firewall rule ──────────────────────────────────────
    try
    {
        RunSilent("netsh", $"advfirewall firewall add rule name=\"LanEmulator Server\" dir=in action=allow protocol=TCP localport={ServerHttpPort}");
        RunSilent("netsh", $"advfirewall firewall add rule name=\"LanEmulator UDP\" dir=in action=allow protocol=UDP localport={UdpPort}");
        Console.WriteLine("[OK]   Firewall rules added");
    }
    catch { Console.WriteLine("[WARN] Could not add firewall rules"); }

    // ── 9. UPnP port mapping ──────────────────────────────────
    SetupUpnp(ServerHttpPort, localIP);

    // ── 10. Show connection info ───────────────────────────────
    Console.WriteLine();
    Console.WriteLine("╔═══════════════════════════════════════════════════════╗");
    Console.WriteLine("║  Share with friends:                                  ║");
    Console.WriteLine($"║    LanEmulator.exe {signalServerUrl}                 ║");
    Console.WriteLine("║                                                       ║");
    Console.WriteLine("║  OR just:                                             ║");
    Console.WriteLine("║    LanEmulator.exe           (auto-discover on LAN)    ║");
    Console.WriteLine("╚═══════════════════════════════════════════════════════╝");
    Console.WriteLine();

    Console.Write("Press any key to continue…");
    Console.ReadKey(true);
    Console.WriteLine();
}
else
{
    // ── 7b. JOIN: Try LAN auto-discovery ──────────────────────
    Console.WriteLine();
    Console.WriteLine("─── Join Setup ─────────────────────────────────────────");

    // If user provided URL via CLI, use it
    if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
    {
        signalServerUrl = args[0].TrimEnd('/');
    }
    else
    {
        Console.Write("[*]   Scanning LAN for servers…");
        signalServerUrl = DiscoverServer();
        if (signalServerUrl != null)
        {
            Console.WriteLine();
            Console.WriteLine($"[OK]   Found server at: {signalServerUrl}");
        }
        else
        {
            Console.WriteLine(" none found.");
            Console.Write("Server URL (e.g. http://192.168.1.50:8000): ");
            signalServerUrl = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(signalServerUrl))
            {
                Console.Error.WriteLine("[FATAL] Server URL required to join.");
                return 12;
            }
        }
    }
}

signalServerUrl = signalServerUrl!.TrimEnd('/');

// ── 11. Select mode ──────────────────────────────────────────
Console.WriteLine();
int mode = 0;
while (mode != 1 && mode != 2)
{
    Console.WriteLine("Select mode:");
    Console.WriteLine("  [1] Steam Game (Goldberg auto-patcher)");
    Console.WriteLine("  [2] Pure LAN (VPN only — no file changes)");
    Console.Write("> ");
    string? input = Console.ReadLine()?.Trim();
    if (int.TryParse(input, out mode) && (mode == 1 || mode == 2))
        break;
    Console.WriteLine("   Please enter 1 or 2.");
}
Console.WriteLine($"[OK]   Mode: {(mode == 1 ? "Steam Game" : "Pure LAN")}");
Console.WriteLine();

// ── 12. Room ID (auto-generate if host) ──────────────────────
string roomId;
if (isHost)
{
    roomId = GenerateRoomId();
    Console.WriteLine($"[OK]   Room created: {roomId}");
    Console.WriteLine("       Share this code with your friends.");
}
else
{
    Console.Write("Room ID: ");
    roomId = Console.ReadLine()?.Trim() ?? "";
    if (string.IsNullOrWhiteSpace(roomId))
    {
        Console.Error.WriteLine("[FATAL] Room ID is required.");
        return 6;
    }
}
Console.WriteLine();

// ── 13. Game path (Steam mode) ───────────────────────────────
string? gameExePath = null;
string? gameDir = null;

if (mode == 1)
{
    Console.Write("Path to game .exe: ");
    gameExePath = Console.ReadLine()?.Trim();

    if (!string.IsNullOrWhiteSpace(gameExePath))
    {
        gameExePath = Path.GetFullPath(gameExePath);
        if (!File.Exists(gameExePath))
        {
            Console.Error.WriteLine($"[FATAL] Game executable not found: {gameExePath}");
            CleanupAndExit(serverProcess, 8);
            return 8;
        }
        gameDir = Path.GetDirectoryName(gameExePath)!;
        Console.WriteLine($"[OK]   Game: {gameExePath}");
    }
}

// ── 14. Register with signaling server ───────────────────────
using var http = new HttpClient { BaseAddress = new Uri(signalServerUrl) };
http.Timeout = TimeSpan.FromSeconds(10);

RegisterResponse? reg;
Console.WriteLine($"[*]   Connecting to {signalServerUrl}…");
try
{
    var regReq = new { player_id = Environment.MachineName, room_id = roomId, udp_port = UdpPort };
    var regResp = await http.PostAsJsonAsync("/register", regReq);
    regResp.EnsureSuccessStatusCode();
    reg = await regResp.Content.ReadFromJsonAsync<RegisterResponse>();
    Console.WriteLine($"[OK]   Registered as '{regReq.player_id}'");
    Console.WriteLine($"[OK]   Players in room: {reg?.player_count}");
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"[FATAL] Cannot reach server: {signalServerUrl}");
    Console.Error.WriteLine($"       {ex.Message}");
    CleanupAndExit(serverProcess, 7);
    return 7;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[FATAL] Registration failed: {ex.Message}");
    CleanupAndExit(serverProcess, 7);
    return 7;
}

string myVirtualIP = reg?.virtual_ip ?? "10.13.37.1";
Console.WriteLine($"[OK]   Assigned VPN IP: {myVirtualIP}");

// ── 15. Poll until peers join ────────────────────────────────
var peers = new List<PlayerInfo>();
var peerLock = new object();
var ipToPeer = new Dictionary<string, IPEndPoint>();

Console.WriteLine();
Console.Write("[*]   Waiting for peers");
int pollRetries = 0;
const int maxPollRetries = 60;

while (peers.Count == 0)
{
    try
    {
        var poll = await http.GetFromJsonAsync<PollResponse>(
            $"/poll?room_id={Uri.EscapeDataString(roomId)}");

        if (poll is { status: "ready", players: not null })
        {
            var found = poll.players.FindAll(p =>
                !string.Equals(p.player_id, Environment.MachineName, StringComparison.OrdinalIgnoreCase));

            if (found.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"[OK]   {found.Count} peer(s) connected:");
                for (int i = 0; i < found.Count; i++)
                {
                    var p = found[i];
                    Console.WriteLine($"       [{i + 1}] {p.player_id,-20} VPN: {p.virtual_ip,-16} Public: {p.ip}:{p.udp_port}");
                }

                lock (peerLock)
                {
                    peers = found;
                    ipToPeer.Clear();
                    foreach (var p in peers)
                        ipToPeer[p.virtual_ip] = new IPEndPoint(IPAddress.Parse(p.ip), p.udp_port);
                }
            }
        }
        pollRetries = 0;
    }
    catch (HttpRequestException)
    {
        pollRetries++;
        if (pollRetries >= maxPollRetries)
        {
            Console.Error.WriteLine($"\n[FATAL] Server unreachable after {maxPollRetries * 2}s.");
            Console.Error.WriteLine($"        Check: is '{signalServerUrl}' running?");
            CleanupAndExit(serverProcess, 7);
            return 7;
        }
        Console.Write("?");
    }
    catch (Exception ex)
    {
        pollRetries++;
        if (pollRetries >= maxPollRetries)
        {
            Console.Error.WriteLine($"\n[FATAL] Poll failed: {ex.Message}");
            CleanupAndExit(serverProcess, 7);
            return 7;
        }
        Console.Write($"!({ex.Message.GetHashCode():X4})");
    }

    if (peers.Count == 0)
    {
        Console.Write(".");
        await Task.Delay(2000, CancellationToken.None);
    }
}

// ── 16. Goldberg Auto-Patcher (Steam mode) ───────────────────
if (mode == 1 && gameDir != null)
{
    Console.WriteLine();
    Console.WriteLine("─── Goldberg Auto-Patcher ────────────────────────────");

    string goldbergSrc = Path.Combine(baseDir, "goldberg", "steam_api64.dll");
    string targetDll = Path.Combine(gameDir, "steam_api64.dll");
    string settingsDst = Path.Combine(gameDir, "steam_settings");

    string? appId = AutoDetectAppId(gameDir, gameExePath!);
    if (string.IsNullOrEmpty(appId) || appId == "0")
    {
        string gameName = Path.GetFileName(gameDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        appId = await SteamSearchAppId(gameName);
    }

    if (!string.IsNullOrEmpty(appId) && appId != "0")
        Console.WriteLine($"[OK]   Detected Steam AppID: {appId}");
    else { appId = "0"; Console.WriteLine("[WARN] Could not detect AppID."); }

    string backupPath = targetDll + ".bak";
    if (File.Exists(targetDll) && !File.Exists(backupPath))
    { File.Move(targetDll, backupPath); Console.WriteLine("[OK]   Original DLL backed up"); }
    else if (File.Exists(backupPath)) Console.WriteLine("[INFO] Backup exists, replacing…");

    if (File.Exists(goldbergSrc))
    { File.Copy(goldbergSrc, targetDll, true); Console.WriteLine("[OK]   Goldberg DLL deployed"); }
    else Console.Error.WriteLine("[WARN] Goldberg DLL not found");

    string firstPeerVPN;
    lock (peerLock) { firstPeerVPN = peers.Count > 0 ? peers[0].virtual_ip : "10.13.37.2"; }
    File.WriteAllText(Path.Combine(gameDir, "GoldbergSteamEmu.ini"), $"[Networking]\nip={firstPeerVPN}\n");
    Console.WriteLine($"[OK]   Goldberg INI → peer: {firstPeerVPN}");

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
        Console.WriteLine("[OK]   steam_settings/ deployed");
    }
    else Directory.CreateDirectory(settingsDst);
    File.WriteAllText(Path.Combine(settingsDst, "steam_appid.txt"), appId);
    Console.WriteLine($"[OK]   steam_appid.txt → {appId}");
    Console.WriteLine("──────────────────────────────────────────────────────");
}

// ── 17. Create UDP socket (shared: hole punch + pumps) ───────
using var udp = new UdpClient(UdpPort);
udp.Client.ReceiveBufferSize = 4 * 1024 * 1024;
udp.Client.SendBufferSize = 4 * 1024 * 1024;
Console.WriteLine($"[OK]   UDP socket: 0.0.0.0:{UdpPort}");

// ── 18. Hole punching ────────────────────────────────────────
Console.WriteLine();
Console.Write("[*]   Hole punching");
byte[] holePunchData = new byte[] { 0x00 };
int punchCount = 0;

List<PlayerInfo> peersSnapshot;
lock (peerLock) { peersSnapshot = new List<PlayerInfo>(peers); }

foreach (var p in peersSnapshot)
{
    try
    {
        var ep = new IPEndPoint(IPAddress.Parse(p.ip), p.udp_port);
        for (int i = 0; i < 10; i++)
        { udp.Send(holePunchData, holePunchData.Length, ep); punchCount++; Console.Write("."); }
    }
    catch { Console.Write("!"); }
    await Task.Delay(100, CancellationToken.None);
}
Console.WriteLine($" {punchCount} packets → {peersSnapshot.Count} peer(s)");

// ── 19. Wintun adapter ───────────────────────────────────────
IntPtr existing = WintunOpenAdapter(AdapterName);
if (existing != IntPtr.Zero)
{ WintunCloseAdapter(existing); Thread.Sleep(500); }

IntPtr adapter = WintunCreateAdapter(AdapterName, "LanEmulator", IntPtr.Zero);
if (adapter == IntPtr.Zero)
{
    int err = Marshal.GetLastWin32Error();
    Console.Error.WriteLine($"[FATAL] CreateAdapter failed. Win32 error {err}: {Win32Msg(err)}");
    if (err == 1168 || err == 2 || err == 126)
        Console.Error.WriteLine("       Wintun driver may not be installed: https://www.wintun.net/");
    CleanupAndExit(serverProcess, 3);
    return 3;
}

if (!WintunGetAdapterLUID(adapter, out ulong luid))
{ Console.Error.WriteLine($"[FATAL] GetAdapterLUID failed"); WintunCloseAdapter(adapter); CleanupAndExit(serverProcess, 4); return 4; }

string ifAlias = GetInterfaceAlias(luid);
RunNetsh($"interface ip set address name=\"{ifAlias}\" source=static addr={myVirtualIP} mask={AdapterMask}");
RunNetsh($"interface set interface name=\"{ifAlias}\" admin=enabled");
Console.WriteLine($"[OK]   Adapter '{AdapterName}': {myVirtualIP}/{PrefixLength}");

// ── 20. Routes ───────────────────────────────────────────────
lock (peerLock)
{ foreach (var p in peers) RunRoute($"add {p.virtual_ip} mask 255.255.255.255 {myVirtualIP} metric 1"); }
Console.WriteLine($"[OK]   Routes: {peersSnapshot.Count} peer(s)");

// ── 21. Wintun session + pumps ───────────────────────────────
const uint RingCapacity = 0x400000;
IntPtr session = WintunStartSession(adapter, RingCapacity);
if (session == IntPtr.Zero)
{ Console.Error.WriteLine("[FATAL] StartSession failed"); CleanupAndExit(serverProcess, 5); return 5; }

using var cts = new CancellationTokenSource();
var pumpNetToTun = new Thread(() => PumpNetworkToTun(udp, session, cts.Token)) { Name = "Net→Tun", IsBackground = true };
var pumpTunToNet = new Thread(() => PumpTunToNet(udp, session, ipToPeer, peerLock, cts.Token)) { Name = "Tun→Net", IsBackground = true };
pumpNetToTun.Start(); pumpTunToNet.Start();
Console.WriteLine("[OK]   Packet pumps running");

var keepaliveTask = KeepAliveAsync(http, roomId, UdpPort, peers, ipToPeer, peerLock, cts.Token);

// ── 22. Launch game (Steam mode) ─────────────────────────────
Process? gameProcess = null;
if (mode == 1 && gameExePath != null)
{
    try
    {
        gameProcess = Process.Start(new ProcessStartInfo(gameExePath) { WorkingDirectory = gameDir, UseShellExecute = false });
        Console.WriteLine($"[OK]   Game launched (PID {gameProcess?.Id})");
    }
    catch (Exception ex) { Console.Error.WriteLine($"[WARN] {ex.Message}"); }
}

// ── 23. Status display ───────────────────────────────────────
Console.WriteLine();
Console.WriteLine("╔═══════════════════════════════════════════════════════╗");
Console.WriteLine(mode == 1
    ? "║  VPN + Goldberg ACTIVE — Game running.              ║"
    : "║  Virtual LAN ACTIVE — Launch your game manually.    ║");
Console.WriteLine("║                                                       ║");
Console.WriteLine($"║  My  IP    : {myVirtualIP}/{PrefixLength,-30}║");
lock (peerLock)
{
    Console.WriteLine($"║  Peers     : {peers.Count,-38}║");
    for (int i = 0; i < Math.Min(peers.Count, 5); i++)
        Console.WriteLine($"║    [{i + 1}] {peers[i].player_id,-12} {peers[i].virtual_ip,-16} {peers[i].ip,-16}║");
    if (peers.Count > 5) Console.WriteLine($"║    ... and {peers.Count - 5} more                              ║");
}
Console.WriteLine($"║  UDP       : 0.0.0.0:{UdpPort,-31}║");
Console.WriteLine($"║  Room      : {roomId,-38}║");
Console.WriteLine($"║  Server    : {signalServerUrl,-38}║");
if (gameProcess != null) Console.WriteLine($"║  Game PID  : {gameProcess.Id,-38}║");
Console.WriteLine("║                                                       ║");
Console.WriteLine("║  Press ESC to shutdown…                              ║");
Console.WriteLine("╚═══════════════════════════════════════════════════════╝");

while (Console.ReadKey(true).Key != ConsoleKey.Escape) { }

// ── 24. Cleanup ──────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("[*] Shutting down…");

cts.Cancel();

try { await http.PostAsync($"/leave?room_id={Uri.EscapeDataString(roomId)}&player_id={Uri.EscapeDataString(Environment.MachineName)}", null); }
catch { }

if (mode == 1 && gameProcess is { HasExited: false })
{
    try { gameProcess.Kill(true); gameProcess.WaitForExit(5000); Console.WriteLine("[OK]   Game terminated"); }
    catch { }
}

try { await keepaliveTask; } catch { }
pumpNetToTun.Join(3000); pumpTunToNet.Join(3000);

WintunEndSession(session);
WintunCloseAdapter(adapter);
Console.WriteLine("[OK]   Wintun adapter closed");

udp.Close();
Console.WriteLine("[OK]   UDP socket closed");

lock (peerLock) { foreach (var p in peers) RunRoute($"delete {p.virtual_ip}"); }
Console.WriteLine("[OK]   Routes removed");

// Firewall cleanup
RunSilent("netsh", "advfirewall firewall delete rule name=\"LanEmulator Server\"");
RunSilent("netsh", "advfirewall firewall delete rule name=\"LanEmulator UDP\"");
Console.WriteLine("[OK]   Firewall rules removed");

CleanupAndExit(serverProcess, 0);
return 0;


// ═══════════════════════════════════════════════════════════════
// Automation helpers
// ═══════════════════════════════════════════════════════════════

static string? FindPython()
{
    string[] candidates = { "python", "python3", "py" };
    foreach (var cmd in candidates)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("where", cmd)
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
            p!.WaitForExit(3000);
            string? path = p.StandardOutput.ReadLine();
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return path;
        }
        catch { }
    }
    return null;
}

static string GetLocalIP()
{
    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (ni.OperationalStatus != OperationalStatus.Up) continue;
        foreach (var ip in ni.GetIPProperties().UnicastAddresses)
        {
            if (ip.Address.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(ip.Address))
                return ip.Address.ToString();
        }
    }
    return "127.0.0.1";
}

static string GenerateRoomId()
{
    const string chars = "abcdefghjkmnpqrstuvwxyz23456789"; // no 0/O/1/I/l for readability
    var rng = Random.Shared;
    return new string(Enumerable.Range(0, 6).Select(_ => chars[rng.Next(chars.Length)]).ToArray());
}

static string? DiscoverServer()
{
    try
    {
        using var sock = new UdpClient();
        sock.EnableBroadcast = true;
        sock.Client.ReceiveTimeout = 3000;

        byte[] ping = "LANEMULATOR_DISCOVER"u8.ToArray();
        sock.Send(ping, ping.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));

        var remote = new IPEndPoint(IPAddress.Any, 0);
        byte[] response = sock.Receive(ref remote);
        return Encoding.UTF8.GetString(response);
    }
    catch { return null; }
}

static void SetupUpnp(int tcpPort, string localIP)
{
    try
    {
        Type? natType = Type.GetTypeFromProgID("HNetCfg.NATUPnP");
        if (natType == null) { Console.WriteLine("[INFO] UPnP not available"); return; }

        dynamic nat = Activator.CreateInstance(natType)!;
        dynamic mappings = nat.StaticPortMappingCollection;
        if (mappings == null) { Console.WriteLine("[INFO] UPnP: router doesn't support it"); return; }

        mappings.Add(tcpPort, "TCP", tcpPort, localIP, true, "LanEmulator Server");
        Console.WriteLine($"[OK]   UPnP: TCP {tcpPort} → {localIP}");
    }
    catch (Exception ex) { Console.WriteLine($"[INFO] UPnP: {ex.Message.GetType().Name}"); }
}

static void CleanupAndExit(Process? server, int code)
{
    if (server is { HasExited: false })
    {
        try { server.Kill(true); server.WaitForExit(3000); }
        catch { }
    }
    Environment.Exit(code);
}

static void RunSilent(string file, string args)
{
    try
    {
        var p = Process.Start(new ProcessStartInfo(file, args)
        { UseShellExecute = false, CreateNoWindow = true });
        p?.WaitForExit(5000);
    }
    catch { }
}


// ═══════════════════════════════════════════════════════════════
// Keepalive: re-register + re-poll for disconnect detection
// ═══════════════════════════════════════════════════════════════

static async Task KeepAliveAsync(HttpClient http, string roomId, int udpPort,
    List<PlayerInfo> peers, Dictionary<string, IPEndPoint> ipToPeer,
    object peerLock, CancellationToken ct)
{
    string myId = Environment.MachineName;
    var knownIds = new HashSet<string>();
    lock (peerLock) { foreach (var p in peers) knownIds.Add(p.player_id); }

    while (!ct.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(10_000, ct);
            var regReq = new { player_id = myId, room_id = roomId, udp_port = udpPort };
            await http.PostAsJsonAsync("/register", regReq, ct);
            var poll = await http.GetFromJsonAsync<PollResponse>($"/poll?room_id={Uri.EscapeDataString(roomId)}", ct);

            if (poll is not { status: "ready", players: not null }) continue;
            var current = poll.players.FindAll(p =>
                !string.Equals(p.player_id, myId, StringComparison.OrdinalIgnoreCase));
            var currentIds = new HashSet<string>(current.Select(p => p.player_id));

            lock (peerLock)
            {
                // Detect left peers
                var left = new List<string>();
                for (int i = peers.Count - 1; i >= 0; i--)
                {
                    if (!currentIds.Contains(peers[i].player_id))
                    {
                        left.Add($"{peers[i].player_id} ({peers[i].virtual_ip})");
                        ipToPeer.Remove(peers[i].virtual_ip);
                        RunRouteSilent($"delete {peers[i].virtual_ip}");
                        peers.RemoveAt(i);
                    }
                }
                foreach (var msg in left)
                    Console.WriteLine($"\n[PEER LEFT] {msg}");

                // Detect new peers
                foreach (var p in current)
                {
                    if (!knownIds.Contains(p.player_id))
                    {
                        peers.Add(p);
                        ipToPeer[p.virtual_ip] = new IPEndPoint(IPAddress.Parse(p.ip), p.udp_port);
                        Console.WriteLine($"\n[PEER JOIN] {p.player_id} VPN: {p.virtual_ip}");
                    }
                }
            }
            knownIds = currentIds;
        }
        catch (OperationCanceledException) { break; }
        catch { }
    }
}


// ═══════════════════════════════════════════════════════════════
// Packet pumps
// ═══════════════════════════════════════════════════════════════

static void PumpNetworkToTun(UdpClient udp, IntPtr session, CancellationToken ct)
{
    try
    {
        while (!ct.IsCancellationRequested)
        {
            var task = udp.ReceiveAsync(ct);
            task.AsTask().Wait(ct);
            byte[] packet = task.Result.Buffer;
            if (packet.Length == 0) continue;

            IntPtr sendBuf = WintunAllocateSendPacket(session, (uint)packet.Length);
            if (sendBuf == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 111) continue;
                if (err == 38) break;
                continue;
            }
            Marshal.Copy(packet, 0, sendBuf, packet.Length);
            WintunSendPacket(session, sendBuf);
        }
    }
    catch (OperationCanceledException) { }
}

static void PumpTunToNet(UdpClient udp, IntPtr session,
    Dictionary<string, IPEndPoint> ipToPeer, object peerLock, CancellationToken ct)
{
    IntPtr readEvent = WintunGetReadWaitEvent(session);
    if (readEvent == IntPtr.Zero) return;

    var wh = new EventWaitHandle(false, EventResetMode.AutoReset);
    wh.SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(readEvent, ownsHandle: false);
    var arr = new[] { readEvent };

    try
    {
        while (!ct.IsCancellationRequested)
        {
            while (true)
            {
                IntPtr packet = WintunReceivePacket(session, out uint sz);
                if (packet == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == 259) break;
                    if (err == 38) return;
                    break;
                }

                byte[] buf = new byte[sz];
                Marshal.Copy(packet, buf, 0, (int)sz);

                if (buf.Length >= 20)
                {
                    var dst = new IPAddress(buf[16..20]);
                    lock (peerLock)
                    {
                        if (ipToPeer.TryGetValue(dst.ToString(), out var ep))
                            udp.Send(buf, buf.Length, ep);
                        else if (IsBroadcast(dst))
                            foreach (var kv in ipToPeer) udp.Send(buf, buf.Length, kv.Value);
                    }
                }
                WintunReleaseReceivePacket(session, packet);
            }
            if ((int)WaitForMultipleObjects(1, arr, false, 500u) == -1) break;
        }
    }
    catch (OperationCanceledException) { }
    finally { wh.SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(IntPtr.Zero, ownsHandle: false); }
}

static bool IsBroadcast(IPAddress ip)
{
    byte[] b = ip.GetAddressBytes();
    return (b[0] == 255 && b[3] == 255) || (b[3] == 255);
}


// ═══════════════════════════════════════════════════════════════
// Helpers
// ═══════════════════════════════════════════════════════════════

static bool IsAdministrator()
{
    using var id = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
}

static string Win32Msg(int c) => new Win32Exception(c).Message;

static void RunNetsh(string args)
{
    using var p = Process.Start(new ProcessStartInfo("netsh", args)
    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
    p.WaitForExit(10_000);
    foreach (string l in p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        Console.WriteLine($"       {l.TrimEnd()}");
    string e = p.StandardError.ReadToEnd().Trim();
    if (e.Length > 0) Console.Error.WriteLine($"   [ERR] {e}");
}

static void RunRoute(string args)
{
    Console.WriteLine($"   [route] route {args}");
    using var p = Process.Start(new ProcessStartInfo("route", args)
    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
    p.WaitForExit(5000);
    string o = p.StandardOutput.ReadToEnd().Trim();
    if (o.Length > 0) Console.WriteLine($"       {o}");
}

static void RunRouteSilent(string args)
{
    try { using var p = Process.Start(new ProcessStartInfo("route", args)
    { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true }); p?.WaitForExit(5000); }
    catch { }
}

static string GetInterfaceAlias(ulong luid)
{
    char[] b = new char[512];
    if (ConvertInterfaceLuidToAlias(ref luid, b, (nuint)(b.Length * 2)) != 0)
        throw new Win32Exception(Marshal.GetLastWin32Error());
    int e = Array.IndexOf(b, '\0');
    return new string(b, 0, e >= 0 ? e : b.Length);
}

static string? AutoDetectAppId(string gameDir, string gameExePath)
{
    foreach (string path in new[] { Path.Combine(gameDir, "steam_appid.txt"),
        Path.Combine(gameDir, "steam_settings", "steam_appid.txt") })
    {
        if (File.Exists(path))
        {
            string? n = ExtractNumber(File.ReadAllText(path).Trim());
            if (n != null) { Console.WriteLine($"[INFO] AppID from file: {n}"); return n; }
        }
    }
    string? dll = File.Exists(Path.Combine(gameDir, "steam_api64.dll"))
        ? Path.Combine(gameDir, "steam_api64.dll")
        : File.Exists(Path.Combine(gameDir, "steam_api64.dll.bak"))
            ? Path.Combine(gameDir, "steam_api64.dll.bak") : null;
    if (dll != null)
    {
        try { string? found = ScanDllForAppId(File.ReadAllBytes(dll));
              if (found != null) { Console.WriteLine($"[INFO] AppID from DLL: {found}"); return found; } } catch { }
    }
    foreach (string ini in Directory.GetFiles(gameDir, "*.ini"))
    {
        try
        {
            foreach (string line in File.ReadAllLines(ini))
                if (line.StartsWith("SteamAppId=", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("AppId=", StringComparison.OrdinalIgnoreCase))
                {
                    string? n = ExtractNumber(line.Split('=')[1]);
                    if (n != null) { Console.WriteLine($"[INFO] AppID from {Path.GetFileName(ini)}: {n}"); return n; }
                }
        } catch { }
    }
    return null;
}

static string? ExtractNumber(string t) { var m = System.Text.RegularExpressions.Regex.Match(t, @"\d+"); return m.Success && m.Value != "0" ? m.Value : null; }

static string? ScanDllForAppId(byte[] dll)
{
    string t = Encoding.ASCII.GetString(dll);
    int i = t.IndexOf("steam_appid", StringComparison.OrdinalIgnoreCase);
    if (i < 0) return null;
    string nb = t[Math.Max(0, i - 32)..Math.Min(t.Length, i + 128)];
    var m = System.Text.RegularExpressions.Regex.Match(nb, @"(\d{2,7})");
    if (!m.Success) return null;
    int v = int.Parse(m.Value);
    return v >= 10 && v <= 9999999 ? m.Value : null;
}

static async Task<string?> SteamSearchAppId(string gameName)
{
    try
    {
        using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        h.DefaultRequestHeaders.Add("User-Agent", "LanEmulator/1.0");
        var r = await h.GetStringAsync($"https://steamcommunity.com/actions/SearchApps/{Uri.EscapeDataString(gameName)}");
        using var j = System.Text.Json.JsonDocument.Parse(r);
        if (j.RootElement.GetArrayLength() == 0) return null;
        var items = j.RootElement.EnumerateArray().ToArray();

        foreach (var it in items)
        {
            string? nm = it.GetProperty("name").GetString();
            int aid = it.GetProperty("appid").GetInt32();
            if (string.Equals(nm, gameName, StringComparison.OrdinalIgnoreCase))
            { Console.WriteLine($"[INFO] Steam: '{nm}' → {aid}"); return aid.ToString(); }
        }
        if (items.Length == 1)
        { Console.WriteLine($"[INFO] Steam: '{items[0].GetProperty("name").GetString()}' → {items[0].GetProperty("appid").GetInt32()}"); return items[0].GetProperty("appid").GetInt32().ToString(); }

        Console.WriteLine("   Multiple results:");
        for (int i = 0; i < Math.Min(items.Length, 5); i++)
            Console.WriteLine($"     [{i + 1}] {items[i].GetProperty("name").GetString()} ({items[i].GetProperty("appid").GetInt32()})");
        Console.Write("   Pick number (Enter=first): ");
        string? ch = Console.ReadLine()?.Trim();
        int idx = 0;
        if (!string.IsNullOrEmpty(ch) && int.TryParse(ch, out int p) && p >= 1 && p <= Math.Min(items.Length, 5)) idx = p - 1;
        string cn = items[idx].GetProperty("name").GetString()!;
        int ci = items[idx].GetProperty("appid").GetInt32();
        Console.WriteLine($"[INFO] Selected: {cn} → {ci}");
        return ci.ToString();
    }
    catch { return null; }
}


// ═══════════════════════════════════════════════════════════════
// P/Invoke — Wintun
// ═══════════════════════════════════════════════════════════════

[DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
static extern IntPtr WintunCreateAdapter(string Name, string TunnelType, IntPtr RequestedGUID);

[DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
static extern IntPtr WintunOpenAdapter(string Name);

[DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall)]
static extern void WintunCloseAdapter(IntPtr Adapter);

[DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool WintunGetAdapterLUID(IntPtr Adapter, out ulong Luid);

[DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall)]
static extern uint WintunGetRunningDriverVersion();

[DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall)]
static extern IntPtr WintunStartSession(IntPtr Adapter, uint Capacity);

[DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall)]
static extern void WintunEndSession(IntPtr Session);

[DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall)]
static extern IntPtr WintunGetReadWaitEvent(IntPtr Session);

[DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall)]
static extern IntPtr WintunReceivePacket(IntPtr Session, out uint PacketSize);

[DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall)]
static extern void WintunReleaseReceivePacket(IntPtr Session, IntPtr Packet);

[DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall)]
static extern IntPtr WintunAllocateSendPacket(IntPtr Session, uint PacketSize);

[DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall)]
static extern void WintunSendPacket(IntPtr Session, IntPtr Packet);

[DllImport("iphlpapi.dll", CharSet = CharSet.Unicode)]
static extern uint ConvertInterfaceLuidToAlias(ref ulong InterfaceLuid, [Out] char[] InterfaceAlias, nuint Length);

[DllImport("kernel32.dll", SetLastError = true)]
static extern uint WaitForMultipleObjects(uint nCount, IntPtr[] lpHandles, [MarshalAs(UnmanagedType.Bool)] bool fWaitAll, uint dwMilliseconds);


// ═══════════════════════════════════════════════════════════════
// Server models (MUST be last — CS8803)
// ═══════════════════════════════════════════════════════════════

record RegisterResponse(string status, string room_id, string? virtual_ip, int player_count);
record PollResponse(string status, int player_count, List<PlayerInfo>? players);
record PlayerInfo(string player_id, string ip, int udp_port, string virtual_ip);
