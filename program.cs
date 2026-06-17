using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
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

Console.WriteLine("=== Wintun LAN Emulator — Step 5 (Goldberg Auto-Patcher) ===");
Console.WriteLine($"    Adapter : {AdapterName}");
Console.WriteLine($"    IP      : assigned by server");
Console.WriteLine($"    UDP     : 0.0.0.0:{UdpPort}");
Console.WriteLine($"    Server  : {SignalServerUrl}");
Console.WriteLine();

// ── 3. Prompt for Room ID ─────────────────────────────────────
Console.Write("Room ID: ");
string? roomId = Console.ReadLine()?.Trim();
if (string.IsNullOrWhiteSpace(roomId))
{
    Console.Error.WriteLine("[FATAL] Room ID is required.");
    return 6;
}
Console.WriteLine($"[OK]   Room: '{roomId}'");

// ── 4. Prompt for Game Executable ─────────────────────────────
Console.Write("Path to game .exe: ");
string? gameExePath = Console.ReadLine()?.Trim();

string? gameDir = null;
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

// ── 7. Goldberg Auto-Patcher ──────────────────────────────────
if (gameDir != null)
{
    Console.WriteLine();
    Console.WriteLine("─── Goldberg Auto-Patcher ────────────────────────────");

    string goldbergSrc = Path.Combine(AppContext.BaseDirectory, "goldberg", "steam_api64.dll");
    string targetDll = Path.Combine(gameDir, "steam_api64.dll");

    // 7a. Backup original DLL
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

    // 7b. Copy goldberg DLL
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

    // 7c. Write INI config
    string iniPath = Path.Combine(gameDir, "GoldbergSteamEmu.ini");
    string iniContent = $"[Networking]\nip={peerVirtualIP}\n";
    File.WriteAllText(iniPath, iniContent);
    Console.WriteLine($"[OK]   Goldberg INI written → peer IP: {peerVirtualIP}");

    // 7d. Copy steam_settings folder (appid, etc.)
    string settingsSrc = Path.Combine(AppContext.BaseDirectory, "goldberg", "steam_settings");
    string settingsDst = Path.Combine(gameDir, "steam_settings");

    if (Directory.Exists(settingsSrc))
    {
        // Copy entire steam_settings folder to game directory
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
        // Ensure at least steam_appid.txt exists (template)
        string appIdPath = Path.Combine(settingsDst, "steam_appid.txt");
        if (!File.Exists(appIdPath))
        {
            Directory.CreateDirectory(settingsDst);
            File.WriteAllText(appIdPath, "0 # <-- Replace with game's Steam AppID");
        }
        Console.WriteLine("[INFO] Created steam_settings/steam_appid.txt (edit AppID!)");
    }
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

// ── 17. Launch the game ───────────────────────────────────────
Process? gameProcess = null;
if (gameExePath != null)
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
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdownEvent.Set();
};

Console.WriteLine();
Console.WriteLine("╔═══════════════════════════════════════════════════════╗");
Console.WriteLine("║  VPN + Goldberg ACTIVE — Game should be running.     ║");
Console.WriteLine("║                                                       ║");
Console.WriteLine($"║  My  IP    : {myVirtualIP}/{PrefixLength,-30}║");
Console.WriteLine($"║  Peer IP   : {peerVirtualIP}/32                            ║");
Console.WriteLine($"║  UDP recv  : 0.0.0.0:{UdpPort,-31}║");
Console.WriteLine($"║  UDP send  : {peerIP}:{peerPort,-31}║");
Console.WriteLine($"║  Room      : {roomId,-38}║");
if (gameProcess != null)
    Console.WriteLine($"║  Game PID  : {gameProcess.Id,-38}║");
Console.WriteLine("║                                                       ║");
Console.WriteLine("║  Press Ctrl+C to shutdown…                           ║");
Console.WriteLine("╚═══════════════════════════════════════════════════════╝");

shutdownEvent.Wait();

// ── 19. Cleanup ───────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("[*] Shutting down…");

// Kill game process first
if (gameProcess is { HasExited: false })
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
