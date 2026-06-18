using static LanEmulator.Core.WintunInterop;

using System.Diagnostics;

namespace LanEmulator.Core;

public static class Pumps
{
    public delegate void SignalingHandler(UdpClient udp, byte[] data, int length, IPEndPoint remote);

    /// <summary>
    /// Pump UDP packets into the TUN adapter.
    /// Signaling packets (UdpSignaling magic) are dispatched to handler instead.
    /// </summary>
    public static void PumpNetworkToTun(UdpClient udp, IntPtr session, CancellationToken ct,
        SignalingHandler? onSignaling = null)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var task = udp.ReceiveAsync(ct);
                task.AsTask().Wait(ct);
                byte[] packet = task.Result.Buffer;
                if (packet.Length == 0) continue;

                // Dispatch signaling packets
                if (packet.Length >= 4 &&
                    packet[0] == UdpSignaling.Magic0 &&
                    packet[1] == UdpSignaling.Magic1 &&
                    packet[2] == UdpSignaling.Magic2)
                {
                    onSignaling?.Invoke(udp, packet, packet.Length, task.Result.RemoteEndPoint);
                    continue;
                }

                IntPtr sendBuf = WintunAllocateSendPacket(session, (uint)packet.Length);
                if (sendBuf == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == 111) continue; // ring full
                    if (err == 38) break; // handle invalid
                    continue;
                }
                Marshal.Copy(packet, 0, sendBuf, packet.Length);
                WintunSendPacket(session, sendBuf);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Trace.WriteLine($"PumpNetToTun error: {ex.Message}"); }
    }

    public static void PumpTunToNet(UdpClient udp, IntPtr session,
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
                        if (err == 259) break; // no more packets
                        if (err == 38) return; // handle invalid
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
                if (WaitForMultipleObjects(1, arr, false, 500u) == unchecked((uint)-1)) break;
            }
        }
        catch (OperationCanceledException) { }
        finally { wh.SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(IntPtr.Zero, ownsHandle: false); }
        wh.Dispose();
    }

    private static bool IsBroadcast(IPAddress ip)
    {
        byte[] b = ip.GetAddressBytes();
        return (b[0] == 255 && b[3] == 255) || b[3] == 255;
    }
}
