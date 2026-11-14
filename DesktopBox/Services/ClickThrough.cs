using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DesktopBox.Services;

internal static class ClickThrough
{
    public static IntPtr EnsureHandle(Window window)
    {
        var helper = new WindowInteropHelper(window);
        if (helper.Handle == IntPtr.Zero)
            helper.EnsureHandle();
        return helper.Handle;
    }

    /// <summary>
    /// 工具窗样式：非置顶、非分层；命中范围交给 SetWindowRgn。
    /// 样式已正确时不调用 SetWindowPos，避免 Shell 子窗闪烁。
    /// </summary>
    public static void ApplyFenceStyle(Window window)
    {
        var hwnd = EnsureHandle(window);
        if (hwnd == IntPtr.Zero)
            return;

        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GwlExstyle);
        var desired = ex;
        // 保留 TOOLWINDOW（不进任务栏）；去掉 NOACTIVATE，否则区内列表无法获焦，键盘/滚轮都无效
        desired |= NativeMethods.WsExToolwindow;
        desired &= ~NativeMethods.WsExNoactivate;
        desired &= ~NativeMethods.WsExTransparent;
        desired &= ~NativeMethods.WsExLayered;
        desired &= ~NativeMethods.WsExTopmost;

        if (desired == ex)
            return;

        NativeMethods.SetWindowLong(hwnd, NativeMethods.GwlExstyle, desired);

        // 仅在清掉 TOPMOST 时改一次 Z 序
        if ((ex & NativeMethods.WsExTopmost) != 0)
        {
            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HwndNotopmost,
                0, 0, 0, 0,
                NativeMethods.SwpNomove | NativeMethods.SwpNosize | NativeMethods.SwpNoactivate);
        }
    }

    /// <summary>
    /// 把围栏贴在桌面图标层之上、普通应用之下（绝不 HWND_TOPMOST）。
    /// 已在正确位置时直接返回 false，避免反复 SetWindowPos 闪烁。
    /// </summary>
    public static bool PlaceAboveDesktopIcons(Window window)
    {
        var hwnd = EnsureHandle(window);
        if (hwnd == IntPtr.Zero)
            return false;

        // 先确保不是置顶
        var ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GwlExstyle);
        if ((ex & NativeMethods.WsExTopmost) != 0)
        {
            ex &= ~NativeMethods.WsExTopmost;
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GwlExstyle, ex);
            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HwndNotopmost,
                0, 0, 0, 0,
                NativeMethods.SwpNomove | NativeMethods.SwpNosize | NativeMethods.SwpNoactivate);
        }

        var desktop = FindDesktopShellWindow();
        if (desktop == IntPtr.Zero)
        {
            // 已在最底则不动
            var next = NativeMethods.GetWindow(hwnd, NativeMethods.GwHwndNext);
            if (next == IntPtr.Zero)
                return false;

            Log("desktop shell window not found; fallback HWND_BOTTOM");
            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HwndBottom,
                0, 0, 0, 0,
                NativeMethods.SwpNomove | NativeMethods.SwpNosize | NativeMethods.SwpNoactivate);
            return true;
        }

        // 紧贴桌面之上：我们的下一窗（更低一层）就是 desktop
        if (NativeMethods.GetWindow(hwnd, NativeMethods.GwHwndNext) == desktop)
            return false;

        // hWndInsertAfter = 当前位于 desktop 之上的窗口 → 我们被插到它下面，即紧贴 desktop 之上
        var aboveDesktop = NativeMethods.GetWindow(desktop, NativeMethods.GwHwndPrev);
        if (aboveDesktop == IntPtr.Zero || aboveDesktop == hwnd)
        {
            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HwndTop,
                0, 0, 0, 0,
                NativeMethods.SwpNomove | NativeMethods.SwpNosize | NativeMethods.SwpNoactivate);
        }
        else
        {
            NativeMethods.SetWindowPos(
                hwnd,
                aboveDesktop,
                0, 0, 0, 0,
                NativeMethods.SwpNomove | NativeMethods.SwpNosize | NativeMethods.SwpNoactivate);
        }

        return true;
    }

    public static void ApplyZoneRegion(Window window, IEnumerable<Rect> dipZones, double cornerRadiusDip)
    {
        var hwnd = EnsureHandle(window);
        if (hwnd == IntPtr.Zero)
            return;

        var src = PresentationSource.FromVisual(window);
        var toDevice = src?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var scaleX = toDevice.M11;
        var scaleY = toDevice.M22;
        var corner = (int)Math.Round(cornerRadiusDip * Math.Min(scaleX, scaleY));
        if (corner > 0 && corner < 2)
            corner = 2;

        var combined = NativeMethods.CreateRectRgn(0, 0, 0, 0);
        foreach (var zone in dipZones)
        {
            if (zone.Width < 8 || zone.Height < 8)
                continue;

            var x1 = (int)Math.Floor(zone.X * scaleX);
            var y1 = (int)Math.Floor(zone.Y * scaleY);
            var x2 = (int)Math.Ceiling((zone.X + zone.Width) * scaleX);
            var y2 = (int)Math.Ceiling((zone.Y + zone.Height) * scaleY);
            // corner=0 用直角矩形，避免 CreateRoundRectRgn 最小圆角导致发虚
            var part = corner <= 0
                ? NativeMethods.CreateRectRgn(x1, y1, x2, y2)
                : NativeMethods.CreateRoundRectRgn(x1, y1, x2, y2, corner, corner);
            NativeMethods.CombineRgn(combined, combined, part, NativeMethods.RgnOr);
            NativeMethods.DeleteObject(part);
        }

        NativeMethods.SetWindowRgn(hwnd, combined, true);
    }

    public static void ClearRegion(Window window)
    {
        var hwnd = EnsureHandle(window);
        if (hwnd == IntPtr.Zero)
            return;
        NativeMethods.SetWindowRgn(hwnd, IntPtr.Zero, true);
    }

    /// <summary>查找承载 SHELLDLL_DefView 的 Progman / WorkerW 顶层窗。</summary>
    private static IntPtr FindDesktopShellWindow()
    {
        var progman = NativeMethods.FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
        {
            var defView = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
                return progman;
        }

        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((h, _) =>
        {
            var sb = new StringBuilder(64);
            if (NativeMethods.GetClassName(h, sb, sb.Capacity) <= 0)
                return true;
            if (!string.Equals(sb.ToString(), "WorkerW", StringComparison.Ordinal))
                return true;

            var defView = NativeMethods.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
            {
                found = h;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.ConfigDirectory);
            File.AppendAllText(
                Path.Combine(AppConfig.ConfigDirectory, "debug.log"),
                $"{DateTime.Now:HH:mm:ss.fff} zorder {message}\n");
        }
        catch
        {
            // ignore
        }
    }
}
