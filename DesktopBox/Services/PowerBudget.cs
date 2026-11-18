using System.Windows.Forms;

namespace DesktopBox.Services;

/// <summary>边栏探测、底栏采样，以及围栏启动策略。</summary>
public static class PowerBudget
{
    /// <summary>是否在用电池（离线供电）。</summary>
    public static bool OnBattery
    {
        get
        {
            try { return SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline; }
            catch { return false; }
        }
    }

    /// <summary>侧栏收起且远离左缘时的探测间隔。</summary>
    public const int EdgeIdleMs = 900;

    /// <summary>侧栏收起且靠近左缘。</summary>
    public const int EdgeNearMs = 220;

    /// <summary>侧栏展开未钉住。</summary>
    public const int EdgeExpandedMs = 150;

    /// <summary>侧栏已钉住。</summary>
    public const int EdgePinnedMs = 400;

    /// <summary>底栏状态采样间隔。</summary>
    public static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(10);

    /// <summary>Wi‑Fi 每 N 次 Tick 采一次。</summary>
    public const int WifiEveryNTicks = 2;
}
