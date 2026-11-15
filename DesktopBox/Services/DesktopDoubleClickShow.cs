using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace DesktopBox.Services;

/// <summary>
/// 围栏隐藏时：在桌面空白处双击左键 → 回调显示围栏。
/// </summary>
internal sealed class DesktopDoubleClickShow : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmLbuttonDown = 0x0201;

    private readonly Action _onShow;
    private readonly Dispatcher _dispatcher;
    private readonly LowLevelMouseProc _proc;
    private IntPtr _hook = IntPtr.Zero;
    private long _lastDownTick;
    private int _lastX;
    private int _lastY;

    public DesktopDoubleClickShow(Action onShow)
    {
        _onShow = onShow;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _proc = HookCallback;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;

        var hMod = Marshal.GetHINSTANCE(typeof(DesktopDoubleClickShow).Module);
        if (hMod == IntPtr.Zero)
            hMod = GetModuleHandle("DesktopBox.dll");

        _hook = SetWindowsHookEx(WhMouseLl, _proc, hMod, 0);
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _lastDownTick = 0;
    }

    public void Dispose() => Stop();

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WmLbuttonDown)
        {
            try
            {
                var info = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                if (IsDesktopPoint(info.Pt.X, info.Pt.Y) && IsSecondClick(info.Pt.X, info.Pt.Y))
                {
                    _lastDownTick = 0;
                    _dispatcher.BeginInvoke(DispatcherPriority.Input, _onShow);
                }
            }
            catch
            {
                // 钩子里绝不抛
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private bool IsSecondClick(int x, int y)
    {
        var now = Environment.TickCount64;
        var limit = GetDoubleClickTime();
        var dx = Math.Abs(x - _lastX);
        var dy = Math.Abs(y - _lastY);
        var cx = Math.Max(4, GetSystemMetrics(36)); // SM_CXDOUBLECLK
        var cy = Math.Max(4, GetSystemMetrics(37)); // SM_CYDOUBLECLK

        var ok = _lastDownTick > 0
                 && now - _lastDownTick <= limit
                 && dx <= cx
                 && dy <= cy;

        _lastDownTick = now;
        _lastX = x;
        _lastY = y;
        return ok;
    }

    private static bool IsDesktopPoint(int x, int y)
    {
        var hwnd = WindowFromPoint(new Point { X = x, Y = y });
        if (hwnd == IntPtr.Zero) return false;

        // 点在桌面图标列表或 DefView / WorkerW / Progman 上
        for (var i = 0; i < 6 && hwnd != IntPtr.Zero; i++)
        {
            var name = GetClass(hwnd);
            if (string.Equals(name, "SysListView32", StringComparison.Ordinal)
                || string.Equals(name, "SHELLDLL_DefView", StringComparison.Ordinal)
                || string.Equals(name, "WorkerW", StringComparison.Ordinal)
                || string.Equals(name, "Progman", StringComparison.Ordinal))
            {
                // SysListView32 必须挂在桌面 DefView 下，避免误伤普通资源管理器
                if (string.Equals(name, "SysListView32", StringComparison.Ordinal))
                    return HasDesktopDefViewAncestor(hwnd);
                return true;
            }

            hwnd = GetParent(hwnd);
        }

        return false;
    }

    private static bool HasDesktopDefViewAncestor(IntPtr hwnd)
    {
        for (var i = 0; i < 6 && hwnd != IntPtr.Zero; i++)
        {
            hwnd = GetParent(hwnd);
            if (hwnd == IntPtr.Zero) break;
            var name = GetClass(hwnd);
            if (string.Equals(name, "SHELLDLL_DefView", StringComparison.Ordinal))
                return true;
            if (string.Equals(name, "CabinetWClass", StringComparison.Ordinal)
                || string.Equals(name, "ExploreWClass", StringComparison.Ordinal))
                return false;
        }

        return false;
    }

    private static string GetClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        _ = GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public Point Pt;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
