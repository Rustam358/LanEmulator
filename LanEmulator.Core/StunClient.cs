namespace LanEmulator.Core;

/// <summary>
/// Minimal STUN client (RFC 5389).
/// Queries a public STUN server to discover the public IP:port
/// behind NAT. Used for UDP hole punching without UPnP or VPS.
/// </summary>
public static class StunClient
{
    private const int StunPort = 19302;
    private static readonly string[] Servers = { "stun.l.google.com", "stun1.l.google.com" };

    /// <summary>
    /// Returns the public IP:port as seen by the STUN server.
    /// Returns null if all servers fail.
    /// </summary>
    public static async Task<IPEndPoint?> GetPublicEndpointAsync(string? localIP = null, int timeoutMs = 3000)
    {
        foreach (var host in Servers)
        {
            try
            {
                var result = await QueryAsync(host, StunPort, localIP, timeoutMs);
                if (result != null) return result;
            }
            catch { }
        }
        return null;
    }

    private static async Task<IPEndPoint?> QueryAsync(string host, int port, string? localIP, int timeoutMs)
    {
        using var udp = new UdpClient();
        udp.Client.ReceiveTimeout = timeoutMs;
        udp.Client.SendTimeout = timeoutMs;

        // Bind to physical interface if specified (bypass VPN)
        if (localIP != null && IPAddress.TryParse(localIP, out var bindAddr))
            udp.Client.Bind(new IPEndPoint(bindAddr, 0));

        // STUN Binding Request (RFC 5389)
        // Magic cookie: 0x2112A442
        byte[] request = {
            0x00, 0x01,             // Type: Binding Request
            0x00, 0x00,             // Length: 0 (no attributes)
            0x21, 0x12, 0xA4, 0x42, // Magic Cookie
            // Transaction ID (12 bytes, random)
            0,0,0,0,0,0,0,0,0,0,0,0
        };
        Random.Shared.NextBytes(request.AsSpan(8, 12));

        await udp.SendAsync(request, request.Length, host, port);

        var respTask = udp.ReceiveAsync();
        if (await Task.WhenAny(respTask, Task.Delay(timeoutMs)) != respTask)
            return null;

        var result = respTask.Result;
        byte[] resp = result.Buffer;

        // Min STUN header: 20 bytes
        if (resp.Length < 20) return null;

        ushort msgType = (ushort)((resp[0] << 8) | resp[1]);
        if (msgType != 0x0101) return null; // Binding Success Response

        ushort msgLen = (ushort)((resp[2] << 8) | resp[3]);
        // Verify magic cookie
        if (resp[4] != 0x21 || resp[5] != 0x12 || resp[6] != 0xA4 || resp[7] != 0x42)
            return null;

        // Parse attributes after 20-byte header
        int pos = 20;
        int end = 20 + msgLen;
        IPEndPoint? mapped = null;

        while (pos + 4 <= end && pos + 4 <= resp.Length)
        {
            ushort attrType = (ushort)((resp[pos] << 8) | resp[pos + 1]);
            ushort attrLen = (ushort)((resp[pos + 2] << 8) | resp[pos + 3]);
            pos += 4;

            if (attrType == 0x0020 && attrLen >= 8 && pos + attrLen <= resp.Length)
            {
                // XOR-MAPPED-ADDRESS
                int family = resp[pos + 1];
                if (family == 0x01) // IPv4
                {
                    ushort xport = (ushort)(((resp[pos + 2] << 8) | resp[pos + 3]) ^ 0x2112);
                    byte[] xaddr = new byte[4];
                    for (int i = 0; i < 4; i++)
                        xaddr[i] = (byte)(resp[pos + 4 + i] ^ resp[4 + i]); // XOR with magic cookie
                    mapped = new IPEndPoint(new IPAddress(xaddr), xport);
                }
                break;
            }
            else if (attrType == 0x0001 && attrLen >= 8 && pos + attrLen <= resp.Length && mapped == null)
            {
                // MAPPED-ADDRESS (fallback)
                int family = resp[pos + 1];
                if (family == 0x01)
                {
                    int mport = (resp[pos + 2] << 8) | resp[pos + 3];
                    var maddr = new IPAddress(resp[(pos + 4)..(pos + 8)]);
                    mapped = new IPEndPoint(maddr, mport);
                }
            }
            pos += attrLen;
            // Align to 4 bytes
            if ((attrLen % 4) != 0) pos += 4 - (attrLen % 4);
        }

        return mapped;
    }
}
