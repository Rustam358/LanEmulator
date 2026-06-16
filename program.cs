using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

// ═══════════════════════════════════════════════════════════════
// Wintun Virtual LAN Adapter — Proof of Concept
// .NET 8 | x64 only | Requires Administrator
//
// DLL source: Cloudflare WARP wintun.dll (exports verified)
// Available: Create/Open/Close/GetLUID/GetVersion
// Unavailable: DeleteAdapter → cleanup via netsh + device removal
// ═══════════════════════════════════════════════════════════════

const string AdapterName  = "LanEmulatorTun";
const string AdapterIP    = "10.13.37.1";
const string AdapterMask  = "255.255.255.0";
const int    PrefixLength = 24;

// ── 1. Admin check ────────────────────────────────────────────
if (!IsAdministrator())
{
    Console.Error.WriteLine("[FATAL] Administrator privileges required.");
    Console.Error.WriteLine("        Right-click → Run as Administrator, or run from elevated terminal.");
    return 1;
}

// ── 2. Verify wintun.dll ──────────────────────────────────────
string wintunPath = Path.Combine(AppContext.BaseDirectory, "wintun.dll");
if (!File.Exists(wintunPath))
{
    Console.Error.WriteLine($"[FATAL] wintun.dll not found at: {wintunPath}");
    Console.Error.WriteLine("        Download x64 from: https://www.wintun.net/");
    return 2;
}

Console.WriteLine("=== Wintun Virtual LAN PoC ===");
Console.WriteLine($"    Adapter : {AdapterName}");
Console.WriteLine($"    Network : {AdapterIP}/{PrefixLength}");
Console.WriteLine();

// ── 3. Verify driver is loaded ────────────────────────────────
uint ver = WintunGetRunningDriverVersion();
Console.WriteLine($"[OK]   Wintun driver v{ver >> 16}.{ver & 0xFFFF}");

// ── 4. Check for stale adapter ────────────────────────────────
IntPtr existing = WintunOpenAdapter(AdapterName);
if (existing != IntPtr.Zero)
{
    Console.WriteLine("[INFO] Stale adapter found (from previous run).");
    Console.WriteLine("[*]    Closing stale handle…");

    // Get LUID before closing, so we can disable via netsh
    bool hasLuid = WintunGetAdapterLUID(existing, out ulong staleLuid);
    WintunCloseAdapter(existing);

    if (hasLuid)
    {
        string? staleAlias = GetInterfaceAlias(staleLuid);
        if (staleAlias != null)
        {
            Console.WriteLine($"[*]    Disabling stale interface '{staleAlias}'…");
            RunNetsh($"interface set interface name=\"{staleAlias}\" admin=disabled");
            // Optionally uninstall the device
            RemoveDeviceByAlias(staleAlias);
        }
    }
    Thread.Sleep(500);
}

// ── 5. Create virtual adapter ─────────────────────────────────
IntPtr adapter = WintunCreateAdapter(AdapterName, "LanEmulator", IntPtr.Zero);
if (adapter == IntPtr.Zero)
{
    int err = Marshal.GetLastWin32Error();
    Console.Error.WriteLine($"[FATAL] WintunCreateAdapter failed.");
    Console.Error.WriteLine($"        Win32 error {err} (0x{err:X8}): {Win32Message(err)}");
    return 3;
}
Console.WriteLine($"[OK]   Adapter '{AdapterName}' created.");

// ── 6. Get adapter LUID → interface alias ─────────────────────
if (!WintunGetAdapterLUID(adapter, out ulong luid))
{
    int err = Marshal.GetLastWin32Error();
    Console.Error.WriteLine($"[FATAL] WintunGetAdapterLUID failed ({err})");
    WintunCloseAdapter(adapter);
    return 4;
}
Console.WriteLine($"[OK]   LUID: 0x{luid:X16}");

string ifAlias = GetInterfaceAlias(luid);
Console.WriteLine($"[OK]   Interface alias: \"{ifAlias}\"");

// ── 7. Assign static IP ───────────────────────────────────────
Console.WriteLine($"[*]    Setting IP {AdapterIP} / mask {AdapterMask}…");
RunNetsh($"interface ip set address name=\"{ifAlias}\" source=static " +
         $"addr={AdapterIP} mask={AdapterMask}");

// ── 8. Bring adapter up ───────────────────────────────────────
Console.WriteLine("[*]    Enabling interface…");
RunNetsh($"interface set interface name=\"{ifAlias}\" admin=enabled");

// ── 9. Verify ─────────────────────────────────────────────────
Console.WriteLine("[*]    Current configuration:");
RunNetsh($"interface ip show config name=\"{ifAlias}\"");

// ── 10. Wait ──────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("╔══════════════════════════════════════════════╗");
Console.WriteLine("║  Adapter is ACTIVE.                          ║");
Console.WriteLine($"║  Name : {AdapterName,-35}║");
Console.WriteLine($"║  IP   : {AdapterIP}/{PrefixLength,-34}║");
Console.WriteLine("║                                              ║");
Console.WriteLine("║  Press ENTER to shutdown & clean up…        ║");
Console.WriteLine("╚══════════════════════════════════════════════╝");
Console.ReadLine();

// ── 11. Cleanup ───────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("[*] Shutting down…");

// Close the Wintun handle
WintunCloseAdapter(adapter);
Console.WriteLine("[OK]   Wintun handle released.");

// Disable the interface
Console.WriteLine($"[*]    Disabling '{ifAlias}'…");
RunNetsh($"interface set interface name=\"{ifAlias}\" admin=disabled");

// Remove the device from system
RemoveDeviceByAlias(ifAlias);

Console.WriteLine("[DONE] Cleanup complete.");
return 0;


// ═══════════════════════════════════════════════════════════════
// Helpers
// ═══════════════════════════════════════════════════════════════

static bool IsAdministrator()
{
    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
}

static string Win32Message(int code) => new Win32Exception(code).Message;

static void RunNetsh(string arguments)
{
    using var proc = new Process
    {
        StartInfo = new ProcessStartInfo("netsh", arguments)
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

/// <summary>
/// Removes a network device by its interface alias using PnPUtil.
/// Falls back gracefully if removal is not possible (e.g. driver in use).
/// </summary>
static void RemoveDeviceByAlias(string alias)
{
    try
    {
        // Get PnP instance ID from netsh
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo("pnputil", $"/enum-devices /connected /class Net")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            }
        };

        // Simpler approach: use PowerShell to find and remove the device
        var psi = new ProcessStartInfo("powershell", 
            $"-NoProfile -Command \"$a=Get-NetAdapter -Name '{alias}' -ErrorAction Stop; " +
            $"pnputil /remove-device $a.PnPDeviceId\"")
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };

        using var p = Process.Start(psi)!;
        p.WaitForExit(15_000);

        if (p.ExitCode == 0)
            Console.WriteLine("[OK]   Device removed from system.");
        else
        {
            string err = p.StandardError.ReadToEnd().Trim();
            Console.WriteLine($"[WARN] Device removal skipped " +
                              $"(may require reboot or manual removal).");
            if (err.Length > 0)
                Console.WriteLine($"       {err}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[WARN] Device removal error: {ex.Message}");
    }
}


// ═══════════════════════════════════════════════════════════════
// P/Invoke — Wintun (wintun.dll)
// ═══════════════════════════════════════════════════════════════

[DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall,
           CharSet = CharSet.Unicode)]
static extern IntPtr WintunCreateAdapter(
    string Name,
    string TunnelType,
    IntPtr RequestedGUID);

[DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall,
           CharSet = CharSet.Unicode)]
static extern IntPtr WintunOpenAdapter(string Name);

[DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall)]
static extern void WintunCloseAdapter(IntPtr Adapter);

[DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool WintunGetAdapterLUID(IntPtr Adapter, out ulong Luid);

[DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall)]
static extern uint WintunGetRunningDriverVersion();

// ── iphlpapi — LUID → alias ───────────────────────────────────

[DllImport("iphlpapi.dll", CharSet = CharSet.Unicode, SetLastError = false)]
static extern uint ConvertInterfaceLuidToAlias(
    ref ulong InterfaceLuid,
    [Out] char[] InterfaceAlias,
    nuint Length);
