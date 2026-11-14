using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace DesktopBox.Services;

/// <summary>按秒采样 CPU / 内存 / 网速 / Wi‑Fi，供内存区底部状态条。</summary>
internal sealed class SystemStatsService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly PerformanceCounter? _cpu;
    private long _prevRecv = -1;
    private long _prevSent = -1;
    private DateTime _prevUtc = DateTime.MinValue;
    private bool _started;
    private bool _cpuPrimed;

    public event EventHandler<SystemStatsSample>? Sampled;

    public SystemStatsService()
    {
        try
        {
            _cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpu.NextValue();
        }
        catch
        {
            _cpu = null;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        SnapshotNet(out _prevRecv, out _prevSent);
        _prevUtc = DateTime.UtcNow;
        _cpuPrimed = false;
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _started = false;
        _prevRecv = -1;
        _prevSent = -1;
    }

    public void Dispose()
    {
        Stop();
        Sampled = null;
        _cpu?.Dispose();
    }

    private void Tick()
    {
        try
        {
            var cpu = ReadCpu();
            var mem = ReadMemory();
            var wifi = ReadWifiSafe();
            ReadNetRates(out var down, out var up);
            Sampled?.Invoke(this, new SystemStatsSample(cpu, mem, down, up, wifi));
        }
        catch
        {
            // 状态条采样失败不拖垮宿主
        }
    }

    private static WifiInfo ReadWifiSafe()
    {
        try { return ReadWifi(); }
        catch { return WifiInfo.Unavailable; }
    }

    private double ReadCpu()
    {
        if (_cpu is null) return -1;
        try
        {
            var v = _cpu.NextValue();
            if (!_cpuPrimed)
            {
                _cpuPrimed = true;
                return -1;
            }
            return Math.Clamp(v, 0, 100);
        }
        catch
        {
            return -1;
        }
    }

    private static MemoryInfo ReadMemory()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status))
                return MemoryInfo.Unknown;
            return new MemoryInfo(
                (long)status.TotalPhys,
                (long)(status.TotalPhys - status.AvailPhys),
                status.MemoryLoad);
        }
        catch
        {
            return MemoryInfo.Unknown;
        }
    }

    private static WifiInfo ReadWifi()
    {
        IntPtr client = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out client) != 0 || client == IntPtr.Zero)
                return FallbackWifi();

            if (WlanEnumInterfaces(client, IntPtr.Zero, out var listPtr) != 0 || listPtr == IntPtr.Zero)
                return FallbackWifi();

            try
            {
                var numberOfItems = Marshal.ReadInt32(listPtr);
                var itemSize = Marshal.SizeOf<WlanInterfaceInfo>();
                // WLAN_INTERFACE_INFO_LIST: dwNumberOfItems + dwIndex = 8
                var itemPtr = listPtr + 8;

                for (var i = 0; i < numberOfItems; i++)
                {
                    var info = Marshal.PtrToStructure<WlanInterfaceInfo>(itemPtr + i * itemSize);
                    if (info.State != 1) // connected
                        continue;

                    var guid = info.InterfaceGuid;
                    if (WlanQueryInterface(client, ref guid, 7 /* current_connection */, IntPtr.Zero,
                            out _, out var data, out _) != 0
                        || data == IntPtr.Zero)
                        continue;

                    try
                    {
                        // WLAN_CONNECTION_ATTRIBUTES (x64)：
                        // isState+connMode=8，strProfileName[256]=512，assoc 从 520
                        const int profileOff = 8;
                        const int assoc = profileOff + 256 * 2; // 520
                        var profile = Marshal.PtrToStringUni(IntPtr.Add(data, profileOff))?.Trim() ?? "";
                        if (profile.Length > 64) profile = profile[..64];

                        var ssidLen = Marshal.ReadInt32(data, assoc);
                        var ssid = "";
                        if (ssidLen > 0 && ssidLen <= 32)
                        {
                            var bytes = new byte[ssidLen];
                            Marshal.Copy(IntPtr.Add(data, assoc + 4), bytes, 0, ssidLen);
                            // SSID 常为 UTF-8；若像乱码再试 Latin1 原始字节当 ANSI
                            ssid = Encoding.UTF8.GetString(bytes).Trim('\0').Trim();
                            if (ssid.Any(ch => ch < 0x20 && ch != '\t'))
                                ssid = Encoding.Default.GetString(bytes).Trim('\0').Trim();
                        }

                        // DOT11_SSID(36)+bssType(4)+bssid(6)+pad(2)+phyType(4)+phyIndex(4)=56
                        var quality = Marshal.ReadInt32(data, assoc + 56);
                        quality = Math.Clamp(quality, 0, 100);

                        if (string.IsNullOrWhiteSpace(ssid))
                            ssid = profile;
                        if (string.IsNullOrWhiteSpace(ssid))
                            ssid = "已连接";

                        return new WifiInfo(true, quality, ssid);
                    }
                    finally
                    {
                        WlanFreeMemory(data);
                    }
                }
            }
            finally
            {
                WlanFreeMemory(listPtr);
            }

            return HasWifiAdapter()
                ? new WifiInfo(false, 0, "未连接")
                : WifiInfo.Unavailable;
        }
        catch
        {
            return FallbackWifi();
        }
        finally
        {
            if (client != IntPtr.Zero)
                WlanCloseHandle(client, IntPtr.Zero);
        }
    }

    private static WifiInfo FallbackWifi()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
                    continue;
                if (nic.OperationalStatus == OperationalStatus.Up)
                    return new WifiInfo(true, -1, nic.Name);
                return new WifiInfo(false, 0, "未连接");
            }
        }
        catch { /* ignore */ }
        return WifiInfo.Unavailable;
    }

    private static bool HasWifiAdapter()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    return true;
            }
        }
        catch { /* ignore */ }
        return false;
    }

    private void ReadNetRates(out double down, out double up)
    {
        down = 0;
        up = 0;
        SnapshotNet(out var recv, out var sent);
        var now = DateTime.UtcNow;
        if (_prevRecv < 0 || _prevSent < 0)
        {
            _prevRecv = recv;
            _prevSent = sent;
            _prevUtc = now;
            return;
        }

        var dt = (now - _prevUtc).TotalSeconds;
        if (dt < 0.2) return;

        down = Math.Max(0, (recv - _prevRecv) / dt);
        up = Math.Max(0, (sent - _prevSent) / dt);
        _prevRecv = recv;
        _prevSent = sent;
        _prevUtc = now;
    }

    private static void SnapshotNet(out long recv, out long sent)
    {
        long r = 0, s = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                    or NetworkInterfaceType.Tunnel)
                    continue;
                try
                {
                    var stats = nic.GetIPStatistics();
                    r += stats.BytesReceived;
                    s += stats.BytesSent;
                }
                catch { /* skip */ }
            }
        }
        catch { /* ignore */ }

        recv = r;
        sent = s;
    }

    public static string FormatRate(double bytesPerSec)
    {
        if (bytesPerSec < 1024)
            return $"{bytesPerSec:0} B/s";
        if (bytesPerSec < 1024 * 1024)
            return $"{bytesPerSec / 1024:0.0} KB/s";
        if (bytesPerSec < 1024L * 1024 * 1024)
            return $"{bytesPerSec / (1024 * 1024):0.00} MB/s";
        return $"{bytesPerSec / (1024 * 1024 * 1024):0.00} GB/s";
    }

    public static string FormatBytes(long bytes)
    {
        double v = bytes;
        if (v < 1024)
            return $"{v:0} B";
        if (v < 1024 * 1024)
            return $"{v / 1024:0.0} KB";
        if (v < 1024L * 1024 * 1024)
            return $"{v / (1024 * 1024):0.0} MB";
        return $"{v / (1024L * 1024 * 1024):0.00} GB";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Description;
        public int State;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(
        IntPtr hClientHandle,
        ref Guid pInterfaceGuid,
        int OpCode,
        IntPtr pReserved,
        out uint pdwDataSize,
        out IntPtr ppData,
        out int pWlanOpcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pMemory);
}

internal readonly record struct MemoryInfo(long TotalBytes, long UsedBytes, uint LoadPercent)
{
    public static MemoryInfo Unknown => new(0, 0, 0);
    public bool IsValid => TotalBytes > 0;
}

internal readonly record struct WifiInfo(bool Connected, int SignalPercent, string Ssid)
{
    /// <summary>SignalPercent &lt; 0 表示有连接但读不到强度。</summary>
    public static WifiInfo Unavailable => new(false, -2, "");
    public bool HasAdapterOrLink => SignalPercent != -2;
}

internal readonly record struct SystemStatsSample(
    double CpuPercent,
    MemoryInfo Memory,
    double DownBytesPerSec,
    double UpBytesPerSec,
    WifiInfo Wifi);
