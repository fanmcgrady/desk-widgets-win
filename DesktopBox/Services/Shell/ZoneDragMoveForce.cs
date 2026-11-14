using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace DesktopBox.Services.Shell;

/// <summary>
/// 子类化各区 Shell 视图：
/// 1) 区间拖放默认移动（模拟 Shift）
/// 2) 鼠标侧键后退/前进
/// 3) 滚轮转到 SysListView32，保证上下滚动
/// </summary>
internal static class ZoneDragMoveForce
{
    private const int WmNotify = 0x004E;
    private const int WmDestroy = 0x0002;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmXButtonDown = 0x020B;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmChar = 0x0102;
    private const int WmDeadChar = 0x0103;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmSysChar = 0x0106;
    private const int XButton1 = 0x0001;
    private const int XButton2 = 0x0002;
    private const int LvnFirst = -100;
    private const int LvnBeginDrag = LvnFirst - 9;
    private const int LvnBeginRDrag = LvnFirst - 11;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkLButton = 0x01;
    private const uint KeyeventFKeyup = 0x0002;

    private static readonly ConcurrentDictionary<IntPtr, WeakReference<ExplorerBrowserHost>> Views = new();
    private static readonly SubclassProc SubclassCallback = OnSubclass;
    private static readonly IntPtr SubclassId = (IntPtr)0xDB01;

    private static DispatcherTimer? _timer;
    private static bool _dragActive;
    private static bool _shiftForced;

    public static void RegisterView(IntPtr hwndView, ExplorerBrowserHost host)
    {
        if (hwndView == IntPtr.Zero)
            return;

        Views[hwndView] = new WeakReference<ExplorerBrowserHost>(host);
        SetWindowSubclass(hwndView, SubclassCallback, SubclassId, IntPtr.Zero);
    }

    /// <summary>额外登记并子类化 ListView，便于命中测试与点击获焦。</summary>
    public static void RegisterAlias(IntPtr hwnd, ExplorerBrowserHost host)
    {
        if (hwnd == IntPtr.Zero)
            return;
        Views[hwnd] = new WeakReference<ExplorerBrowserHost>(host);
        SetWindowSubclass(hwnd, SubclassCallback, SubclassId, IntPtr.Zero);
    }

    public static void UnregisterView(IntPtr hwndView)
    {
        if (hwndView == IntPtr.Zero)
            return;

        // 清掉指向同一 host 的别名
        if (Views.TryGetValue(hwndView, out var weak) && weak.TryGetTarget(out var host))
        {
            foreach (var key in Views.Keys)
            {
                if (Views.TryGetValue(key, out var w) && w.TryGetTarget(out var h) && ReferenceEquals(h, host))
                    Views.TryRemove(key, out _);
            }
        }

        Views.TryRemove(hwndView, out _);
        RemoveWindowSubclass(hwndView, SubclassCallback, SubclassId);
    }

    public static void Shutdown()
    {
        ShellWheelHook.Uninstall();
        _dragActive = false;
        StopTimer();
        ReleaseForcedShift();
        foreach (var hwnd in Views.Keys)
            RemoveWindowSubclass(hwnd, SubclassCallback, SubclassId);
        Views.Clear();
    }

    /// <summary>光标下若属于某区视图，返回对应宿主。</summary>
    public static ExplorerBrowserHost? HostFromCursor()
    {
        if (!GetCursorPos(out var pt))
            return null;
        return HostFromScreenPoint(pt.X, pt.Y);
    }

    /// <summary>
    /// 仅 WindowFromPoint 父链命中。滚轮钩子必须用这个：
    /// 矩形回退会在其它窗口盖住围栏时仍吞滚轮。
    /// </summary>
    public static ExplorerBrowserHost? HostFromScreenPointStrict(int x, int y)
    {
        var hwnd = WindowFromPoint(new Point { X = x, Y = y });
        var walk = hwnd;
        while (walk != IntPtr.Zero)
        {
            if (Views.TryGetValue(walk, out var weak) && weak.TryGetTarget(out var host))
                return host;
            walk = GetParent(walk);
        }

        return null;
    }

    public static ExplorerBrowserHost? HostFromScreenPoint(int x, int y)
    {
        // 1) WindowFromPoint 沿父链
        var strict = HostFromScreenPointStrict(x, y);
        if (strict is not null)
            return strict;

        var hwnd = WindowFromPoint(new Point { X = x, Y = y });
        if (hwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            // 其它进程窗口盖在上面：禁止矩形回退，否则滚轮/拖放会误判
            if (pid != 0 && pid != (uint)Environment.ProcessId)
                return null;
        }

        // 2) 同进程内按窗口屏幕矩形命中（WPF 主窗盖住 Shell 时 WindowFromPoint 常不准）
        ExplorerBrowserHost? best = null;
        var bestArea = long.MaxValue;
        foreach (var kv in Views)
        {
            if (!kv.Value.TryGetTarget(out var host))
                continue;
            if (!IsWindow(kv.Key))
            {
                Views.TryRemove(kv.Key, out _);
                continue;
            }
            if (!GetWindowRect(kv.Key, out var rc))
                continue;
            if (x < rc.Left || x >= rc.Right || y < rc.Top || y >= rc.Bottom)
                continue;

            long area = (long)(rc.Right - rc.Left) * (rc.Bottom - rc.Top);
            if (area > 0 && area < bestArea)
            {
                bestArea = area;
                best = host;
            }
        }

        return best;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static bool IsDescendantOrSelf(IntPtr ancestor, IntPtr hwnd)
    {
        while (hwnd != IntPtr.Zero)
        {
            if (hwnd == ancestor)
                return true;
            hwnd = GetParent(hwnd);
        }

        return false;
    }

    private static IntPtr OnSubclass(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uid, IntPtr data)
    {
        if (msg is WmLButtonDown or WmRButtonDown)
        {
            if (TryGetHost(hwnd, out var host))
                host.ActivateForInput();
        }
        else if (msg is WmKeyDown or WmKeyUp or WmChar or WmDeadChar
                 or WmSysKeyDown or WmSysKeyUp or WmSysChar)
        {
            // 嵌入 IShellView：Delete/F2/Ctrl+A 等由 TranslateAccelerator 处理，
            // 不能只靠 SysListView32 默认行为。
            if (TryGetHost(hwnd, out var host)
                && host.TryTranslateAccelerator(hwnd, (int)msg, wParam, lParam))
                return (IntPtr)1;
        }
        else if (msg == WmXButtonDown)
        {
            if (TryGetHost(hwnd, out var host))
            {
                host.ActivateForInput();
                var button = ((int)wParam >> 16) & 0xffff;
                if (button == XButton1)
                {
                    host.GoBack();
                    return IntPtr.Zero;
                }

                if (button == XButton2)
                {
                    host.GoForward();
                    return IntPtr.Zero;
                }
            }
        }
        else if (msg is 0x020A or 0x020E) // WM_MOUSEWHEEL / WM_MOUSEHWHEEL
        {
            // 消息已落到本区子窗时本地滚动，减轻对全局钩子的依赖
            if (TryGetHost(hwnd, out var host))
            {
                var lv = host.GetListViewHwndPublic();
                if (lv != IntPtr.Zero)
                {
                    var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                    ShellWheelHook.ScrollListView(lv, delta, horizontal: msg == 0x020E);
                    return (IntPtr)1;
                }
            }
        }
        else if (msg == WmNotify && lParam != IntPtr.Zero)
        {
            var code = Marshal.ReadInt32(lParam, IntPtr.Size * 2);
            if (code == LvnBeginDrag || code == LvnBeginRDrag)
                BeginDragWatch();
        }
        else if (msg == WmDestroy)
        {
            UnregisterView(hwnd);
        }

        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private static bool TryGetHost(IntPtr hwnd, out ExplorerBrowserHost host)
    {
        host = null!;
        return Views.TryGetValue(hwnd, out var weak) && weak.TryGetTarget(out host!);
    }

    private static IntPtr FindDescendantByClass(IntPtr root, string className)
    {
        var queue = new Queue<IntPtr>();
        queue.Enqueue(root);
        var sb = new StringBuilder(256);

        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            var child = IntPtr.Zero;
            while (true)
            {
                child = FindWindowEx(parent, child, null, null);
                if (child == IntPtr.Zero)
                    break;

                sb.Clear();
                GetClassName(child, sb, sb.Capacity);
                if (string.Equals(sb.ToString(), className, StringComparison.OrdinalIgnoreCase))
                    return child;

                queue.Enqueue(child);
            }
        }

        return IntPtr.Zero;
    }

    private static void BeginDragWatch()
    {
        _dragActive = true;
        EnsureTimer();
    }

    private static void EnsureTimer()
    {
        if (_timer is not null)
            return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        _timer = new DispatcherTimer(DispatcherPriority.Input, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private static void StopTimer()
    {
        if (_timer is null)
            return;
        _timer.Stop();
        _timer = null;
    }

    private static void Tick()
    {
        if (!_dragActive)
        {
            ReleaseForcedShift();
            StopTimer();
            return;
        }

        var lButtonDown = (GetAsyncKeyState(VkLButton) & 0x8000) != 0;
        if (!lButtonDown)
        {
            _dragActive = false;
            ReleaseForcedShift();
            StopTimer();
            return;
        }

        if ((GetAsyncKeyState(VkControl) & 0x8000) != 0)
        {
            ReleaseForcedShift();
            return;
        }

        if (!GetCursorPos(out var pt) || !IsOverRegisteredView(pt))
        {
            ReleaseForcedShift();
            return;
        }

        EnsureShiftDown();
    }

    private static bool IsOverRegisteredView(Point pt)
    {
        var hwnd = WindowFromPoint(pt);
        while (hwnd != IntPtr.Zero)
        {
            if (Views.ContainsKey(hwnd))
                return true;
            hwnd = GetParent(hwnd);
        }

        return false;
    }

    private static void EnsureShiftDown()
    {
        if (_shiftForced)
            return;
        keybd_event((byte)VkShift, 0, 0, UIntPtr.Zero);
        _shiftForced = true;
    }

    private static void ReleaseForcedShift()
    {
        if (!_shiftForced)
            return;
        keybd_event((byte)VkShift, 0, KeyeventFKeyup, UIntPtr.Zero);
        _shiftForced = false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, IntPtr uIdSubclass);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
