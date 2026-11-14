using System.Windows;
using System.Windows.Threading;

namespace DesktopBox.Services;

/// <summary>前台窗口变化时立刻回调，避免围栏盖住其它应用。</summary>
internal sealed class ForegroundWatcher : IDisposable
{
    public const uint EventSystemForeground = 0x0003;
    public const uint WineventOutofcontext = 0x0000;

    private readonly Window _window;
    private readonly Action _onChanged;
    private readonly NativeMethods.WinEventDelegate _callback;
    private IntPtr _hook = IntPtr.Zero;

    public ForegroundWatcher(Window window, Action onChanged)
    {
        _window = window;
        _onChanged = onChanged;
        _callback = OnWinEvent;
    }

    public void Start()
    {
        _hook = NativeMethods.SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            _callback,
            0,
            0,
            WineventOutofcontext);
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        // 本窗自己获焦时不必立刻纠正；用 Background 合并连发的前台切换
        if (hwnd != IntPtr.Zero)
        {
            var self = new System.Windows.Interop.WindowInteropHelper(_window).Handle;
            if (self != IntPtr.Zero && hwnd == self)
                return;
        }

        _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, _onChanged);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
