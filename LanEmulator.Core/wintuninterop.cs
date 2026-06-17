namespace LanEmulator.Core;

public static class WintunInterop
{
    [DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public static extern IntPtr WintunCreateAdapter(string Name, string TunnelType, IntPtr RequestedGUID);

    [DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public static extern IntPtr WintunOpenAdapter(string Name);

    [DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern void WintunCloseAdapter(IntPtr Adapter);

    [DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WintunGetAdapterLUID(IntPtr Adapter, out ulong Luid);

    [DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern uint WintunGetRunningDriverVersion();

    [DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr WintunStartSession(IntPtr Adapter, uint Capacity);

    [DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern void WintunEndSession(IntPtr Session);

    [DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr WintunGetReadWaitEvent(IntPtr Session);

    [DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr WintunReceivePacket(IntPtr Session, out uint PacketSize);

    [DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern void WintunReleaseReceivePacket(IntPtr Session, IntPtr Packet);

    [DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr WintunAllocateSendPacket(IntPtr Session, uint PacketSize);

    [DllImport("wintun.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern void WintunSendPacket(IntPtr Session, IntPtr Packet);

    [DllImport("iphlpapi.dll", CharSet = CharSet.Unicode)]
    public static extern uint ConvertInterfaceLuidToAlias(ref ulong InterfaceLuid, [Out] char[] InterfaceAlias, nuint Length);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForMultipleObjects(uint nCount, IntPtr[] lpHandles, [MarshalAs(UnmanagedType.Bool)] bool fWaitAll, uint dwMilliseconds);
}
