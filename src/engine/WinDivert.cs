// WinDivert 2.x interop layer
// Sadece cekirdek surucusuyle konusan ince katman. Hot-path'te allocation yok.
using System;
using System.Runtime.InteropServices;

namespace Etodpx.Engine
{
    internal enum DivertLayer
    {
        Network = 0,
        NetworkForward = 1,
        Flow = 2,
        Socket = 3,
        Reflect = 4
    }

    [Flags]
    internal enum DivertFlags : ulong
    {
        None = 0,
        Sniff = 0x0001,
        Drop = 0x0002,
        RecvOnly = 0x0004,
        SendOnly = 0x0008,
        NoInstall = 0x0010,
        Fragments = 0x0020
    }

    internal enum DivertParam
    {
        QueueLength = 0,
        QueueTime = 1,
        QueueSize = 2,
        VersionMajor = 3,
        VersionMinor = 4
    }

    [Flags]
    internal enum ChecksumFlags : ulong
    {
        All = 0,
        NoIp = 0x0001,
        NoIcmp = 0x0002,
        NoIcmpV6 = 0x0004,
        NoTcp = 0x0008,
        NoUdp = 0x0010,
        NoReplace = 0x0020
    }

    // WINDIVERT_ADDRESS 80 bayttir ve icinde bitfield'lar var. Bitfield'lari
    // C# tarafinda tasimak yerine ham dword'u tutup maskeliyoruz; boylece
    // struct layout'u surum farklarindan etkilenmiyor.
    [StructLayout(LayoutKind.Sequential, Size = 80)]
    internal struct DivertAddress
    {
        public long Timestamp;   // 0
        public uint Bits;        // 8  : Layer:8 | Event:8 | Sniffed | Outbound | Loopback | Impostor | IPv6 | IPChk | TCPChk | UDPChk
        public uint Reserved2;   // 12
        public uint IfIdx;       // 16 (union: WINDIVERT_DATA_NETWORK)
        public uint SubIfIdx;    // 20
        private ulong pad0, pad1, pad2, pad3, pad4, pad5, pad6; // 24..79

        private const int BitSniffed = 16;
        private const int BitOutbound = 17;
        private const int BitLoopback = 18;
        private const int BitImpostor = 19;
        private const int BitIPv6 = 20;
        private const int BitIpChecksum = 21;
        private const int BitTcpChecksum = 22;
        private const int BitUdpChecksum = 23;

        private bool Get(int bit) { return ((Bits >> bit) & 1u) != 0u; }
        private void Set(int bit, bool v)
        {
            if (v) Bits |= (1u << bit);
            else Bits &= ~(1u << bit);
        }

        public bool Outbound { get { return Get(BitOutbound); } set { Set(BitOutbound, value); } }
        public bool Loopback { get { return Get(BitLoopback); } }
        public bool Impostor { get { return Get(BitImpostor); } set { Set(BitImpostor, value); } }
        public bool IPv6 { get { return Get(BitIPv6); } }
        public bool IpChecksum { get { return Get(BitIpChecksum); } set { Set(BitIpChecksum, value); } }
        public bool TcpChecksum { get { return Get(BitTcpChecksum); } set { Set(BitTcpChecksum, value); } }
        public bool UdpChecksum { get { return Get(BitUdpChecksum); } set { Set(BitUdpChecksum, value); } }
    }

    internal static unsafe class WinDivert
    {
        public const string Dll = "WinDivert.dll";
        public static readonly IntPtr InvalidHandle = new IntPtr(-1);

        public const uint ShutdownRecv = 0x1;
        public const uint ShutdownSend = 0x2;
        public const uint ShutdownBoth = 0x3;

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, SetLastError = true, CharSet = CharSet.Ansi)]
        public static extern IntPtr WinDivertOpen(
            [MarshalAs(UnmanagedType.LPStr)] string filter, DivertLayer layer, short priority, ulong flags);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinDivertRecv(
            IntPtr handle, byte* pPacket, uint packetLen, uint* pRecvLen, DivertAddress* pAddr);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinDivertSend(
            IntPtr handle, byte* pPacket, uint packetLen, uint* pSendLen, DivertAddress* pAddr);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinDivertShutdown(IntPtr handle, uint how);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinDivertClose(IntPtr handle);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinDivertSetParam(IntPtr handle, DivertParam param, ulong value);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinDivertGetParam(IntPtr handle, DivertParam param, ulong* pValue);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinDivertHelperCalcChecksums(
            byte* pPacket, uint packetLen, DivertAddress* pAddr, ulong flags);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinDivertHelperCompileFilter(
            [MarshalAs(UnmanagedType.LPStr)] string filter, DivertLayer layer,
            byte* obj, uint objLen, byte** errorStr, uint* errorPos);
    }
}
