using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopBox.Services.Shell;

/// <summary>
/// 主窗 HwndSource 钩子：标题栏等 WPF 区域接收侧键；滚轮在非原生子窗上时转给对应区。
/// </summary>
internal sealed class ShellMouseRouting : IDisposable
{
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseHWheel = 0x020E;
    private const int WmXButtonDown = 0x020B;
    private const int WmAppCommand = 0x0319;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmChar = 0x0102;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int XButton1 = 0x0001;
    private const int XButton2 = 0x0002;
    private const int AppCommandBrowserBackward = 1;
    private const int AppCommandBrowserForward = 2;

    private readonly Window _window;
    private readonly Func<System.Windows.Point, ZoneView?> _zoneFromPoint;
    private HwndSource? _source;
    private bool _disposed;

    public ShellMouseRouting(Window window, Func<System.Windows.Point, ZoneView?> zoneFromPoint)
    {
        _window = window;
        _zoneFromPoint = zoneFromPoint;
    }

    public void Attach()
    {
        _source = PresentationSource.FromVisual(_window) as HwndSource;
        _source?.AddHook(Hook);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _source?.RemoveHook(Hook);
        _source = null;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 焦点在围栏 WPF 层（标题栏等）时，把键盘转给最近活动的 Shell 区
        if (msg is WmKeyDown or WmKeyUp or WmChar or WmSysKeyDown or WmSysKeyUp)
        {
            var host = ExplorerBrowserHost.LastActive
                       ?? ZoneDragMoveForce.HostFromCursor();
            if (host is null)
                ZoneUnderCursor()?.TryGetShellHost(out host);
            if (host is not null && host.TryTranslateAccelerator(hwnd, msg, wParam, lParam))
            {
                handled = true;
                return IntPtr.Zero;
            }
        }

        if (msg is WmMouseWheel or WmMouseHWheel)
        {
            // 光标已在 Shell 原生子窗上：由 ZoneDragMoveForce 子类化处理
            if (ZoneDragMoveForce.HostFromCursor() is not null)
                return IntPtr.Zero;

            var zone = ZoneUnderCursor();
            if (zone is not null)
            {
                zone.ForwardMouseWheel(msg, wParam, lParam);
                handled = true;
            }

            return IntPtr.Zero;
        }

        if (msg == WmXButtonDown)
        {
            var button = ((int)wParam >> 16) & 0xffff;
            if (TryNavigateSideButton(button))
            {
                handled = true;
                return IntPtr.Zero;
            }
        }

        if (msg == WmAppCommand)
        {
            var cmd = ((int)lParam >> 16) & 0x0FFF;
            if (cmd == AppCommandBrowserBackward && TryNavigateSideButton(XButton1))
            {
                handled = true;
                return IntPtr.Zero;
            }

            if (cmd == AppCommandBrowserForward && TryNavigateSideButton(XButton2))
            {
                handled = true;
                return IntPtr.Zero;
            }
        }

        return IntPtr.Zero;
    }

    private bool TryNavigateSideButton(int button)
    {
        ExplorerBrowserHost? host = ZoneDragMoveForce.HostFromCursor();
        if (host is null)
            ZoneUnderCursor()?.TryGetShellHost(out host);

        if (host is null)
            return false;

        if (button == XButton1)
        {
            host.GoBack();
            return true;
        }

        if (button == XButton2)
        {
            host.GoForward();
            return true;
        }

        return false;
    }

    private ZoneView? ZoneUnderCursor()
    {
        if (!GetCursorPos(out var pt))
            return null;
        try
        {
            var local = _window.PointFromScreen(new System.Windows.Point(pt.X, pt.Y));
            return _zoneFromPoint(local);
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
