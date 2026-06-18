namespace LanEmulator.Core.Interfaces;

/// <summary>
/// Wintun virtual adapter + UDP transport + packet pumps + keepalive.
/// The "network layer" of the emulator.
/// </summary>
public interface IVpnController
{
    bool IsRunning { get; }

    /// <summary>Create adapter, start pumps, begin keepalive. Returns assigned virtual IP.</summary>
    void Start(
        string adapterName,
        string myVirtualIp,
        string adapterMask,
        int prefixLength,
        int udpPort,
        IPeerRegistry peerRegistry);

    /// <summary>Gracefully stop pumps, close adapter, clean up routes and firewall.</summary>
    Task StopAsync(string adapterName);
}
