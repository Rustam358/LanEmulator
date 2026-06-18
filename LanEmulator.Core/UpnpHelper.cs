namespace LanEmulator.Core;

/// <summary>
/// UPnP port mapping via Windows NATUPNPLib COM interface.
/// Used to auto-open ports on the router when hosting.
/// </summary>
public static class UpnpHelper
{
    private record PortMap(int Port, string Protocol);
    private static readonly List<PortMap> _opened = new();

    /// <summary>
    /// Open all required ports on the router via UPnP.
    /// Returns the number of ports successfully opened.
    /// </summary>
    public static int OpenPorts(string localIP, int tcpPort, int udpPort)
    {
        _opened.Clear();
        int ok = 0;

        try
        {
            var natType = Type.GetTypeFromProgID("HNetCfg.NATUPnP");
            if (natType == null) return 0;

            dynamic upnpnat = Activator.CreateInstance(natType)!;
            dynamic mappings = upnpnat.StaticPortMappingCollection;
            if (mappings == null) return 0;

            try
            {
                mappings.Add(tcpPort, "TCP", tcpPort, localIP, true, "LanEmulator");
                _opened.Add(new PortMap(tcpPort, "TCP"));
                ok++;
            }
            catch { }

            try
            {
                mappings.Add(udpPort, "UDP", udpPort, localIP, true, "LanEmulator");
                _opened.Add(new PortMap(udpPort, "UDP"));
                ok++;
            }
            catch { }
        }
        catch { }

        return ok;
    }

    /// <summary>
    /// Close all ports opened by OpenPorts().
    /// Safe to call even if no ports were opened.
    /// </summary>
    public static void ClosePorts()
    {
        foreach (var m in _opened)
        {
            try
            {
                var natType = Type.GetTypeFromProgID("HNetCfg.NATUPnP");
                if (natType == null) break;
                dynamic upnpnat = Activator.CreateInstance(natType)!;
                dynamic mappings = upnpnat.StaticPortMappingCollection;
                mappings?.Remove(m.Port, m.Protocol);
            }
            catch { }
        }
        _opened.Clear();
    }

    /// <summary>
    /// Get the router's public IP address.
    /// Tries multiple services, returns null if all fail.
    /// </summary>
    public static async Task<string?> GetPublicIPAsync()
    {
        string[] services = {
            "https://api.ipify.org",
            "https://icanhazip.com",
            "https://ifconfig.me/ip"
        };

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        foreach (var url in services)
        {
            try
            {
                string ip = (await http.GetStringAsync(url)).Trim();
                if (System.Net.IPAddress.TryParse(ip, out _))
                    return ip;
            }
            catch { }
        }
        return null;
    }
}
