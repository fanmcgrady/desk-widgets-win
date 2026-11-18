using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopBox.Services;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace DesktopBox;

/// <summary>
/// 底边系统 AppBar：状态 + 剪切板。占工作区底边，其它窗口不会画到条下面；
/// 不挤顶边，关窗按钮仍在屏幕右上角。
/// </summary>
public partial class StatsAppBarWindow : Window
{
    public const double BarHeightDip = 28;

    private static readonly SolidColorBrush WifiBarGreen = CreateFrozenBrush(0x10, 0x7C, 0x10);
    private static readonly SolidColorBrush WifiBarOrange = CreateFrozenBrush(0xF7, 0x63, 0x0C);
    private static readonly SolidColorBrush WifiBarRed = CreateFrozenBrush(0xE8, 0x11, 0x23);
    private static readonly SolidColorBrush WifiBarGray = CreateFrozenBrush(0xA0, 0xA0, 0xA0);
    private static readonly SolidColorBrush WifiTextMuted = CreateFrozenBrush(0x99, 0x99, 0x99);
    private static readonly SolidColorBrush CursorBarOrange = CreateFrozenBrush(0xF7, 0x63, 0x0C);
    private static readonly SolidColorBrush CursorBarRed = CreateFrozenBrush(0xE8, 0x11, 0x23);
    private static readonly SolidColorBrush CursorBarBlue = CreateFrozenBrush(0x00, 0x78, 0xD4);
    private static readonly SolidColorBrush NoticeFlashFg = CreateFrozenBrush(0x1B, 0x5E, 0x20);
    private static readonly SolidColorBrush NoticeBadgeGreen = CreateFrozenBrush(0x10, 0x7C, 0x10);
    private static readonly System.Windows.Media.Color NoticeFlashBg = System.Windows.Media.Color.FromRgb(0xE8, 0xF5, 0xE9);

    private readonly SystemStatsService _stats = new();
    private readonly ClipboardWatcher _clipboard = new();
    private Storyboard? _noticeStory;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private uint _callbackMsg;
    private bool _registered;
    private bool _closing;

    public StatsAppBarWindow()
    {
        InitializeComponent();
        Height = BarHeightDip;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _stats.Sampled += OnStatsSampled;
        _clipboard.Changed += OnClipboardChanged;
    }

    public void Start()
    {
        if (!IsVisible)
            Show();
        RegisterOrReposition();
        _stats.Start();
        AttachClipboard();
    }

    public void Pause()
    {
        _stats.Stop();
        _clipboard.Detach();
        UnregisterAppBar();
        Hide();
    }

    public void Resume()
    {
        Show();
        RegisterOrReposition();
        _stats.Start();
        AttachClipboard();
    }

    public void RefreshPosition()
    {
        if (_closing || !IsVisible)
            return;
        RegisterOrReposition();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
        ApplyToolWindowStyle();
        RegisterOrReposition();
        _stats.Start();
        AttachClipboard();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closing = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _stats.Sampled -= OnStatsSampled;
        _clipboard.Changed -= OnClipboardChanged;
        _noticeStory?.Stop();
        _stats.Dispose();
        _clipboard.Dispose();
        UnregisterAppBar();
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }

    private void AttachClipboard()
    {
        if (_hwnd == IntPtr.Zero || _closing)
            return;
        _clipboard.Attach(_hwnd);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_closing || !IsVisible)
            return;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, RegisterOrReposition);
    }

    private void ApplyToolWindowStyle()
    {
        if (_hwnd == IntPtr.Zero)
            return;
        var ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GwlExstyle);
        ex |= NativeMethods.WsExToolwindow | NativeMethods.WsExNoactivate;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GwlExstyle, ex);
    }

    private void RegisterOrReposition()
    {
        if (_hwnd == IntPtr.Zero || _closing)
            return;

        var screen = WinForms.Screen.PrimaryScreen;
        if (screen is null)
            return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var barPx = (int)Math.Round(BarHeightDip * dpi.PixelsPerDip);
        if (barPx < 24)
            barPx = 24;

        if (!_registered)
        {
            if (_callbackMsg == 0)
                _callbackMsg = AppBarNative.RegisterWindowMessage("DesktopBox.StatsAppBar");

            var dataNew = NewData();
            dataNew.UCallbackMessage = _callbackMsg;
            AppBarNative.SHAppBarMessage(AppBarNative.AbmNew, ref dataNew);
            _registered = true;

            var state = NewData();
            state.LParam = new IntPtr(AppBarNative.AbsAlwaysOnTop);
            AppBarNative.SHAppBarMessage(AppBarNative.AbmSetState, ref state);
        }

        var bounds = screen.Bounds;
        // 先贴底边整条；QUERYPOS 会避开任务栏等已有 AppBar
        var data = NewData();
        data.UEdge = AppBarNative.AbeBottom;
        data.Rc = new AppBarNative.Rect
        {
            Left = bounds.Left,
            Top = bounds.Bottom - barPx,
            Right = bounds.Right,
            Bottom = bounds.Bottom
        };

        AppBarNative.SHAppBarMessage(AppBarNative.AbmQueryPos, ref data);
        // 保持预定高度，贴在查询结果的底边之上
        data.Rc.Top = data.Rc.Bottom - barPx;
        AppBarNative.SHAppBarMessage(AppBarNative.AbmSetPos, ref data);

        Left = data.Rc.Left / dpi.DpiScaleX;
        Top = data.Rc.Top / dpi.DpiScaleY;
        Width = (data.Rc.Right - data.Rc.Left) / dpi.DpiScaleX;
        Height = (data.Rc.Bottom - data.Rc.Top) / dpi.DpiScaleY;

        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HwndTopmost,
            data.Rc.Left,
            data.Rc.Top,
            data.Rc.Right - data.Rc.Left,
            data.Rc.Bottom - data.Rc.Top,
            NativeMethods.SwpNoactivate | NativeMethods.SwpShowwindow);

        var act = NewData();
        AppBarNative.SHAppBarMessage(AppBarNative.AbmActivate, ref act);
        AppBarNative.SHAppBarMessage(AppBarNative.AbmWindowPosChanged, ref act);
    }

    private void UnregisterAppBar()
    {
        if (!_registered || _hwnd == IntPtr.Zero)
            return;
        var data = NewData();
        AppBarNative.SHAppBarMessage(AppBarNative.AbmRemove, ref data);
        _registered = false;
    }

    private AppBarNative.AppBarData NewData() => new()
    {
        CbSize = Marshal.SizeOf<AppBarNative.AppBarData>(),
        Hwnd = _hwnd,
        UCallbackMessage = _callbackMsg,
        UEdge = AppBarNative.AbeBottom
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == ClipboardWatcher.WmClipboardUpdate)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _clipboard.HandleUpdate());
            handled = true;
            return IntPtr.Zero;
        }

        if (_callbackMsg != 0 && unchecked((uint)msg) == _callbackMsg)
        {
            switch (wParam.ToInt32())
            {
                case AppBarNative.AbnPosChanged:
                case AppBarNative.AbnStateChange:
                    RegisterOrReposition();
                    break;
            }
            handled = true;
        }

        const int WmActivate = 0x0006;
        const int WmWindowPosChanged = 0x0047;
        if ((msg == WmActivate || msg == WmWindowPosChanged) && _registered)
        {
            var data = NewData();
            AppBarNative.SHAppBarMessage(AppBarNative.AbmActivate, ref data);
            AppBarNative.SHAppBarMessage(AppBarNative.AbmWindowPosChanged, ref data);
        }

        return IntPtr.Zero;
    }

    private void OnClipboardChanged(object? sender, ClipboardNotice notice)
    {
        var preview = notice.Preview?.Trim() ?? "";
        NoticeBadge.Visibility = Visibility.Visible;
        NoticeBadge.Background = NoticeBadgeGreen;
        NoticeBadgeText.Text = "已复制";
        NoticeText.Text = preview;
        NoticeText.ToolTip = string.IsNullOrEmpty(preview) ? "已复制" : preview;
        NoticeText.Foreground = NoticeFlashFg;
        PlayCopyTip();
    }

    /// <summary>淡入 → 保留约 10s → 淡出清空。</summary>
    private void PlayCopyTip()
    {
        _noticeStory?.Stop();
        NoticePanel.Background = new SolidColorBrush(NoticeFlashBg);
        NoticeContent.Opacity = 0;
        NoticeSlide.X = 0;

        var sb = new Storyboard();

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fadeIn, NoticeContent);
        Storyboard.SetTargetProperty(fadeIn, new PropertyPath(UIElement.OpacityProperty));
        sb.Children.Add(fadeIn);

        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280))
        {
            BeginTime = TimeSpan.FromSeconds(10),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(fadeOut, NoticeContent);
        Storyboard.SetTargetProperty(fadeOut, new PropertyPath(UIElement.OpacityProperty));
        sb.Children.Add(fadeOut);

        sb.Completed += (_, _) => ClearNotice();
        _noticeStory = sb;
        sb.Begin();
    }

    private void ClearNotice()
    {
        NoticeBadge.Visibility = Visibility.Collapsed;
        NoticeText.Text = "";
        NoticeText.ToolTip = null;
        NoticeContent.Opacity = 0;
        NoticePanel.Background = System.Windows.Media.Brushes.Transparent;
    }

    private void OnStatsSampled(object? sender, SystemStatsSample sample)
    {
        ApplyCursorUsageUi(sample.Cursor);
        StatsUptime.Text = SystemStatsService.FormatUptime(sample.Uptime);
        ApplyWifiUi(sample.Wifi);
    }

    private void ApplyCursorUsageUi(CursorUsageInfo usage)
    {
        var track = Math.Max(1, StatsCursorTrack.Width);
        if (usage.State == CursorUsageState.Ok && usage.PercentUsed >= 0)
        {
            var used = Math.Clamp(usage.PercentUsed, 0, 100);
            StatsCursor.Text = $"{used:0}%";
            StatsCursorHint.Text = string.IsNullOrWhiteSpace(usage.ResetHint)
                ? "已用"
                : usage.ResetHint!;
            StatsCursorBar.Background = used >= 90 ? CursorBarRed
                : used >= 70 ? CursorBarOrange
                : CursorBarBlue;
            StatsCursorBar.Width = track * (used / 100.0);
            return;
        }

        StatsCursorBar.Width = 0;
        StatsCursorBar.Background = CursorBarBlue;
        if (usage.State == CursorUsageState.Unavailable)
        {
            StatsCursor.Text = "未登录";
            StatsCursorHint.Text = "WSL agent login";
        }
        else
        {
            StatsCursor.Text = "…";
            StatsCursorHint.Text = "读取中";
        }
    }

    private void ApplyWifiUi(WifiInfo wifi)
    {
        var track = Math.Max(1, StatsWifiTrack.Width);
        if (!wifi.HasAdapterOrLink)
        {
            StatsWifi.Text = "无网卡";
            StatsWifi.Foreground = WifiTextMuted;
            StatsWifiSsid.Text = "";
            StatsWifiBar.Width = 0;
            StatsWifiBar.Background = WifiBarGray;
            return;
        }

        if (!wifi.Connected)
        {
            StatsWifi.Text = "未连接";
            StatsWifi.Foreground = WifiTextMuted;
            StatsWifiSsid.Text = "";
            StatsWifiBar.Width = 0;
            StatsWifiBar.Background = WifiBarGray;
            return;
        }

        var q = wifi.SignalPercent >= 0 ? wifi.SignalPercent : 50;
        string level;
        SolidColorBrush color;
        if (q >= 70)
        {
            level = "强";
            color = WifiBarGreen;
        }
        else if (q >= 40)
        {
            level = "中";
            color = WifiBarOrange;
        }
        else
        {
            level = "弱";
            color = WifiBarRed;
        }

        StatsWifi.Text = level;
        StatsWifi.Foreground = color;
        StatsWifiSsid.Text = string.IsNullOrWhiteSpace(wifi.Ssid) ? "" : wifi.Ssid;
        StatsWifiBar.Background = color;
        StatsWifiBar.Width = track * Math.Clamp(q / 100.0, 0.08, 1);
    }

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
