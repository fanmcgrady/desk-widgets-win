using System.Windows.Forms;

namespace DesktopBox.Services;

/// <summary>电池/市电简易策略，供边栏探测与 MEMORY 状态条降频。</summary>
public static class PowerBudget
{
    public static bool OnBattery
    {
        get
        {
            try { return SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline; }
            catch { return false; }
        }
    }

    /// <summary>侧栏收起且远离左缘时的探测间隔。</summary>
    public static int EdgeIdleMs => OnBattery ? 1200 : 450;

    /// <summary>侧栏收起且靠近左缘。</summary>
    public static int EdgeNearMs => OnBattery ? 250 : 120;

    /// <summary>侧栏展开未钉住。</summary>
    public static int EdgeExpandedMs => OnBattery ? 200 : 80;

    /// <summary>侧栏已钉住。</summary>
    public static int EdgePinnedMs => OnBattery ? 400 : 200;

    /// <summary>MEMORY 状态条采样间隔。</summary>
    public static TimeSpan StatsInterval =>
        OnBattery ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(2);

    /// <summary>电池下 Wi‑Fi 采样更稀（每 N 次 Tick 一次）。</summary>
    public static int WifiEveryNTicks => OnBattery ? 3 : 1;
}
