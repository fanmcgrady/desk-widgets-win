namespace DesktopBox.Services;

/// <summary>边栏探测与底栏状态采样间隔（固定值）。</summary>
public static class PowerBudget
{
    /// <summary>侧栏收起且远离左缘时的探测间隔。</summary>
    public const int EdgeIdleMs = 450;

    /// <summary>侧栏收起且靠近左缘。</summary>
    public const int EdgeNearMs = 120;

    /// <summary>侧栏展开未钉住。</summary>
    public const int EdgeExpandedMs = 80;

    /// <summary>侧栏已钉住。</summary>
    public const int EdgePinnedMs = 200;

    /// <summary>底栏状态采样间隔。</summary>
    public static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(5);

    /// <summary>Wi‑Fi 每 N 次 Tick 采一次。</summary>
    public const int WifiEveryNTicks = 1;
}
