using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace DesktopBox.Services.Shell;

/// <summary>
/// 子类化各区 Shell 视图：
/// 1) 区间拖放：按配置强制复制(Ctrl)或移动(Shift)
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

    private static readonly ConcurrentDictionary<IntPtr, WeakReference<ExplorerBrowserHost>> Views = new();
    private static readonly SubclassProc SubclassCallback = OnSubclass;
    private static readonly IntPtr SubclassId = (IntPtr)0xDB01;

    private static DispatcherTimer? _timer;
    private static bool _dragActive;
    private static bool _pressStartedInView;
    private static bool _shiftForced;
    private static bool _ctrlForced;
    /// <summary>true=拖入强制复制；false=强制移动（默认）。</summary>
    private static bool _preferCopy;
    private static readonly ConcurrentDictionary<IntPtr, ForceEffectDropTarget> DropTargets = new();

    /// <summary>供 ForceEffectDropTarget 读取当前拖放模式。</summary>
    public static bool PreferCopy => _preferCopy;

    public static void SetDropMode(string? mode)
    {
        _preferCopy = string.Equals(mode, "copy", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 拆掉窗口自带 IDropTarget，换成自管移动/复制。
    /// DefView 与 SysListView32 都要挂（实际落点常在 ListView）。
    /// </summary>
    public static void InstallFolderDropTarget(IntPtr hwnd, Func<string> getDestFolder)
    {
        if (hwnd == IntPtr.Zero || getDestFolder is null)
            return;

        UninstallFolderDropTarget(hwnd);
        _ = OleDropNative.RevokeDragDrop(hwnd);

        var wrapper = new ForceEffectDropTarget(getDestFolder);
        var hr = OleDropNative.RegisterDragDrop(hwnd, wrapper);
        if (hr == 0)
            DropTargets[hwnd] = wrapper;
    }

    public static void UninstallFolderDropTarget(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;
        if (!DropTargets.TryRemove(hwnd, out _))
            return;
        try { OleDropNative.RevokeDragDrop(hwnd); } catch { /* ignore */ }
    }

    /// <summary>
    /// 登记 DefView：只加入命中表并子类化以收 LVN 拖放通知（区间默认移动）。
    /// 滚轮/键盘/侧键走 ListView 子类化。
    /// </summary>
    public static void RegisterView(IntPtr hwndView, ExplorerBrowserHost host)
    {
        if (hwndView == IntPtr.Zero)
            return;

        Views[hwndView] = new WeakReference<ExplorerBrowserHost>(host);
        SetWindowSubclass(hwndView, SubclassCallback, SubclassId, IntPtr.Zero);
        EnsureWatchTimer();
    }

    /// <summary>
    /// 登记别名。仅 SysListView32 再子类化；Host 等只进命中表，少挂一层钩子。
    /// </summary>
    public static void RegisterAlias(IntPtr hwnd, ExplorerBrowserHost host)
    {
        if (hwnd == IntPtr.Zero)
            return;
        Views[hwnd] = new WeakReference<ExplorerBrowserHost>(host);
        if (IsClass(hwnd, "SysListView32"))
            SetWindowSubclass(hwnd, SubclassCallback, SubclassId, IntPtr.Zero);
        EnsureWatchTimer();
    }

    public static void UnregisterView(IntPtr hwndView)
    {
        if (hwndView == IntPtr.Zero)
            return;

        // 清掉指向同一 host 的别名、拖放目标与子类化
        if (Views.TryGetValue(hwndView, out var weak) && weak.TryGetTarget(out var host))
        {
            foreach (var key in Views.Keys.ToArray())
            {
                if (Views.TryGetValue(key, out var w) && w.TryGetTarget(out var h) && ReferenceEquals(h, host))
                {
                    UninstallFolderDropTarget(key);
                    RemoveWindowSubclass(key, SubclassCallback, SubclassId);
                    Views.TryRemove(key, out _);
                }
            }
        }

        UninstallFolderDropTarget(hwndView);
        RemoveWindowSubclass(hwndView, SubclassCallback, SubclassId);
        Views.TryRemove(hwndView, out _);
    }

    private static bool IsClass(IntPtr hwnd, string className)
    {
        var sb = new StringBuilder(64);
        return GetClassName(hwnd, sb, sb.Capacity) > 0
               && string.Equals(sb.ToString(), className, StringComparison.OrdinalIgnoreCase);
    }

    public static void Shutdown()
    {
        ShellWheelHook.Uninstall();
        _dragActive = false;
        StopTimer();
        ReleaseForcedModifiers();
        foreach (var hwnd in DropTargets.Keys.ToArray())
            UninstallFolderDropTarget(hwnd);
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
            _pressStartedInView = true;
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
        EnsureWatchTimer();
    }

    /// <summary>常驻轻量轮询：覆盖「从外部拖进分区」（无 LVN_BEGINDRAG）。</summary>
    private static void EnsureWatchTimer()
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
        if (Views.IsEmpty)
        {
            ReleaseForcedModifiers();
            _dragActive = false;
            _pressStartedInView = false;
            StopTimer();
            return;
        }

        var lButtonDown = (GetAsyncKeyState(VkLButton) & 0x8000) != 0;
        if (!lButtonDown)
        {
            _dragActive = false;
            _pressStartedInView = false;
            ReleaseForcedModifiers();
            return;
        }

        if (!GetCursorPos(out var pt) || !IsOverRegisteredView(pt))
        {
            ReleaseForcedModifiers();
            return;
        }

        // 区内按下起的普通点选/框选不强制；区内起拖或外部拖入才强制
        var externalDrop = !_pressStartedInView;
        if (!_dragActive && !externalDrop)
        {
            ReleaseForcedModifiers();
            return;
        }

        if (_preferCopy)
        {
            // 用户按住 Shift 时仍允许移动
            if ((GetAsyncKeyState(VkShift) & 0x8000) != 0 && !_shiftForced)
            {
                ReleaseForcedCtrl();
                return;
            }

            ReleaseForcedShift();
            EnsureCtrlDown();
        }
        else
        {
            // 用户按住 Ctrl 时仍允许复制（排除我们自己按下的 Ctrl）
            if ((GetAsyncKeyState(VkControl) & 0x8000) != 0 && !_ctrlForced)
            {
                ReleaseForcedShift();
                return;
            }

            ReleaseForcedCtrl();
            EnsureShiftDown();
        }
    }

    private static bool IsOverRegisteredView(Point pt)
    {
        // 拖放时 WindowFromPoint 常落在拖影窗（别的进程），父链命不中；改按分区矩形判断
        foreach (var kv in Views)
        {
            if (!IsWindow(kv.Key))
            {
                Views.TryRemove(kv.Key, out _);
                continue;
            }
            if (!GetWindowRect(kv.Key, out var rc))
                continue;
            if (pt.X >= rc.Left && pt.X < rc.Right && pt.Y >= rc.Top && pt.Y < rc.Bottom)
                return true;
        }

        return false;
    }

    private static void EnsureShiftDown()
    {
        if (_shiftForced)
            return;
        SendKey(VkShift, keyUp: false);
        _shiftForced = true;
    }

    private static void EnsureCtrlDown()
    {
        if (_ctrlForced)
            return;
        SendKey(VkControl, keyUp: false);
        _ctrlForced = true;
    }

    private static void ReleaseForcedShift()
    {
        if (!_shiftForced)
            return;
        SendKey(VkShift, keyUp: true);
        _shiftForced = false;
    }

    private static void ReleaseForcedCtrl()
    {
        if (!_ctrlForced)
            return;
        SendKey(VkControl, keyUp: true);
        _ctrlForced = false;
    }

    private static void ReleaseForcedModifiers()
    {
        ReleaseForcedShift();
        ReleaseForcedCtrl();
    }

    private static void SendKey(int vk, bool keyUp)
    {
        var input = new Input
        {
            Type = 1, // INPUT_KEYBOARD
            U = new InputUnion
            {
                Ki = new KeybdInput
                {
                    WVk = (ushort)vk,
                    DwFlags = keyUp ? 0x0002u : 0u // KEYEVENTF_KEYUP
                }
            }
        };
        _ = SendInput(1, [input], Marshal.SizeOf<Input>());
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeybdInput Ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
