using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

// ═══════════════════════════════════════════════════════════════
// Wintun Virtual LAN Emulator — Step 2: Ring Buffer + UDP
// .NET 8 | x64 only | Requires Administrator
// ═══════════════════════════════════════════════════════════════

const string AdapterName  = "LanEmulatorTun";
const string AdapterIP    = "10.13.37.1";
const string AdapterMask  = "255.255.255.0";
const int    PrefixLength = 24;
const int    UdpPort      = 51820;
// Hardcoded peer for local loopback test (127.0.0.1:51821)
const string PeerIP       = "127.0.0.1";
const int    PeerPort     = 51821;

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

Console.WriteLine("=== Wintun LAN Emulator — Step 2 (Ring Buffer + UDP) ===");
Console.WriteLine($"    Adapter : {AdapterName}");
Console.WriteLine($"    IP      : {AdapterIP}/{PrefixLength}");
Console.WriteLine($"    UDP     : 0.0.0.0:{UdpPort} -> {PeerIP}:{PeerPort}");
Console.WriteLine();

// ── 3. Driver version ─────────────────────────────────────────
uint ver = WintunGetRunningDriverVersion();
Console.WriteLine($"[OK]   Wintun driver v{ver >> 16}.{ver & 0xFFFF}");

// ── 4. Clean stale adapter ────────────────────────────────────
IntPtr existing = WintunOpenAdapter(AdapterName);
if (existing != IntPtr.Zero)
{
    Console.WriteLine("[INFO] Stale adapter found, closing (auto-removes)…");
    WintunCloseAdapter(existing);
    Thread.Sleep(500);
}

// ── 5. Create virtual adapter ─────────────────────────────────
IntPtr adapter = WintunCreateAdapter(AdapterName, "LanEmulator", IntPtr.Zero);
if (adapter == IntPtr.Zero)
{
    int err = Marshal.GetLastWin32Error();
    Console.Error.WriteLine($"[FATAL] WintunCreateAdapter failed. Win32 error {err} (0x{err:X8}): {Win32Msg(err)}");
    return 3;
}
Console.WriteLine($"[OK]   Adapter '{AdapterName}' created.");

// ── 6. Get LUID → interface alias ─────────────────────────────
if (!WintunGetAdapterLUID(adapter, out ulong luid))
{
    Console.Error.WriteLine($"[FATAL] WintunGetAdapterLUID failed ({Marshal.GetLastWin32Error()})");
    WintunCloseAdapter(adapter);
    return 4;
}
Console.WriteLine($"[OK]   LUID: 0x{luid:X16}");

string ifAlias = GetInterfaceAlias(luid);
Console.WriteLine($"[OK]   Interface alias: \"{ifAlias}\"");

// ── 7. Assign IP & bring up ───────────────────────────────────
RunNetsh($"interface ip set address name=\"{ifAlias}\" source=static addr={AdapterIP} mask={AdapterMask}");
RunNetsh($"interface set interface name=\"{ifAlias}\" admin=enabled");
Console.WriteLine($"[OK]   IP {AdapterIP}/{PrefixLength} assigned, interface UP");

// ── 8. Start Wintun session (Ring Buffer) ────────────────────
const uint RingCapacity = 0x400000; // 4 MiB
IntPtr session = WintunStartSession(adapter, RingCapacity);
if (session == IntPtr.Zero)
{
    int err = Marshal.GetLastWin32Error();
    Console.Error.WriteLine($"[FATAL] WintunStartSession failed. Win32 error {err} (0x{err:X8}): {Win32Msg(err)}");
    WintunCloseAdapter(adapter);
    return 5;
}
Console.WriteLine($"[OK]   Wintun session started (ring: {RingCapacity / 1024 / 1024} MiB)");

// ── 9. Launch UDP listener + packet pump ──────────────────────
using var cts = new CancellationTokenSource();
using var udp = new UdpClient(UdpPort);
udp.Client.ReceiveBufferSize = 4 * 1024 * 1024;
udp.Client.SendBufferSize    = 4 * 1024 * 1024;
Console.WriteLine($"[OK]   UDP listening on 0.0.0.0:{UdpPort}");

// Thread A: UDP → Wintun (packets from network into virtual adapter)
var pumpNetToTun = new Thread(() => PumpNetworkToTun(udp, session, cts.Token))
{
    Name = "Net→Tun", IsBackground = true
};

// Thread B: Wintun → UDP (packets from virtual adapter out to network)
var pumpTunToNet = new Thread(() => PumpTunToNet(session, PeerIP, PeerPort, cts.Token))
{
    Name = "Tun→Net", IsBackground = true
};

pumpNetToTun.Start();
pumpTunToNet.Start();
Console.WriteLine("[OK]   Packet pump threads running");

// ── 10. Wait ──────────────────────────────────────────────────
var shutdownEvent = new ManualResetEventSlim(false);
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // Don't kill process immediately
    shutdownEvent.Set();
};

Console.WriteLine();
Console.WriteLine("╔═══════════════════════════════════════════════════════╗");
Console.WriteLine("║  Adapter is LIVE — Ring Buffer + UDP ACTIVE.         ║");
Console.WriteLine("║                                                       ║");
Console.WriteLine($"║  Tun IP    : {AdapterIP}/{PrefixLength,-30}║");
Console.WriteLine($"║  UDP recv  : 0.0.0.0:{UdpPort,-31}║");
Console.WriteLine($"║  UDP send  : {PeerIP}:{PeerPort,-31}║");
Console.WriteLine("║                                                       ║");
Console.WriteLine("║  Test from a SECOND terminal:                         ║");
Console.WriteLine($"║    ping {AdapterIP}                                   ║");
Console.WriteLine("║    python test-peer.py                                ║");
Console.WriteLine("║                                                       ║");
Console.WriteLine("║  Press Ctrl+C to shutdown…                           ║");
Console.WriteLine("╚═══════════════════════════════════════════════════════╝");

// Wait until Ctrl+C is pressed
shutdownEvent.Wait();

// ── 11. Cleanup ───────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("[*] Shutting down…");
cts.Cancel();

// Join threads with timeout
pumpNetToTun.Join(3000);
pumpTunToNet.Join(3000);

// End session
WintunEndSession(session);
Console.WriteLine("[OK]   Wintun session ended.");

// Close adapter (also removes it from system — per Wintun docs)
WintunCloseAdapter(adapter);
Console.WriteLine("[OK]   Adapter closed and removed from system.");

udp.Close();
Console.WriteLine("[DONE] Cleanup complete.");
return 0;


// ═══════════════════════════════════════════════════════════════
// Packet pump logic
// ═══════════════════════════════════════════════════════════════

/// <summary>Reads UDP datagrams from the network and injects them into Wintun adapter.</summary>
static void PumpNetworkToTun(UdpClient udp, IntPtr session, CancellationToken ct)
{
    try
    {
        var peer = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            // Blocking receive with cancellation support
            var task = udp.ReceiveAsync(ct);
            task.AsTask().Wait(ct);
            var result = task.Result;
            byte[] packet = result.Buffer;

            if (packet.Length == 0) continue;

            // Allocate send buffer in Wintun ring
            IntPtr sendBuf = WintunAllocateSendPacket(session, (uint)packet.Length);
            if (sendBuf == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 111 /*ERROR_BUFFER_OVERFLOW*/)
                    continue; // Ring full, drop packet
                if (err == 38 /*ERROR_HANDLE_EOF*/)
                    break; // Adapter closing
                Console.Error.WriteLine($"   [WARN] AllocateSendPacket failed: {err}");
                continue;
            }

            // Copy UDP payload into Wintun buffer
            Marshal.Copy(packet, 0, sendBuf, packet.Length);

            // Send into the virtual adapter (injects into Windows network stack)
            WintunSendPacket(session, sendBuf);
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[ERR] Net→Tun pump: {ex.Message}");
    }
}

/// <summary>Reads packets from Wintun adapter and sends them via UDP to the peer.</summary>
static void PumpTunToNet(IntPtr session, string peerIP, int peerPort, CancellationToken ct)
{
    var peer = new IPEndPoint(IPAddress.Parse(peerIP), peerPort);
    using var sock = new UdpClient();
    sock.Client.SendBufferSize = 4 * 1024 * 1024;

    // Get the read-wait event for efficient polling
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
            // Try to receive immediately
            while (true)
            {
                IntPtr packet = WintunReceivePacket(session, out uint packetSize);
                if (packet == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == 259 /*ERROR_NO_MORE_ITEMS*/)
                        break; // No more packets, wait
                    if (err == 38 /*ERROR_HANDLE_EOF*/)
                        return; // Adapter closing
                    break;
                }

                // Copy packet data
                byte[] buffer = new byte[packetSize];
                Marshal.Copy(packet, buffer, 0, (int)packetSize);

                // Send to peer via UDP
                sock.Send(buffer, buffer.Length, peer);

                // Release the packet back to Wintun
                WintunReleaseReceivePacket(session, packet);
            }

            // Wait for more data or cancellation
            int signaled = (int)WaitForMultipleObjects(
                1, new[] { readEvent }, false, 500u);
            if (signaled == unchecked((int)0xFFFFFFFF)) // WAIT_FAILED
                break;
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
// P/Invoke — Wintun (wintun.dll)
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

// ── Session API (Ring Buffer) ─────────────────────────────────

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

// ── iphlpapi ──────────────────────────────────────────────────

[DllImport("iphlpapi.dll", CharSet = CharSet.Unicode)]
static extern uint ConvertInterfaceLuidToAlias(ref ulong InterfaceLuid, [Out] char[] InterfaceAlias, nuint Length);

// ── kernel32 (for event wait) ─────────────────────────────────

[DllImport("kernel32.dll", SetLastError = true)]
static extern uint WaitForMultipleObjects(
    uint nCount,
    IntPtr[] lpHandles,
    [MarshalAs(UnmanagedType.Bool)] bool fWaitAll,
    uint dwMilliseconds);
