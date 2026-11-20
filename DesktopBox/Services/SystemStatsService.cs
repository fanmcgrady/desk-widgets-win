using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace DesktopBox.Services;

/// <summary>采样 Cursor 用量 / Wi‑Fi / 开机时长。</summary>
internal sealed class SystemStatsService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private bool _started;
    private int _tick;
    private WifiInfo _lastWifi = WifiInfo.Unavailable;

    public event EventHandler<SystemStatsSample>? Sampled;

    public SystemStatsService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = PowerBudget.StatsInterval
        };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _tick = 0;
        _timer.Interval = PowerBudget.StatsInterval;
        CursorUsageService.Updated -= OnCursorUsageUpdated;
        CursorUsageService.Updated += OnCursorUsageUpdated;
        _ = CursorUsageService.PeekAndRefresh();
        _timer.Start();
        Tick();
    }

    public void Stop()
    {
        _timer.Stop();
        _started = false;
        CursorUsageService.Updated -= OnCursorUsageUpdated;
    }

    public void Dispose()
    {
        Stop();
        Sampled = null;
    }

    private void OnCursorUsageUpdated()
    {
        // 拉取完成立刻刷底栏，不必等 10 秒 Tick
        var dispatcher = _timer.Dispatcher;
        if (dispatcher.CheckAccess())
            Tick();
        else
            dispatcher.BeginInvoke(Tick);
    }

    private void Tick()
    {
        try
        {
            var usage = CursorUsageService.PeekAndRefresh();
            if (_tick++ % PowerBudget.WifiEveryNTicks == 0)
                _lastWifi = ReadWifiSafe();
            var uptime = TimeSpan.FromMilliseconds(GetTickCount64());
            Sampled?.Invoke(this, new SystemStatsSample(usage, _lastWifi, uptime));
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
                var itemPtr = listPtr + 8;

                for (var i = 0; i < numberOfItems; i++)
                {
                    var info = Marshal.PtrToStructure<WlanInterfaceInfo>(itemPtr + i * itemSize);
                    if (info.State != 1)
                        continue;

                    var guid = info.InterfaceGuid;
                    if (WlanQueryInterface(client, ref guid, 7 /* current_connection */, IntPtr.Zero,
                            out _, out var data, out _) != 0
                        || data == IntPtr.Zero)
                        continue;

                    try
                    {
                        const int profileOff = 8;
                        const int assoc = profileOff + 256 * 2;
                        var profile = Marshal.PtrToStringUni(IntPtr.Add(data, profileOff))?.Trim() ?? "";
                        if (profile.Length > 64) profile = profile[..64];

                        var ssidLen = Marshal.ReadInt32(data, assoc);
                        var ssid = "";
                        if (ssidLen > 0 && ssidLen <= 32)
                        {
                            var bytes = new byte[ssidLen];
                            Marshal.Copy(IntPtr.Add(data, assoc + 4), bytes, 0, ssidLen);
                            ssid = Encoding.UTF8.GetString(bytes).Trim('\0').Trim();
                            if (ssid.Any(ch => ch < 0x20 && ch != '\t'))
                                ssid = Encoding.Default.GetString(bytes).Trim('\0').Trim();
                        }

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Description;
        public int State;
    }

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

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    public static string FormatUptime(TimeSpan t)
    {
        if (t.TotalDays >= 1)
            return $"{(int)t.TotalDays} 天 {t.Hours} 时";
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours} 时 {t.Minutes} 分";
        return $"{t.Minutes} 分 {t.Seconds} 秒";
    }
}

internal readonly record struct WifiInfo(bool Connected, int SignalPercent, string Ssid)
{
    /// <summary>SignalPercent &lt; 0 表示有连接但读不到强度。</summary>
    public static WifiInfo Unavailable => new(false, -2, "");
    public bool HasAdapterOrLink => SignalPercent != -2;
}

internal readonly record struct SystemStatsSample(
    CursorUsageInfo Cursor,
    WifiInfo Wifi,
    TimeSpan Uptime);
