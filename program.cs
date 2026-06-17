using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

// ═══════════════════════════════════════════════════════════════
// Wintun Virtual LAN Emulator v1.0.0
// .NET 8 | x64 only | Requires Administrator
// ═══════════════════════════════════════════════════════════════

const string Version = "1.0.0";
const string AdapterName = "LanEmulatorTun";
const string AdapterMask = "255.255.255.0";
const int    PrefixLength = 24;
const int    UdpPort = 51820;

// Server URL: first CLI arg (unless it's --help/-h)
if (args.Length > 0 && (args[0] == "--help" || args[0] == "-h" || args[0] == "/?"))
{
    Console.WriteLine($"LanEmulator v{Version} — Virtual LAN for gaming");
    Console.WriteLine("Usage: LanEmulator.exe [server_url]");
    Console.WriteLine("  server_url  Signaling server address (default: http://127.0.0.1:8000)");
    Console.WriteLine("Examples:");
    Console.WriteLine("  LanEmulator.exe");
    Console.WriteLine("  LanEmulator.exe http://192.168.1.50:8000");
    Console.WriteLine("  LanEmulator.exe https://my-signal-server.onrender.com");
    return 0;
}

string signalServerUrl = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
    ? args[0].TrimEnd('/')
    : "http://127.0.0.1:8000";

// ── 1. Admin check ────────────────────────────────────────────
if (!IsAdministrator())
{
    Console.Error.WriteLine("[FATAL] Administrator privileges required.");
    return 1;
}

// ── 2. Verify wintun.dll ──────────────────────────────────────
string wintunPath = Path.Combine(AppContext.BaseDirectory, "wintun.dll");
if (!File.Exists(wintunPath))
{
    Console.Error.WriteLine($"[FATAL] wintun.dll not found at: {wintunPath}");
    return 2;
}

Console.WriteLine($"=== Wintun LAN Emulator v{Version} ===");
Console.WriteLine($"    Adapter : {AdapterName}");
Console.WriteLine($"    IP      : assigned by server");
Console.WriteLine($"    UDP     : 0.0.0.0:{UdpPort}");
Console.WriteLine($"    Server  : {signalServerUrl}");
Console.WriteLine();

// ── 3. Select mode ────────────────────────────────────────────
int mode = 0;
while (mode != 1 && mode != 2)
{
    Console.WriteLine("Select mode:");
    Console.WriteLine("  [1] Steam Game (Goldberg auto-patcher)");
    Console.WriteLine("  [2] Pure LAN (no patching — VPN only)");
    Console.Write("> ");
    string? input = Console.ReadLine()?.Trim();
    if (int.TryParse(input, out mode) && (mode == 1 || mode == 2))
        break;
    Console.WriteLine("   Please enter 1 or 2.");
}
Console.WriteLine($"[OK]   Mode: {(mode == 1 ? "Steam Game (Goldberg)" : "Pure LAN (VPN only)")}");
Console.WriteLine();

// ── 4. Prompt for Room ID ─────────────────────────────────────
Console.Write("Room ID: ");
string? roomId = Console.ReadLine()?.Trim();
if (string.IsNullOrWhiteSpace(roomId))
{
    Console.Error.WriteLine("[FATAL] Room ID is required.");
    return 6;
}
Console.WriteLine($"[OK]   Room: '{roomId}'");

// ── 5. Prompt for Game Executable (Steam mode only) ──────────
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
            return 8;
        }
        gameDir = Path.GetDirectoryName(gameExePath)!;
        Console.WriteLine($"[OK]   Game: {gameExePath}");
    }
}

// ── 6. Register with signaling server ─────────────────────────
using var http = new HttpClient { BaseAddress = new Uri(signalServerUrl) };
http.Timeout = TimeSpan.FromSeconds(10);

RegisterResponse? reg;
try
{
    var regReq = new { player_id = Environment.MachineName, room_id = roomId, udp_port = UdpPort };
    var regResp = await http.PostAsJsonAsync("/register", regReq);
    regResp.EnsureSuccessStatusCode();
    reg = await regResp.Content.ReadFromJsonAsync<RegisterResponse>();
    Console.WriteLine($"[OK]   Registered as '{regReq.player_id}' (players in room: {reg?.player_count})");
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"[FATAL] Cannot reach signaling server: {signalServerUrl}");
    Console.Error.WriteLine($"       {ex.Message}");
    return 7;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[FATAL] Registration failed: {ex.Message}");
    return 7;
}

string myVirtualIP = reg?.virtual_ip ?? "10.13.37.1";
Console.WriteLine($"[OK]   Assigned virtual IP: {myVirtualIP}");

// ── 7. Poll until at least one peer joins ────────────────────
var peers = new List<PlayerInfo>();
var peerLock = new object();
var ipToPeer = new Dictionary<string, IPEndPoint>();

Console.WriteLine();
Console.Write("[*]   Waiting for peers");
int pollRetries = 0;
const int maxPollRetries = 60;  // 2 minutes total

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
                Console.WriteLine($"[OK]   {found.Count} peer(s) in room:");
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
        pollRetries = 0;  // reset on success
    }
    catch (HttpRequestException)
    {
        pollRetries++;
        if (pollRetries >= maxPollRetries)
        {
            Console.Error.WriteLine($"\n[FATAL] Signaling server unreachable after {maxPollRetries * 2}s.");
            Console.Error.WriteLine($"        Check: is '{signalServerUrl}' running?");
            return 7;
        }
        Console.Write("?");
    }
    catch (Exception ex)
    {
        pollRetries++;
        if (pollRetries >= maxPollRetries)
        {
            Console.Error.WriteLine($"\n[FATAL] Poll failed after {maxPollRetries * 2}s: {ex.Message}");
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

// ── 8. Goldberg Auto-Patcher (Steam mode only) ────────────────
if (mode == 1 && gameDir != null)
{
    Console.WriteLine();
    Console.WriteLine("─── Goldberg Auto-Patcher ────────────────────────────");

    string goldbergSrc = Path.Combine(AppContext.BaseDirectory, "goldberg", "steam_api64.dll");
    string targetDll = Path.Combine(gameDir, "steam_api64.dll");
    string settingsDst = Path.Combine(gameDir, "steam_settings");

    // 8a. Auto-detect Steam AppID
    string? appId = AutoDetectAppId(gameDir, gameExePath!);
    if (string.IsNullOrEmpty(appId) || appId == "0")
    {
        string gameName = Path.GetFileName(gameDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        appId = await SteamSearchAppId(gameName);
    }

    if (!string.IsNullOrEmpty(appId) && appId != "0")
        Console.WriteLine($"[OK]   Detected Steam AppID: {appId}");
    else
    {
        appId = "0";
        Console.WriteLine("[WARN] Could not auto-detect Steam AppID.");
        Console.WriteLine($"       Edit {settingsDst}\\steam_appid.txt after first run.");
    }

    // 8b. Backup original DLL
    string backupPath = targetDll + ".bak";
    if (File.Exists(targetDll) && !File.Exists(backupPath))
    {
        File.Move(targetDll, backupPath);
        Console.WriteLine($"[OK]   Backed up original DLL → steam_api64.dll.bak");
    }
    else if (File.Exists(backupPath))
        Console.WriteLine("[INFO] Backup already exists, replacing target…");

    // 8c. Copy goldberg DLL
    if (File.Exists(goldbergSrc))
    {
        File.Copy(goldbergSrc, targetDll, overwrite: true);
        Console.WriteLine("[OK]   Goldberg steam_api64.dll deployed");
    }
    else
    {
        Console.Error.WriteLine($"[WARN] Goldberg DLL not found at: {goldbergSrc}");
    }

    // 8d. Write INI config
    string iniPath = Path.Combine(gameDir, "GoldbergSteamEmu.ini");
    string firstPeerVPN;
    lock (peerLock) { firstPeerVPN = peers.Count > 0 ? peers[0].virtual_ip : "10.13.37.2"; }
    File.WriteAllText(iniPath, $"[Networking]\nip={firstPeerVPN}\n");
    Console.WriteLine($"[OK]   Goldberg INI written → peer IP: {firstPeerVPN}");

    // 8e. Copy steam_settings + write appid
    string settingsSrc = Path.Combine(AppContext.BaseDirectory, "goldberg", "steam_settings");
    if (Directory.Exists(settingsSrc))
    {
        Directory.CreateDirectory(settingsDst);
        foreach (string file in Directory.GetFiles(settingsSrc, "*", SearchOption.AllDirectories))
        {
            string relative = file[(settingsSrc.Length + 1)..];
            string dstFile = Path.Combine(settingsDst, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);
            File.Copy(file, dstFile, overwrite: true);
        }
        Console.WriteLine("[OK]   steam_settings/ deployed to game folder");
    }
    else
        Directory.CreateDirectory(settingsDst);

    File.WriteAllText(Path.Combine(settingsDst, "steam_appid.txt"), appId);
    Console.WriteLine($"[OK]   steam_appid.txt → {appId}");
    Console.WriteLine("──────────────────────────────────────────────────────");
}

// ── 9. Create UDP socket (shared: hole punch + pumps) ────────
using var udp = new UdpClient(UdpPort);
udp.Client.ReceiveBufferSize = 4 * 1024 * 1024;
udp.Client.SendBufferSize    = 4 * 1024 * 1024;
Console.WriteLine($"[OK]   UDP socket bound to 0.0.0.0:{UdpPort}");

// ── 10. UDP Hole Punching (all peers) ─────────────────────────
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
        {
            udp.Send(holePunchData, holePunchData.Length, ep);
            punchCount++;
            Console.Write(".");
        }
    }
    catch (Exception ex)
    {
        Console.Write($"!({ex.Message.GetHashCode():X4})");
    }
    await Task.Delay(100, CancellationToken.None);
}
Console.WriteLine($" {punchCount} packets sent to {peersSnapshot.Count} peer(s)");

// ── 11. Driver version ────────────────────────────────────────
uint ver = WintunGetRunningDriverVersion();
if (ver == 0)
{
    Console.WriteLine("[WARN] Wintun driver not detected.");
    Console.WriteLine("       Download and install from: https://www.wintun.net/");
}
else
    Console.WriteLine($"[OK]   Wintun driver v{ver >> 16}.{ver & 0xFFFF}");

// ── 12. Clean stale adapter ───────────────────────────────────
IntPtr existing = WintunOpenAdapter(AdapterName);
if (existing != IntPtr.Zero)
{
    Console.WriteLine("[INFO] Stale adapter found, closing…");
    WintunCloseAdapter(existing);
    Thread.Sleep(500);
}

// ── 13. Create virtual adapter ────────────────────────────────
IntPtr adapter = WintunCreateAdapter(AdapterName, "LanEmulator", IntPtr.Zero);
if (adapter == IntPtr.Zero)
{
    int err = Marshal.GetLastWin32Error();
    Console.Error.WriteLine($"[FATAL] WintunCreateAdapter failed. Win32 error {err} (0x{err:X8}): {Win32Msg(err)}");
    if (err == 1168 || err == 2 || err == 126)
        Console.Error.WriteLine("       Wintun driver may not be installed. Download: https://www.wintun.net/");
    return 3;
}
Console.WriteLine($"[OK]   Adapter '{AdapterName}' created.");

// ── 14. Get LUID → interface alias ────────────────────────────
if (!WintunGetAdapterLUID(adapter, out ulong luid))
{
    int err = Marshal.GetLastWin32Error();
    Console.Error.WriteLine($"[FATAL] WintunGetAdapterLUID failed (0x{err:X8})");
    WintunCloseAdapter(adapter);
    return 4;
}
Console.WriteLine($"[OK]   LUID: 0x{luid:X16}");

string ifAlias = GetInterfaceAlias(luid);
Console.WriteLine($"[OK]   Interface alias: \"{ifAlias}\"");

// ── 15. Assign IP & bring up ──────────────────────────────────
RunNetsh($"interface ip set address name=\"{ifAlias}\" source=static addr={myVirtualIP} mask={AdapterMask}");
RunNetsh($"interface set interface name=\"{ifAlias}\" admin=enabled");
Console.WriteLine($"[OK]   IP {myVirtualIP}/{PrefixLength} assigned, interface UP");

// ── 16. Add routes for all peers ──────────────────────────────
lock (peerLock)
{
    foreach (var p in peers)
        RunRoute($"add {p.virtual_ip} mask 255.255.255.255 {myVirtualIP} metric 1");
}
Console.WriteLine($"[OK]   Routes added for {peersSnapshot.Count} peer(s) → via {myVirtualIP}");

// ── 17. Start Wintun session (Ring Buffer) ────────────────────
const uint RingCapacity = 0x400000;
IntPtr session = WintunStartSession(adapter, RingCapacity);
if (session == IntPtr.Zero)
{
    int err = Marshal.GetLastWin32Error();
    Console.Error.WriteLine($"[FATAL] WintunStartSession failed. Win32 error {err} (0x{err:X8}): {Win32Msg(err)}");
    WintunCloseAdapter(adapter);
    return 5;
}
Console.WriteLine($"[OK]   Wintun session started (ring: {RingCapacity / 1024 / 1024} MiB)");

// ── 18. Launch packet pumps + keepalive ───────────────────────
using var cts = new CancellationTokenSource();

var pumpNetToTun = new Thread(() => PumpNetworkToTun(udp, session, cts.Token))
{
    Name = "Net→Tun", IsBackground = true
};
var pumpTunToNet = new Thread(() => PumpTunToNet(udp, session, ipToPeer, peerLock, cts.Token))
{
    Name = "Tun→Net", IsBackground = true
};

pumpNetToTun.Start();
pumpTunToNet.Start();
Console.WriteLine("[OK]   Packet pump threads running");

// Background re-register + re-poll for disconnect detection
var keepaliveTask = KeepAliveAsync(http, roomId, UdpPort, peers, ipToPeer, peerLock, cts.Token);

// ── 19. Launch the game (Steam mode only) ─────────────────────
Process? gameProcess = null;
if (mode == 1 && gameExePath != null)
{
    try
    {
        gameProcess = Process.Start(new ProcessStartInfo(gameExePath)
        {
            WorkingDirectory = gameDir,
            UseShellExecute = false
        });
        Console.WriteLine($"[OK]   Game launched (PID: {gameProcess?.Id})");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[WARN] Failed to launch game: {ex.Message}");
    }
}

// ── 20. Display & wait for ESC ────────────────────────────────
Console.WriteLine();
Console.WriteLine("╔═══════════════════════════════════════════════════════╗");
if (mode == 1)
    Console.WriteLine("║  VPN + Goldberg ACTIVE — Game should be running.     ║");
else
    Console.WriteLine("║  Virtual LAN ACTIVE — Launch your game manually.     ║");
Console.WriteLine("║                                                       ║");
Console.WriteLine($"║  My  IP    : {myVirtualIP}/{PrefixLength,-30}║");

lock (peerLock)
{
    Console.WriteLine($"║  Peers     : {peers.Count,-38}║");
    for (int i = 0; i < Math.Min(peers.Count, 5); i++)
    {
        var p = peers[i];
        Console.WriteLine($"║    [{i + 1}] {p.player_id,-12} {p.virtual_ip,-16} {p.ip,-16}║");
    }
    if (peers.Count > 5)
        Console.WriteLine($"║    ... and {peers.Count - 5} more                              ║");
}
Console.WriteLine($"║  UDP       : 0.0.0.0:{UdpPort,-31}║");
Console.WriteLine($"║  Room      : {roomId,-38}║");
Console.WriteLine($"║  Mode      : {(mode == 1 ? "Steam + Goldberg" : "Pure LAN"),-38}║");
if (gameProcess != null)
    Console.WriteLine($"║  Game PID  : {gameProcess.Id,-38}║");
Console.WriteLine("║                                                       ║");
Console.WriteLine("║  Press ESC to shutdown…                              ║");
Console.WriteLine("╚═══════════════════════════════════════════════════════╝");

while (Console.ReadKey(true).Key != ConsoleKey.Escape) { }

// ── 21. Cleanup ───────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("[*] Shutting down…");

cts.Cancel();

// Notify server
try { await http.PostAsync($"/leave?room_id={Uri.EscapeDataString(roomId)}&player_id={Uri.EscapeDataString(Environment.MachineName)}", null); }
catch { /* best effort */ }

// Kill game (Steam mode)
if (mode == 1 && gameProcess is { HasExited: false })
{
    Console.WriteLine($"[*]   Terminating game (PID {gameProcess.Id})…");
    try
    {
        gameProcess.Kill(entireProcessTree: true);
        gameProcess.WaitForExit(5000);
        Console.WriteLine("[OK]   Game process terminated.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[WARN] Failed to kill game: {ex.Message}");
    }
}

// Wait for pumps & keepalive
try { await keepaliveTask; } catch { }

pumpNetToTun.Join(3000);
pumpTunToNet.Join(3000);

WintunEndSession(session);
Console.WriteLine("[OK]   Wintun session ended.");

WintunCloseAdapter(adapter);
Console.WriteLine("[OK]   Adapter closed and removed from system.");

udp.Close();
Console.WriteLine("[OK]   UDP socket closed.");

lock (peerLock)
{
    foreach (var p in peers)
        RunRoute($"delete {p.virtual_ip}");
}
Console.WriteLine($"[OK]   Routes removed.");

Console.WriteLine("[DONE] Cleanup complete.");
return 0;


// ═══════════════════════════════════════════════════════════════
// Keepalive: re-register + re-poll for disconnect detection
// ═══════════════════════════════════════════════════════════════

static async Task KeepAliveAsync(
    HttpClient http, string roomId, int udpPort,
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

            // Re-register to refresh server presence
            var regReq = new { player_id = myId, room_id = roomId, udp_port = udpPort };
            await http.PostAsJsonAsync("/register", regReq, ct);

            // Poll for changes
            var poll = await http.GetFromJsonAsync<PollResponse>(
                $"/poll?room_id={Uri.EscapeDataString(roomId)}", ct);

            if (poll is not { status: "ready", players: not null }) continue;

            var current = poll.players.FindAll(p =>
                !string.Equals(p.player_id, myId, StringComparison.OrdinalIgnoreCase));

            var currentIds = new HashSet<string>();
            foreach (var p in current) currentIds.Add(p.player_id);

            // Detect peers who left
            lock (peerLock)
            {
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
                        Console.WriteLine($"\n[PEER JOIN] {p.player_id} VPN: {p.virtual_ip} Public: {p.ip}:{p.udp_port}");
                    }
                }
            }

            knownIds = currentIds;
        }
        catch (OperationCanceledException) { break; }
        catch { /* transient error — retry next cycle */ }
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
            var result = task.Result;
            byte[] packet = result.Buffer;

            if (packet.Length == 0) continue;

            IntPtr sendBuf = WintunAllocateSendPacket(session, (uint)packet.Length);
            if (sendBuf == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 111) continue;
                if (err == 38) break;
                Console.Error.WriteLine($"   [WARN] AllocateSendPacket failed: {err}");
                continue;
            }

            Marshal.Copy(packet, 0, sendBuf, packet.Length);
            WintunSendPacket(session, sendBuf);
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[ERR] Net→Tun pump: {ex.Message}");
    }
}

static void PumpTunToNet(UdpClient udp, IntPtr session,
    Dictionary<string, IPEndPoint> ipToPeer, object peerLock, CancellationToken ct)
{
    IntPtr readEvent = WintunGetReadWaitEvent(session);
    if (readEvent == IntPtr.Zero)
    {
        Console.Error.WriteLine("[ERR] WintunGetReadWaitEvent returned NULL");
        return;
    }

    var waitHandle = new EventWaitHandle(false, EventResetMode.AutoReset);
    waitHandle.SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(readEvent, ownsHandle: false);
    var readEventArray = new[] { readEvent };

    try
    {
        while (!ct.IsCancellationRequested)
        {
            while (true)
            {
                IntPtr packet = WintunReceivePacket(session, out uint packetSize);
                if (packet == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == 259) break;
                    if (err == 38) return;
                    break;
                }

                byte[] buffer = new byte[packetSize];
                Marshal.Copy(packet, buffer, 0, (int)packetSize);

                if (buffer.Length >= 20)
                {
                    var dstIP = new IPAddress(buffer[16..20]);

                    lock (peerLock)
                    {
                        if (ipToPeer.TryGetValue(dstIP.ToString(), out var ep))
                        {
                            udp.Send(buffer, buffer.Length, ep);
                        }
                        else if (IsBroadcast(dstIP))
                        {
                            // Forward broadcast to all peers
                            foreach (var kv in ipToPeer)
                                udp.Send(buffer, buffer.Length, kv.Value);
                        }
                        // else: unknown destination — drop
                    }
                }

                WintunReleaseReceivePacket(session, packet);
            }

            int signaled = (int)WaitForMultipleObjects(1, readEventArray, false, 500u);
            if (signaled == unchecked((int)0xFFFFFFFF)) break;
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        if (!ct.IsCancellationRequested)
            Console.Error.WriteLine($"[ERR] Tun→Net pump: {ex.Message}");
    }
    finally
    {
        waitHandle.SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(IntPtr.Zero, ownsHandle: false);
    }
}

static bool IsBroadcast(IPAddress ip)
{
    byte[] b = ip.GetAddressBytes();
    // 255.255.255.255 (limited broadcast) or x.x.x.255 (subnet broadcast /24)
    return b[0] == 255 && b[1] == 255 && b[2] == 255 && b[3] == 255
        || (b.Length == 4 && b[3] == 255);
}


// ═══════════════════════════════════════════════════════════════
// Helpers
// ═══════════════════════════════════════════════════════════════

static bool IsAdministrator()
{
    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
}

static string Win32Msg(int code) => new Win32Exception(code).Message;

static void RunNetsh(string args)
{
    using var proc = new Process
    {
        StartInfo = new ProcessStartInfo("netsh", args)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        }
    };
    proc.Start();
    proc.WaitForExit(10_000);

    string stdout = proc.StandardOutput.ReadToEnd().Trim();
    string stderr = proc.StandardError.ReadToEnd().Trim();

    foreach (string line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        Console.WriteLine($"       {line.TrimEnd()}");
    if (stderr.Length > 0)
        Console.Error.WriteLine($"   [ERR] {stderr}");
    if (proc.ExitCode != 0)
        Console.Error.WriteLine($"   [WARN] netsh exit code: {proc.ExitCode}");
}

static void RunRoute(string args)
{
    Console.WriteLine($"   [route] route {args}");
    using var proc = new Process
    {
        StartInfo = new ProcessStartInfo("route", args)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        }
    };
    proc.Start();
    proc.WaitForExit(5000);

    string stdout = proc.StandardOutput.ReadToEnd().Trim();
    string stderr = proc.StandardError.ReadToEnd().Trim();

    if (stdout.Length > 0) Console.WriteLine($"       {stdout}");
    if (stderr.Length > 0) Console.Error.WriteLine($"   [ERR] {stderr}");
    if (proc.ExitCode != 0) Console.Error.WriteLine($"   [WARN] route exit code: {proc.ExitCode}");
}

static void RunRouteSilent(string args)
{
    using var proc = new Process
    {
        StartInfo = new ProcessStartInfo("route", args)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        }
    };
    proc.Start();
    proc.WaitForExit(5000);
    // silent — no console output
}

static string GetInterfaceAlias(ulong luid)
{
    char[] buffer = new char[512];
    nuint byteSize = (nuint)(buffer.Length * sizeof(char));
    uint ret = ConvertInterfaceLuidToAlias(ref luid, buffer, byteSize);
    if (ret != 0)
        throw new Win32Exception((int)ret, "ConvertInterfaceLuidToAlias failed");

    int end = Array.IndexOf(buffer, '\0');
    return new string(buffer, 0, end >= 0 ? end : buffer.Length);
}

static string? AutoDetectAppId(string gameDir, string gameExePath)
{
    string[] candidates =
    {
        Path.Combine(gameDir, "steam_appid.txt"),
        Path.Combine(gameDir, "steam_settings", "steam_appid.txt"),
    };

    foreach (string path in candidates)
    {
        if (File.Exists(path))
        {
            string content = File.ReadAllText(path).Trim();
            string? extracted = ExtractNumber(content);
            if (extracted != null)
            {
                Console.WriteLine($"[INFO] Found AppID in {Path.GetFileName(path)}: {extracted}");
                return extracted;
            }
        }
    }

    string dllPath = Path.Combine(gameDir, "steam_api64.dll");
    string bakPath = dllPath + ".bak";
    string? scanPath = File.Exists(dllPath) ? dllPath : File.Exists(bakPath) ? bakPath : null;

    if (scanPath != null)
    {
        try
        {
            byte[] dllBytes = File.ReadAllBytes(scanPath);
            string? found = ScanDllForAppId(dllBytes);
            if (found != null)
            {
                Console.WriteLine($"[INFO] Extracted AppID from steam_api64.dll: {found}");
                return found;
            }
        }
        catch { }
    }

    foreach (string iniFile in Directory.GetFiles(gameDir, "*.ini"))
    {
        try
        {
            foreach (string line in File.ReadAllLines(iniFile))
            {
                if (line.StartsWith("SteamAppId=", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("AppId=", StringComparison.OrdinalIgnoreCase))
                {
                    string? num = ExtractNumber(line.Split('=')[1]);
                    if (num != null)
                    {
                        Console.WriteLine($"[INFO] Found AppID in {Path.GetFileName(iniFile)}: {num}");
                        return num;
                    }
                }
            }
        }
        catch { }
    }

    return null;
}

static string? ExtractNumber(string text)
{
    var match = System.Text.RegularExpressions.Regex.Match(text, @"\d+");
    return match.Success && match.Value != "0" ? match.Value : null;
}

static string? ScanDllForAppId(byte[] dllBytes)
{
    string text = System.Text.Encoding.ASCII.GetString(dllBytes);
    int steamAppIdIdx = text.IndexOf("steam_appid", StringComparison.OrdinalIgnoreCase);
    if (steamAppIdIdx >= 0)
    {
        string nearby = text[Math.Max(0, steamAppIdIdx - 32)..Math.Min(text.Length, steamAppIdIdx + 128)];
        var match = System.Text.RegularExpressions.Regex.Match(nearby, @"(\d{2,7})");
        if (match.Success)
        {
            int val = int.Parse(match.Value);
            if (val >= 10 && val <= 9999999) return match.Value;
        }
    }

    return null;
}

static async Task<string?> SteamSearchAppId(string gameName)
{
    try
    {
        string url = $"https://steamcommunity.com/actions/SearchApps/{Uri.EscapeDataString(gameName)}";
        using var steamHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        steamHttp.DefaultRequestHeaders.Add("User-Agent", "LanEmulator/1.0");
        var resp = await steamHttp.GetStringAsync(url);
        using var json = System.Text.Json.JsonDocument.Parse(resp);

        if (json.RootElement.GetArrayLength() == 0) return null;

        var results = json.RootElement.EnumerateArray().ToArray();
        if (results.Length == 0) return null;

        foreach (var item in results)
        {
            string? name = item.GetProperty("name").GetString();
            int appId = item.GetProperty("appid").GetInt32();
            if (string.Equals(name, gameName, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[INFO] Steam: exact match '{name}' → AppID {appId}");
                return appId.ToString();
            }
        }

        if (results.Length == 1)
        {
            string name = results[0].GetProperty("name").GetString()!;
            int appId = results[0].GetProperty("appid").GetInt32();
            Console.WriteLine($"[INFO] Steam: '{name}' → AppID {appId}");
            return appId.ToString();
        }

        Console.WriteLine("   Multiple Steam results found:");
        for (int i = 0; i < Math.Min(results.Length, 5); i++)
        {
            string name = results[i].GetProperty("name").GetString()!;
            int appId = results[i].GetProperty("appid").GetInt32();
            Console.WriteLine($"     [{i + 1}] {name} ({appId})");
        }
        Console.Write("   Pick number (or Enter for first): ");
        string? choice = Console.ReadLine()?.Trim();
        int idx = 0;
        if (!string.IsNullOrEmpty(choice) && int.TryParse(choice, out int pick) && pick >= 1 && pick <= Math.Min(results.Length, 5))
            idx = pick - 1;

        string chosenName = results[idx].GetProperty("name").GetString()!;
        int chosenId = results[idx].GetProperty("appid").GetInt32();
        Console.WriteLine($"[INFO] Selected: {chosenName} → AppID {chosenId}");
        return chosenId.ToString();
    }
    catch { }

    return null;
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
static extern uint WaitForMultipleObjects(
    uint nCount,
    IntPtr[] lpHandles,
    [MarshalAs(UnmanagedType.Bool)] bool fWaitAll,
    uint dwMilliseconds);


// ═══════════════════════════════════════════════════════════════
// Server models (MUST be last — CS8803)
// ═══════════════════════════════════════════════════════════════

record RegisterResponse(string status, string room_id, string? virtual_ip, int player_count);

record PollResponse(string status, int player_count, List<PlayerInfo>? players);

record PlayerInfo(string player_id, string ip, int udp_port, string virtual_ip);
