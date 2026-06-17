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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

// ═══════════════════════════════════════════════════════════════
// Wintun Virtual LAN Emulator — Step 5: Goldberg Auto-Patcher
// .NET 8 | x64 only | Requires Administrator
// ═══════════════════════════════════════════════════════════════

const string SignalServerUrl  = "http://127.0.0.1:8000";
const string AdapterName      = "LanEmulatorTun";
const string AdapterMask      = "255.255.255.0";
const int    PrefixLength     = 24;
const int    UdpPort          = 51820;

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

Console.WriteLine("=== Wintun LAN Emulator — Step 6 (Dual Mode) ===");
Console.WriteLine($"    Adapter : {AdapterName}");
Console.WriteLine($"    IP      : assigned by server");
Console.WriteLine($"    UDP     : 0.0.0.0:{UdpPort}");
Console.WriteLine($"    Server  : {SignalServerUrl}");
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

// ── 5. Register with signaling server ─────────────────────────
using var http = new HttpClient { BaseAddress = new Uri(SignalServerUrl) };
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
catch (Exception ex)
{
    Console.Error.WriteLine($"[FATAL] Registration failed: {ex.Message}");
    return 7;
}

string myVirtualIP = reg?.virtual_ip ?? "10.13.37.1";
Console.WriteLine($"[OK]   Assigned virtual IP: {myVirtualIP}");

// ── 6. Poll until peer joins ──────────────────────────────────
string peerIP = "127.0.0.1";
int peerPort = UdpPort;
string peerVirtualIP = "10.13.37.2";

Console.WriteLine();
Console.Write("[*]   Waiting for peer");
bool peerFound = false;

while (!peerFound)
{
    try
    {
        var poll = await http.GetFromJsonAsync<PollResponse>($"/poll?room_id={Uri.EscapeDataString(roomId)}");

        if (poll is { status: "ready", players: not null })
        {
            var peer = poll.players.Find(p =>
                !string.Equals(p.player_id, Environment.MachineName, StringComparison.OrdinalIgnoreCase));

            if (peer != null)
            {
                peerIP = peer.ip;
                peerPort = peer.udp_port;
                peerVirtualIP = peer.virtual_ip;
                peerFound = true;
                Console.WriteLine();
                Console.WriteLine($"[OK]   Peer found: {peer.player_id}");
                Console.WriteLine($"       Public IP : {peerIP}:{peerPort}");
                Console.WriteLine($"       VPN IP    : {peerVirtualIP}");
            }
        }
    }
    catch (Exception ex)
    {
        Console.Write($"!({ex.Message.GetHashCode():X4})");
    }

    if (!peerFound)
    {
        Console.Write(".");
        await Task.Delay(2000, CancellationToken.None);
    }
}

// ── 7. Goldberg Auto-Patcher (Steam mode only) ────────────────
if (mode == 1 && gameDir != null)
{
    Console.WriteLine();
    Console.WriteLine("─── Goldberg Auto-Patcher ────────────────────────────");

    string goldbergSrc = Path.Combine(AppContext.BaseDirectory, "goldberg", "steam_api64.dll");
    string targetDll = Path.Combine(gameDir, "steam_api64.dll");
    string settingsDst = Path.Combine(gameDir, "steam_settings");

    // 7a. Auto-detect Steam AppID
    string? appId = AutoDetectAppId(gameDir, gameExePath!);
    if (string.IsNullOrEmpty(appId) || appId == "0")
    {
        // Try Steam API lookup by game folder name
        string gameName = Path.GetFileName(gameDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        appId = await SteamSearchAppId(http, gameName);
    }

    if (!string.IsNullOrEmpty(appId) && appId != "0")
    {
        Console.WriteLine($"[OK]   Detected Steam AppID: {appId}");
    }
    else
    {
        appId = "0";
        Console.WriteLine("[WARN] Could not auto-detect Steam AppID.");
        Console.WriteLine($"       Edit {settingsDst}\\steam_appid.txt after first run.");
    }

    // 7b. Backup original DLL
    string backupPath = targetDll + ".bak";
    if (File.Exists(targetDll) && !File.Exists(backupPath))
    {
        File.Move(targetDll, backupPath);
        Console.WriteLine($"[OK]   Backed up original DLL → steam_api64.dll.bak");
    }
    else if (File.Exists(backupPath))
    {
        Console.WriteLine("[INFO] Backup already exists, replacing target…");
    }

    // 7c. Copy goldberg DLL
    if (File.Exists(goldbergSrc))
    {
        File.Copy(goldbergSrc, targetDll, overwrite: true);
        Console.WriteLine("[OK]   Goldberg steam_api64.dll deployed");
    }
    else
    {
        Console.Error.WriteLine($"[WARN] Goldberg DLL not found at: {goldbergSrc}");
        Console.Error.WriteLine("       Place steam_api64.dll in ./goldberg/ next to wintun-poc.exe");
    }

    // 7d. Write INI config
    string iniPath = Path.Combine(gameDir, "GoldbergSteamEmu.ini");
    string iniContent = $"[Networking]\nip={peerVirtualIP}\n";
    File.WriteAllText(iniPath, iniContent);
    Console.WriteLine($"[OK]   Goldberg INI written → peer IP: {peerVirtualIP}");

    // 7e. Copy steam_settings + write appid
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
    {
        Directory.CreateDirectory(settingsDst);
    }

    // Write detected AppID
    File.WriteAllText(Path.Combine(settingsDst, "steam_appid.txt"), appId);
    Console.WriteLine($"[OK]   steam_appid.txt → {appId}");
    Console.WriteLine("──────────────────────────────────────────────────────");
}

// ── 8. UDP Hole Punching ──────────────────────────────────────
Console.WriteLine();
Console.Write("[*]   Hole punching");
using var holePunchSock = new UdpClient();
holePunchSock.Client.SendBufferSize = 64 * 1024;

var peerEndPoint = new IPEndPoint(IPAddress.Parse(peerIP), peerPort);
byte[] holePunchData = new byte[] { 0x00 };
int punchCount = 0;

for (int i = 0; i < 10; i++)
{
    try
    {
        holePunchSock.Send(holePunchData, holePunchData.Length, peerEndPoint);
        punchCount++;
        Console.Write(".");
    }
    catch (Exception ex)
    {
        Console.Write($"!({ex.Message.GetHashCode():X4})");
    }
    await Task.Delay(500, CancellationToken.None);
}
Console.WriteLine($" {punchCount}/10 packets sent");

// ── 9. Driver version ─────────────────────────────────────────
uint ver = WintunGetRunningDriverVersion();
Console.WriteLine($"[OK]   Wintun driver v{ver >> 16}.{ver & 0xFFFF}");

// ── 10. Clean stale adapter ───────────────────────────────────
IntPtr existing = WintunOpenAdapter(AdapterName);
if (existing != IntPtr.Zero)
{
    Console.WriteLine("[INFO] Stale adapter found, closing…");
    WintunCloseAdapter(existing);
    Thread.Sleep(500);
}

// ── 11. Create virtual adapter ────────────────────────────────
IntPtr adapter = WintunCreateAdapter(AdapterName, "LanEmulator", IntPtr.Zero);
if (adapter == IntPtr.Zero)
{
    int err = Marshal.GetLastWin32Error();
    Console.Error.WriteLine($"[FATAL] WintunCreateAdapter failed. Win32 error {err} (0x{err:X8}): {Win32Msg(err)}");
    return 3;
}
Console.WriteLine($"[OK]   Adapter '{AdapterName}' created.");

// ── 12. Get LUID → interface alias ────────────────────────────
if (!WintunGetAdapterLUID(adapter, out ulong luid))
{
    Console.Error.WriteLine($"[FATAL] WintunGetAdapterLUID failed ({Marshal.GetLastWin32Error()})");
    WintunCloseAdapter(adapter);
    return 4;
}
Console.WriteLine($"[OK]   LUID: 0x{luid:X16}");

string ifAlias = GetInterfaceAlias(luid);
Console.WriteLine($"[OK]   Interface alias: \"{ifAlias}\"");

// ── 13. Assign IP & bring up ──────────────────────────────────
RunNetsh($"interface ip set address name=\"{ifAlias}\" source=static addr={myVirtualIP} mask={AdapterMask}");
RunNetsh($"interface set interface name=\"{ifAlias}\" admin=enabled");
Console.WriteLine($"[OK]   IP {myVirtualIP}/{PrefixLength} assigned, interface UP");

// ── 14. Add route for peer ────────────────────────────────────
RunRoute($"add {peerVirtualIP} mask 255.255.255.255 {myVirtualIP} metric 1");
Console.WriteLine($"[OK]   Route added: {peerVirtualIP}/32 → via {myVirtualIP}");

// ── 15. Start Wintun session (Ring Buffer) ────────────────────
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

// ── 16. Launch UDP listener + packet pump ─────────────────────
using var cts = new CancellationTokenSource();
using var udp = new UdpClient(UdpPort);
udp.Client.ReceiveBufferSize = 4 * 1024 * 1024;
udp.Client.SendBufferSize    = 4 * 1024 * 1024;
Console.WriteLine($"[OK]   UDP listening on 0.0.0.0:{UdpPort}");

var pumpNetToTun = new Thread(() => PumpNetworkToTun(udp, session, cts.Token))
{
    Name = "Net→Tun", IsBackground = true
};
var pumpTunToNet = new Thread(() => PumpTunToNet(session, peerIP, peerPort, cts.Token))
{
    Name = "Tun→Net", IsBackground = true
};

pumpNetToTun.Start();
pumpTunToNet.Start();
Console.WriteLine("[OK]   Packet pump threads running");

// ── 17. Launch the game (Steam mode only) ─────────────────────
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

// ── 18. Wait ──────────────────────────────────────────────────
var shutdownEvent = new ManualResetEventSlim(false);

Console.WriteLine();
Console.WriteLine("╔═══════════════════════════════════════════════════════╗");
if (mode == 1)
    Console.WriteLine("║  VPN + Goldberg ACTIVE — Game should be running.     ║");
else
    Console.WriteLine("║  Virtual LAN ACTIVE — Launch your game manually.     ║");
Console.WriteLine("║                                                       ║");
Console.WriteLine($"║  My  IP    : {myVirtualIP}/{PrefixLength,-30}║");
Console.WriteLine($"║  Peer IP   : {peerVirtualIP}/32                            ║");
Console.WriteLine($"║  UDP recv  : 0.0.0.0:{UdpPort,-31}║");
Console.WriteLine($"║  UDP send  : {peerIP}:{peerPort,-31}║");
Console.WriteLine($"║  Room      : {roomId,-38}║");
Console.WriteLine($"║  Mode      : {(mode == 1 ? "Steam + Goldberg" : "Pure LAN"),-38}║");
if (gameProcess != null)
    Console.WriteLine($"║  Game PID  : {gameProcess.Id,-38}║");
Console.WriteLine("║                                                       ║");
Console.WriteLine("║  Press ESC to shutdown…                              ║");
Console.WriteLine("╚═══════════════════════════════════════════════════════╝");

// Wait for ESC key
while (Console.ReadKey(true).Key != ConsoleKey.Escape)
{
    // ignore other keys
}
shutdownEvent.Set();

// ── 19. Cleanup ───────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("[*] Shutting down…");

// Kill game process (Steam mode only)
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

cts.Cancel();
pumpNetToTun.Join(3000);
pumpTunToNet.Join(3000);

WintunEndSession(session);
Console.WriteLine("[OK]   Wintun session ended.");

WintunCloseAdapter(adapter);
Console.WriteLine("[OK]   Adapter closed and removed from system.");

udp.Close();
Console.WriteLine("[OK]   UDP socket closed.");

RunRoute($"delete {peerVirtualIP}");
Console.WriteLine("[OK]   Route removed.");

Console.WriteLine("[DONE] Cleanup complete.");
return 0;


// ═══════════════════════════════════════════════════════════════
// Packet pumps
// ═══════════════════════════════════════════════════════════════

static void PumpNetworkToTun(UdpClient udp, IntPtr session, CancellationToken ct)
{
    try
    {
        var peer = new IPEndPoint(IPAddress.Any, 0);
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

static void PumpTunToNet(IntPtr session, string peerIP, int peerPort, CancellationToken ct)
{
    var peer = new IPEndPoint(IPAddress.Parse(peerIP), peerPort);
    using var sock = new UdpClient();
    sock.Client.SendBufferSize = 4 * 1024 * 1024;

    IntPtr readEvent = WintunGetReadWaitEvent(session);
    if (readEvent == IntPtr.Zero)
    {
        Console.Error.WriteLine("[ERR] WintunGetReadWaitEvent returned NULL");
        return;
    }

    var waitHandle = new EventWaitHandle(false, EventResetMode.AutoReset);
    waitHandle.SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(readEvent, ownsHandle: false);

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
                sock.Send(buffer, buffer.Length, peer);
                WintunReleaseReceivePacket(session, packet);
            }

            int signaled = (int)WaitForMultipleObjects(1, new[] { readEvent }, false, 500u);
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

/// <summary>Try to detect Steam AppID from game folder.</summary>
static string? AutoDetectAppId(string gameDir, string gameExePath)
{
    // 1. steam_appid.txt in game dir (from Steam or previous cracks)
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

    // 2. Scan original steam_api64.dll for embedded AppID patterns
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
        catch { /* ignore dll scan errors */ }
    }

    // 3. Check .ini files for SteamAppId or AppId
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
        catch { /* skip unreadable files */ }
    }

    return null;
}

/// <summary>Extract first numeric value from a string.</summary>
static string? ExtractNumber(string text)
{
    var match = System.Text.RegularExpressions.Regex.Match(text, @"\d+");
    return match.Success && match.Value != "0" ? match.Value : null;
}

/// <summary>Scan DLL binary for Steam AppID pattern.</summary>
static string? ScanDllForAppId(byte[] dllBytes)
{
    // Steam AppIDs are 2-7 digit numbers. Look for them near "steam_appid" or
    // in the .rdata section (common for steam_api stubs).
    // Strategy: scan for text strings that are pure numbers in the 100-9999999 range,
    // then verify they appear in context (near "Steam" or "steam_appid" bytes).

    string text = System.Text.Encoding.ASCII.GetString(dllBytes);
    int steamAppIdIdx = text.IndexOf("steam_appid", StringComparison.OrdinalIgnoreCase);
    if (steamAppIdIdx >= 0)
    {
        // Look for numbers around "steam_appid" reference
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

/// <summary>Search Steam for a game's AppID. Shows results if multiple found.</summary>
static async Task<string?> SteamSearchAppId(HttpClient http, string gameName)
{
    try
    {
        string url = $"https://steamcommunity.com/actions/SearchApps/{Uri.EscapeDataString(gameName)}";
        using var steamHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        steamHttp.DefaultRequestHeaders.Add("User-Agent", "LanEmulator/1.0");
        var resp = await steamHttp.GetStringAsync(url);
        using var json = System.Text.Json.JsonDocument.Parse(resp);

        if (json.RootElement.GetArrayLength() == 0)
            return null;

        // If exact match on first result, use it
        var results = json.RootElement.EnumerateArray().ToArray();
        if (results.Length == 0) return null;

        // Look for exact name match first
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

        // Multiple results — let user pick
        if (results.Length == 1)
        {
            string name = results[0].GetProperty("name").GetString()!;
            int appId = results[0].GetProperty("appid").GetInt32();
            Console.WriteLine($"[INFO] Steam: '{name}' → AppID {appId}");
            return appId.ToString();
        }

        // 2+ results: show list
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
    catch
    {
        // Steam API unavailable — fall through to manual entry
    }

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
// Server models
// ═══════════════════════════════════════════════════════════════

record RegisterResponse(string status, string room_id, string? virtual_ip, int player_count);

record PollResponse(string status, int player_count, List<PlayerInfo>? players);

record PlayerInfo(string player_id, string ip, int udp_port, string virtual_ip);
