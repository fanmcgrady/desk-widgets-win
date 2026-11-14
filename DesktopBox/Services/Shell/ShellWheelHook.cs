using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DesktopBox.Services;

namespace DesktopBox.Services.Shell;

/// <summary>
/// 低级鼠标钩子：悬停在围栏区上时强制滚动 ListView。
/// </summary>
internal static class ShellWheelHook
{
    private const int WhMouseLl = 14;
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseHWheel = 0x020E;
    private const int SpiGetWheelScrollLines = 0x0068;
    private const uint LvmScroll = 0x1014;
    private const uint LvmGetItemRect = 0x100E;
    private const int LvirBounds = 0;
    private const int WmVscroll = 0x0115;
    private const int WmHscroll = 0x0114;
    private const int SbLineUp = 0;
    private const int SbLineDown = 1;
    private const int SbLineLeft = 0;
    private const int SbLineRight = 1;

    private static readonly LowLevelMouseProc Proc = HookCallback;
    private static IntPtr _hook = IntPtr.Zero;
    private static int _logBudget = 20;

    public static void Install()
    {
        if (_hook != IntPtr.Zero)
            return;

        var hMod = Marshal.GetHINSTANCE(typeof(ShellWheelHook).Module);
        if (hMod == IntPtr.Zero)
            hMod = GetModuleHandle("DesktopBox.dll");
        if (hMod == IntPtr.Zero)
        {
            using var process = Process.GetCurrentProcess();
            using var module = process.MainModule;
            hMod = GetModuleHandle(module?.ModuleName);
        }

        _hook = SetWindowsHookEx(WhMouseLl, Proc, hMod, 0);
        var err = Marshal.GetLastWin32Error();
        Log(_hook == IntPtr.Zero
            ? $"wheel hook install FAILED err={err} hMod={hMod}"
            : $"wheel hook install ok hMod={hMod}");
        _logBudget = 20;
    }

    public static void Uninstall()
    {
        if (_hook == IntPtr.Zero)
            return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WmMouseWheel || wParam == (IntPtr)WmMouseHWheel))
        {
            var info = Marshal.PtrToStructure<MsllHookStruct>(lParam);
            // 严格命中：禁止矩形回退，避免盖在围栏上的浏览器/编辑器滚轮被吞
            var host = ZoneDragMoveForce.HostFromScreenPointStrict(info.Pt.X, info.Pt.Y);
            if (host is not null)
            {
                var delta = (short)((info.MouseData >> 16) & 0xFFFF);
                var horizontal = wParam == (IntPtr)WmMouseHWheel;
                var lv = host.GetListViewHwndPublic();
                if (_logBudget > 0)
                {
                    _logBudget--;
                    Log($"wheel hit host lv=0x{lv.ToInt64():X} delta={delta} at {info.Pt.X},{info.Pt.Y}");
                }

                if (lv != IntPtr.Zero)
                    ScrollListView(lv, delta, horizontal, info.Pt.X, info.Pt.Y);

                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    internal static int GetWheelScrollLines()
    {
        uint lines = 3;
        if (!SystemParametersInfo(SpiGetWheelScrollLines, 0, ref lines, 0) || lines == 0)
            return 3;
        if (lines >= 0xFFFFFFFF / 2)
            return 3;
        return (int)lines;
    }

    internal static int MeasureListItemHeight(IntPtr listView)
    {
        var rc = new Rect { Left = LvirBounds };
        if (SendMessageRect(listView, LvmGetItemRect, IntPtr.Zero, ref rc) != IntPtr.Zero)
        {
            var h = rc.Bottom - rc.Top;
            if (h > 0 && h < 200)
                return h;
        }

        return 22;
    }

    /// <summary>
    /// 只发 LVM_SCROLL / *_SCROLL，绝不 SetFocus。
    /// 以前为让滚轮生效去抢焦点，会导致移出围栏后滚轮仍送给区内列表、外面失效。
    /// </summary>
    internal static void ScrollListView(IntPtr listView, int wheelDelta, bool horizontal, int screenX = 0, int screenY = 0)
    {
        _ = screenX;
        _ = screenY;
        if (listView == IntPtr.Zero)
            return;

        var lines = GetWheelScrollLines();
        var notches = wheelDelta / 120;
        if (notches == 0)
            notches = wheelDelta > 0 ? 1 : -1;

        if (horizontal)
        {
            SendMessage(listView, LvmScroll, (IntPtr)(-notches * 40), IntPtr.Zero);
            return;
        }

        var itemHeight = MeasureListItemHeight(listView);
        var dy = -notches * lines * itemHeight;
        if (dy == 0)
            dy = notches > 0 ? -itemHeight : itemHeight;
        SendMessage(listView, LvmScroll, IntPtr.Zero, (IntPtr)dy);
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.ConfigDirectory);
            File.AppendAllText(
                Path.Combine(AppConfig.ConfigDirectory, "debug.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
        }
        catch
        {
            // ignore
        }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(int uiAction, uint uiParam, ref uint pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessageRect(IntPtr hWnd, uint msg, IntPtr wParam, ref Rect lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
