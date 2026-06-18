namespace LanEmulator.Core.Services;

using LanEmulator.Core.Interfaces;
using LanEmulator.Core;

/// <summary>
/// Wintun VPN controller -- handles adapter lifecycle, UDP transport,
/// hole punching, packet pumps, and keepalive.
/// </summary>
public class VpnController : IVpnController
{
    private UdpClient? _udp;
    private IntPtr _adapter, _session;
    private CancellationTokenSource? _cts;
    private Thread? _pumpNetToTun, _pumpTunToNet;
    private Task? _keepaliveTask;

    public bool IsRunning { get; private set; }

    public void Start(
        string adapterName,
        string myVirtualIp,
        string adapterMask,
        int prefixLength,
        int udpPort,
        IPeerRegistry peerRegistry,
        Pumps.SignalingHandler? onSignaling = null)
    {
        // UDP socket
        _udp = new UdpClient(udpPort);
        try
        {
        _udp.Client.ReceiveBufferSize = 4 * 1024 * 1024;
        _udp.Client.SendBufferSize = 4 * 1024 * 1024;

        // Hole punching
        byte[] holePunchData = new byte[] { 0x00 };
        var snapshot = peerRegistry.Peers;
        foreach (var p in snapshot)
        {
            try
            {
                var ep = new IPEndPoint(IPAddress.Parse(p.ip), p.udp_port);
                for (int i = 0; i < 10; i++)
                    _udp.Send(holePunchData, holePunchData.Length, ep);
            }
            catch { /* best-effort */ }
            Thread.Sleep(100);
        }

        // Wintun adapter
        IntPtr existing = WintunInterop.WintunOpenAdapter(adapterName);
        if (existing != IntPtr.Zero)
        { WintunInterop.WintunCloseAdapter(existing); Thread.Sleep(500); }

        _adapter = WintunInterop.WintunCreateAdapter(adapterName, "LanEmulator", IntPtr.Zero);
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
        Helpers.RunNetsh(string.Concat("interface ip set address name=\"", ifAlias, "\" source=static addr=", myVirtualIp, " mask=", adapterMask));
        Helpers.RunNetsh(string.Concat("interface set interface name=\"", ifAlias, "\" admin=enabled"));

        // Routes
        var ipToPeer = peerRegistry.GetIpToPeerSnapshot();
        foreach (var p in snapshot)
            Helpers.RunRoute($"add {p.virtual_ip} mask 255.255.255.255 {myVirtualIp} metric 1");

        // Session + pumps
        const uint RingCapacity = 0x400000;
        _session = WintunInterop.WintunStartSession(_adapter, RingCapacity);
        if (_session == IntPtr.Zero) throw new Exception("StartSession failed");

        _cts = new CancellationTokenSource();

        _pumpNetToTun = new Thread(() => Pumps.PumpNetworkToTun(_udp, _session, _cts.Token, onSignaling))
            { Name = "Net->Tun", IsBackground = true };
        _pumpTunToNet = new Thread(() => Pumps.PumpTunToNet(_udp, _session, ipToPeer, new object(), _cts.Token)) { Name = "Tun->Net", IsBackground = true };
        _pumpNetToTun.Start(); _pumpTunToNet.Start();

        IsRunning = true;
        }
        catch
        {
            _udp?.Dispose();
            _udp = null;
            throw;
        }
    }

    /// <summary>Send a signaling packet through the UDP socket (for hole punch / join requests).</summary>
    public void SendSignaling(byte[] data, IPEndPoint destination)
    {
        try { _udp?.Send(data, data.Length, destination); }
        catch { /* best-effort */ }
    }

    public async Task StopAsync(string adapterName)
    {
        IsRunning = false;
        _cts?.Cancel();

        _pumpNetToTun?.Join(3000);
        _pumpTunToNet?.Join(3000);

        if (_session != IntPtr.Zero) { WintunInterop.WintunEndSession(_session); _session = IntPtr.Zero; }
        if (_adapter != IntPtr.Zero) { WintunInterop.WintunCloseAdapter(_adapter); _adapter = IntPtr.Zero; }

        _udp?.Dispose();
        _keepaliveTask = null;
    }
}
