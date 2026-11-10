using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeskWidgets.Services;
using Markdig;
using DrawingColor = System.Drawing.Color;
using DrawingIcon = System.Drawing.Icon;
using DrawingPoint = System.Drawing.Point;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using Microsoft.Win32;
using WpfButton = System.Windows.Controls.Button;
using WpfCursors = System.Windows.Input.Cursors;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WinForms = System.Windows.Forms;

namespace DeskWidgets;

public partial class MainWindow : Window
{
    /// <summary>笔记、待办、邮件列表、大模型、文件的窄栏宽度；仅网安满屏。</summary>
    private const double PanelWidthDip = 320;
    /// <summary>邮件详情 / 写信栏宽；网安满屏时也作下限参考。</summary>
    private const double PanelWidthMailDip = 560;
    private const int EdgeHitDip = 12;
    /// <summary>贴边需停留多久才展开，减少路过/滚轮时误触。</summary>
    private const int EdgeExpandDwellMs = 180;

    private enum TabMode { Note, Todo, Mail, Agent, Cyber, File }
    private enum CyberListMode { Hot, Fav, Sources }

    private readonly Store _store = new();
    private readonly NoteFiles _notes = new();
    private readonly MsToDoSync _sync;
    private readonly ImapSmtpMailService _mail = new();
    private readonly CursorAgentClient _agent = new();
    private readonly CyberNewsService _cyberNews = new();
    private CyberNewsItem? _cyberNewsOpen;
    private bool _cyberNewsLoading;
    private bool _cyberWebReady;
    private bool _cyberPrefetchWebReady;
    private bool _cyberWebNavHooked;
    private bool _cyberPrefetchNavHooked;
    /// <summary>true 时 CyberNewsWebPrefetch 为前台，CyberNewsWeb 为预载。</summary>
    private bool _cyberWebFlipped;
    /// <summary>详情导航代数：连按切换时只认最新一次。</summary>
    private int _cyberWebNavEpoch;
    private string? _cyberWebTargetUrl;
    private string? _cyberPrefetchUrl;
    private int _cyberPrefetchEpoch;
    private bool _cyberPrefetchReady;
    private CancellationTokenSource? _cyberNavCts;
    private DateTime _cyberScrollAtUtc = DateTime.MinValue;
    private CyberListMode _cyberListMode = CyberListMode.Hot;
    private static readonly MarkdownPipeline AgentMdPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();
    private bool _agentHasSession;
    private bool _agentBusy;
    private bool _agentTimelineReady;
    private Task? _agentTimelineInitTask;
    private bool _agentWelcomeShown;
    private bool _agentStreaming;
    private string _agentStreamText = "";
    private CancellationTokenSource? _agentCts;
    private enum MailView { List, Read, Compose, Settings }
    private MailView _mailView = MailView.List;
    private MailMessageDetail? _mailCurrent;
    private bool _mailBusy;
    private bool _mailHtmlReady;
    private bool _mailHtmlHooksBound;
    private int _mailUnread;
    private List<MailMessageSummary> _mailCache = new();
    private DateTime _mailCacheAtUtc = DateTime.MinValue;
    private static readonly TimeSpan MailCacheTtl = TimeSpan.FromMinutes(3);
    private readonly DispatcherTimer _edgeTimer;
    private readonly DispatcherTimer _syncTimer;
    private readonly DispatcherTimer _mailPollTimer;
    private readonly DispatcherTimer _cyberPollTimer;
    private DispatcherTimer? _displayDebounce;
    private WinForms.NotifyIcon? _tray;
    private DrawingIcon? _trayIcon;
    private HwndSource? _hwndSource;
    private bool _keyboardHookInstalled;
    private IntPtr _keyboardHook = IntPtr.Zero;
    private LowLevelKeyboardProc? _keyboardProc; // 防 GC 回收
    private bool _mouseHookInstalled;
    private IntPtr _mouseHook = IntPtr.Zero;
    private LowLevelMouseProc? _mouseProc;
    private bool _ctrlHeld;
    private bool _ctrlChorded;
    private DateTime _ctrlDownUtc = DateTime.MinValue;
    private DateTime _lastCtrlTapUtc = DateTime.MinValue;
    private bool _altSpaceHeld;
    private bool _ctrlSpaceHeld;
    /// <summary>按下 Ctrl+空格 后的一小段时间内，壳层抢焦点要改把焦点放进大模型输入框。</summary>
    private DateTime _agentFocusRequestedUtc = DateTime.MinValue;
    private DispatcherTimer? _agentFocusTimer;
    private int _agentFocusTicks;
    private bool _agentFocusBusy;
    private bool _agentFocusHooked;
    private DateTime _fileFocusRequestedUtc = DateTime.MinValue;
    private DispatcherTimer? _fileFocusTimer;
    private int _fileFocusTicks;
    private bool _fileFocusBusy;

    // 全局唤醒：连按两下 Ctrl；消失：Esc；文件：Alt+空格；大模型：Ctrl+空格；展开时 Ctrl+1..6 用 RegisterHotKey
    private const int HotkeyIdTabBase = 0xD510; // +1..+6
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint WmHotkey = 0x0312;
    private const int WmWindowPosChanging = 0x0046;
    private const uint VkSpace = 0x20;
    private const uint LlKhfInjected = 0x10;
    private const uint LlKhfAltDown = 0x20;
    private bool _tabHotkeysRegistered;
    private uint _lastHookFlags;
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const uint MouseXButton1 = 1;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint VkControl = 0x11;
    private const uint VkLControl = 0xA2;
    private const uint VkRControl = 0xA3;
    private const uint VkMenu = 0x12; // Alt
    private const uint VkShift = 0x10;
    private const uint VkLShift = 0xA0;
    private const uint VkRShift = 0xA1;
    private const uint VkF1 = 0x70;
    private const uint VkEscape = 0x1B;
    private const uint VkLeft = 0x25;
    private const uint VkUp = 0x26;
    private const uint VkRight = 0x27;
    private const uint VkDown = 0x28;
    private const uint GaRoot = 2;
    /// <summary>单次 Ctrl 轻点最长按住时间。</summary>
    private static readonly TimeSpan EscTapMaxHold = TimeSpan.FromMilliseconds(400);
    /// <summary>两次 Ctrl 轻点间隔上限。</summary>
    private static readonly TimeSpan DoubleEscWindow = TimeSpan.FromMilliseconds(450);
    private FileSystemWatcher? _noteWatcher;
    private WinForms.Screen? _dockScreen;
    /// <summary>投屏/分辨率抖动期间禁止自动换屏，避免侧栏跳来跳去。</summary>
    private DateTime _dockLockUntilUtc = DateTime.MinValue;
    private int _lastPlaceX = int.MinValue;
    private int _lastPlaceY = int.MinValue;
    private int _lastPlaceW = int.MinValue;
    private int _lastPlaceH = int.MinValue;

    private bool _expanded;
    /// <summary>钉子固定：置顶且不因鼠标离开自动收起。</summary>
    private bool _pinned;
    private bool _allowClose;
    private DateTime? _leaveSince;
    /// <summary>贴左缘开始计时，满 dwell 才 Expand。</summary>
    private DateTime? _edgeHoverSince;
    /// <summary>托盘打开后：鼠标尚未进入侧栏前不自动收起，避免一点开就被 EdgeTimer 关掉。</summary>
    private bool _awaitPanelEnter;
    private int _year;
    private int _month;
    private DateTime _selected;
    private TabMode _tab = TabMode.Note;
    private bool _showDone;
    private bool _filterByDay;
    private bool _inputFocused;
    /// <summary>行内编辑等：Esc 先取消编辑。</summary>
    private Action? _inlineEscCancel;
    private bool _syncing;
    private string? _activeNoteId;
    private string? _editingTodoId; // 从待办「编辑」进入时，笔记区改的是这条待办
    private bool _noteLoading;
    private bool _noteWriting;
    private bool _notePreviewMode;
    private bool _notePreviewReady;
    private bool _notePreviewHooksBound;
    private DispatcherTimer? _noteSaveTimer;
    private DispatcherTimer? _noteReloadDebounce;
    // 待办拖拽排序
    private string? _todoDragId;
    private System.Windows.Point _todoDragStart;
    private bool _todoDragging;
    private bool _todoDragSuppressClick;
    private Border? _todoDragRow;
    private Border? _todoInsertLine;
    private int _todoInsertIndex = -1;
    private const double TodoDragThreshold = 6;

    public MainWindow()
    {
        InitializeComponent();

        _sync = new MsToDoSync(_store);
        var now = DateTime.Today;
        _year = now.Year;
        _month = now.Month;
        _selected = now;

        _edgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _edgeTimer.Tick += EdgeTimer_Tick;

        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _syncTimer.Tick += async (_, _) =>
        {
            if (_sync.HasClientId && !_syncing)
                await TrySyncSilentAsync();
        };

        // 邮件未读：约每 2 分钟静默查一次；进邮件页 / 手动刷新也会更新
        _mailPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _mailPollTimer.Tick += async (_, _) => await PollMailUnreadAsync(silent: true);

        // 网安热点：约每 15 分钟静默拉取；进页/手动刷新也会拉
        _cyberPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _cyberPollTimer.Tick += async (_, _) => await PollCyberNewsAsync(silent: true);

        _fileSearchTimer.Tick += async (_, _) =>
        {
            _fileSearchTimer.Stop();
            await RunFileSearchAsync();
        };

        Loaded += OnLoaded;
        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            Collapse();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetupTray();
        StyleTabs();
        UpdatePinButton();
        PaintShowDoneBox();
        RebuildCalendar();
        UpdateDueHint();
        UpdateTodoPlaceholder();
        UpdateSyncHint();
        EnsureActiveNote();
        ReloadNoteList();
        LoadActiveNote();
        UpdateNoteChrome();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _dockScreen = PickDefaultDockScreen();
        // 先装钩子/热键，再直接收起。托盘、左缘、双击 Ctrl 再展开。
        RegisterWakeHotkey();
        _expanded = false;
        PlaceCollapsed();
        SyncEdgeTimerInterval();
        Activated += (_, _) => { if (_expanded) RegisterTabHotkeys(); };
        Deactivated += (_, _) => UnregisterTabHotkeys();
        _edgeTimer.Start();
        _syncTimer.Start();
        _mailPollTimer.Start();
        _cyberPollTimer.Start();
        UpdateCyberBadge();

        Dispatcher.BeginInvoke(() =>
        {
            try { Autostart.ApplyPreferred(); } catch { /* ignore */ }
        }, DispatcherPriority.ContextIdle);

        StartupWatchWindow.ShowIfFreshBoot();

        var deferred = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        deferred.Tick += async (_, _) =>
        {
            deferred.Stop();
            _ = PollMailUnreadAsync(silent: true);
            await PollCyberNewsAsync(silent: true);
        };
        deferred.Start();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // 投屏时 DisplaySettingsChanged 会连打多次，合并成一次再重定位
        Dispatcher.BeginInvoke(() =>
        {
            _displayDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            _displayDebounce.Stop();
            _displayDebounce.Tick -= OnDisplayDebounceTick;
            _displayDebounce.Tick += OnDisplayDebounceTick;
            _displayDebounce.Start();
            // 抖动期内先锁停靠屏，EdgeTimer 不换屏、不因坐标乱跳而乱展开
            _dockLockUntilUtc = DateTime.UtcNow.AddSeconds(5);
        });
    }

    private void OnDisplayDebounceTick(object? sender, EventArgs e)
    {
        _displayDebounce?.Stop();
        _dockLockUntilUtc = DateTime.UtcNow.AddSeconds(3);

        // 始终回到主屏，不跟着扩展屏或投屏走
        _dockScreen = PickDefaultDockScreen();

        // 强制重放一次（忽略上次坐标缓存）
        _lastPlaceX = int.MinValue;
        if (_expanded) PlaceExpanded();
        else PlaceCollapsed();
    }

    private bool IsDockLocked() => DateTime.UtcNow < _dockLockUntilUtc;

    // ----- multi-monitor dock (physical pixels + MoveWindow) -----
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookEx", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExMouse(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct
    {
        public NativePoint Pt;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const uint KeyeventfKeyup = 0x0002;
    private const byte VkW = 0x57;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hwndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint lpPoint);

    private const uint WmMouseWheel = 0x020A;
    /// <summary>Windows 滚轮一格。Chromium 收到后按系统「一次滚动行数」换算，并做和鼠标相同的滑动。</summary>
    private const int WheelDeltaUnit = 120;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private static WinForms.Screen PickDefaultDockScreen()
    {
        // 始终停在 Windows 主屏幕右侧，不跟扩展屏
        foreach (var s in WinForms.Screen.AllScreens)
        {
            if (s.Primary) return s;
        }
        return WinForms.Screen.PrimaryScreen ?? WinForms.Screen.AllScreens[0];
    }

    private static WinForms.Screen? ResolveScreen(WinForms.Screen? screen)
    {
        if (screen == null) return null;
        foreach (var s in WinForms.Screen.AllScreens)
        {
            if (s.DeviceName == screen.DeviceName) return s;
        }
        return null;
    }

    /// <summary>WPF 当前把 DIP 换成像素的比例。还没有视觉树时退回调用方给的比例。</summary>
    private double WpfDeviceScale(double fallback)
    {
        var src = PresentationSource.FromVisual(this);
        var m = src?.CompositionTarget?.TransformToDevice.M11 ?? 0;
        return m > 0.01 ? m : fallback;
    }

    private static double DpiScale(WinForms.Screen screen)
    {
        var c = new NativePoint
        {
            X = screen.Bounds.Left + screen.Bounds.Width / 2,
            Y = screen.Bounds.Top + screen.Bounds.Height / 2,
        };
        var mon = MonitorFromPoint(c, 2); // MONITOR_DEFAULTTONEAREST
        if (mon != IntPtr.Zero && GetDpiForMonitor(mon, 0, out uint dpiX, out _) == 0 && dpiX > 0)
            return dpiX / 96.0;
        return 1.0;
    }

    private static WinForms.Screen? ScreenNearLeftEdge(DrawingPoint mouse)
    {
        foreach (var s in WinForms.Screen.AllScreens)
        {
            var scale = DpiScale(s);
            var hit = Math.Max(6, (int)Math.Ceiling(EdgeHitDip * scale));
            var wa = s.WorkingArea;
            if (mouse.X >= wa.Left - 2 && mouse.X <= wa.Left + hit
                && mouse.Y >= wa.Top && mouse.Y <= wa.Bottom)
                return s;
        }
        return null;
    }

    /// <summary>
    /// 贴边展开只在「桌面」时生效：前台是桌面/任务栏才允许。
    /// 其它窗口（浏览器、IDE 等）在前台时不贴边唤出；托盘 / 双击 Ctrl 仍可开。
    /// </summary>
    private bool ShouldSuppressEdgeExpand(WinForms.Screen screen)
    {
        _ = screen;
        EnsureHwnd();
        var self = new WindowInteropHelper(this).Handle;
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == self) return false;

        var cls = new System.Text.StringBuilder(64);
        if (GetClassName(fg, cls, cls.Capacity) <= 0) return true;
        var name = cls.ToString();
        // 只有桌面墙纸层 / 任务栏算「在桌面」
        return name is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    private void EnsureHwnd()
    {
        var helper = new WindowInteropHelper(this);
        if (helper.Handle == IntPtr.Zero)
            helper.EnsureHandle();
    }

    private double ActivePanelWidthDip => _tab switch
    {
        TabMode.Cyber => CyberReadingWidthDip(),
        // 邮件详情 / 写信略宽，列表与其它页签仍用窄栏
        TabMode.Mail when _mailView is MailView.Read or MailView.Compose => PanelWidthMailDip,
        _ => PanelWidthDip,
    };

    /// <summary>网安页签：从左缘向右铺满当前屏工作区。</summary>
    private double CyberReadingWidthDip()
    {
        var screen = ResolveScreen(_dockScreen)
                     ?? WinForms.Screen.PrimaryScreen
                     ?? PickDefaultDockScreen();
        return Math.Max(PanelWidthMailDip, screen.WorkingArea.Width / DpiScale(screen));
    }

    private double CapPanelWidthDip(WinForms.Screen screen, double dipW)
    {
        // 网安页签允许满屏；其它页签仍限制约 55%，避免占满笔记本
        if (_tab == TabMode.Cyber)
            return dipW;
        var maxDip = screen.WorkingArea.Width / DpiScale(screen) * 0.55;
        return dipW > maxDip ? Math.Max(PanelWidthDip, maxDip) : dipW;
    }

    private void PlaceOnScreen(WinForms.Screen screen, bool expanded)
    {
        _dockScreen = screen;
        EnsureHwnd();
        var hwnd = new WindowInteropHelper(this).Handle;
        var scale = DpiScale(screen);
        var wpfScale = WpfDeviceScale(scale);
        // 收起时只留 1 像素在主屏左缘内侧。
        var dipW = expanded ? CapPanelWidthDip(screen, ActivePanelWidthDip) : 1.0 / scale;
        var widthPx = expanded ? Math.Max(1, (int)Math.Round(dipW * scale)) : 1;
        var wa = screen.WorkingArea;
        var x = wa.Left;
        var y = wa.Top;
        var h = Math.Max(1, wa.Height);
        dipW = widthPx / wpfScale;

        // 坐标未变则跳过 MoveWindow，减少投屏时闪跳
        if (x == _lastPlaceX && y == _lastPlaceY && widthPx == _lastPlaceW && h == _lastPlaceH
            && Math.Abs(Width - dipW) < 0.5)
        {
            if (expanded) Show();
            else Hide();
            return;
        }

        _lastPlaceX = x;
        _lastPlaceY = y;
        _lastPlaceW = widthPx;
        _lastPlaceH = h;

        // 宽高按这块屏的像素来，再换成 WPF 正在用的比例，避免 200% 和 125% 叠在一起把窗口撑大
        // MaxWidth 锁死窄栏：邮件 HTML / WebView2 内容最小宽常把窗口撑到 560+
        Width = dipW;
        MaxWidth = expanded && _tab != TabMode.Cyber ? dipW : double.PositiveInfinity;
        Height = h / wpfScale;
        MoveWindow(hwnd, x, y, widthPx, h, true);
        if (expanded)
        {
            Show();
        }
        else
        {
            Hide();
        }
    }

    private void PlaceExpanded()
    {
        var prevW = _lastPlaceW;
        var screen = ResolveScreen(_dockScreen)
                     ?? WinForms.Screen.PrimaryScreen
                     ?? PickDefaultDockScreen();
        PlaceOnScreen(screen, expanded: true);
        // 网安满屏 → 窄页签：窗口骤缩后鼠标常落在新边界外，会被 EdgeTimer 当成离开而收起
        if (!_pinned && prevW > 0 && _lastPlaceW > 0 && _lastPlaceW < prevW - 40)
        {
            _awaitPanelEnter = true;
            _leaveSince = null;
        }
    }

    private void PlaceCollapsed()
    {
        var screen = ResolveScreen(_dockScreen)
                     ?? WinForms.Screen.PrimaryScreen
                     ?? PickDefaultDockScreen();
        PlaceOnScreen(screen, expanded: false);
    }

    private void Expand()
    {
        _expanded = true;
        _leaveSince = null;
        PlaceExpanded();
        Activate();
        Topmost = true;
        RegisterTabHotkeys();
        SyncEdgeTimerInterval();
        // 首次/每次展开都清输入焦点，保证 Ctrl+数字走热键而不是进 NoteEdit
        ScheduleShellFocusReclaim();
    }

    /// <summary>托盘 / 菜单唤起：展开后等鼠标进侧栏再启用自动收起。</summary>
    private void ExpandFromTray()
    {
        _awaitPanelEnter = true;
        _leaveSince = null;
        Expand();
    }

    /// <summary>连按两下 Ctrl：展开侧栏。已经展开则置顶。</summary>
    private void WakeFromHotkey()
    {
        _lastCtrlTapUtc = DateTime.MinValue;
        if (_expanded)
        {
            Activate();
            Topmost = true;
            ScheduleShellFocusReclaim();
            return;
        }
        ExpandFromTray();
        ScheduleShellFocusReclaim();
    }

    /// <summary>
    /// 把键盘焦点收回壳层（立刻 + Input + ContextIdle），盖过 NoteEdit/AgentInput/WebView 抢焦点。
    /// activate：打开网安详情后顺带 Activate，避免还要点一下窗体。
    /// </summary>
    private void ScheduleShellFocusReclaim(bool activate = false, Func<bool>? when = null)
    {
        void Once()
        {
            try
            {
                if (when != null && !when()) return;
                if (activate && _expanded) Activate();
                // 刚要求聚焦大模型或文件搜索时，不要把焦点收走
                if (ShouldKeepAgentInputFocus())
                {
                    FocusAgentInputCore();
                    return;
                }
                if (ShouldKeepFileInputFocus())
                {
                    FocusFileSearchCore();
                    return;
                }
                BlurInputFocus();
            }
            catch { /* ignore */ }
        }

        Once();
        _ = Dispatcher.BeginInvoke(Once, DispatcherPriority.Input);
        _ = Dispatcher.BeginInvoke(Once, DispatcherPriority.ContextIdle);
    }

    private void RegisterWakeHotkey()
    {
        EnsureHwnd();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);

        InstallKeyboardHook();
        InstallMouseHook();
        UpdateTrayHotkeyTip();
    }

    private void InstallKeyboardHook()
    {
        if (_keyboardHookInstalled) return;
        _keyboardProc = KeyboardHookCallback;
        using var proc = Process.GetCurrentProcess();
        using var mod = proc.MainModule;
        var hMod = GetModuleHandle(mod?.ModuleName);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, hMod, 0);
        _keyboardHookInstalled = _keyboardHook != IntPtr.Zero;
    }

    private void UninstallKeyboardHook()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        _keyboardProc = null;
        _keyboardHookInstalled = false;
        UninstallMouseHook();
        _ctrlHeld = false;
        _ctrlChorded = false;
        _lastCtrlTapUtc = DateTime.MinValue;
        _altSpaceHeld = false;
        _ctrlSpaceHeld = false;
    }

    private void InstallMouseHook()
    {
        if (_mouseHookInstalled) return;
        _mouseProc = MouseHookCallback;
        using var proc = Process.GetCurrentProcess();
        using var mod = proc.MainModule;
        var hMod = GetModuleHandle(mod?.ModuleName);
        _mouseHook = SetWindowsHookExMouse(WhMouseLl, _mouseProc, hMod, 0);
        _mouseHookInstalled = _mouseHook != IntPtr.Zero;
    }

    private void UninstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        _mouseProc = null;
        _mouseHookInstalled = false;
    }

    /// <summary>
    /// 鼠标侧键回退。WebView 会自己当成浏览器后退，所以在钩子里吞掉：
    /// 笔记页打开记事本；网安页下一篇；阅读邮件时返回列表。
    /// </summary>
    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = wParam.ToInt32();
            if (msg is WmXButtonDown or WmXButtonUp)
            {
                var info = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
                var button = (info.MouseData >> 16) & 0xFFFF;
                if (button == MouseXButton1 && _expanded && IsOurWindowForeground())
                {
                    if (_tab == TabMode.Note)
                    {
                        if (msg == WmXButtonDown)
                            _ = Dispatcher.BeginInvoke(() => BtnNotepad_Click(this, new RoutedEventArgs()));
                        return (IntPtr)1;
                    }
                    if (_tab == TabMode.Cyber)
                    {
                        if (msg == WmXButtonDown)
                            _ = Dispatcher.BeginInvoke(OpenCyberNewsNext, DispatcherPriority.Send);
                        return (IntPtr)1;
                    }
                    if (_tab == TabMode.Mail && _mailView == MailView.Read)
                    {
                        if (msg == WmXButtonDown)
                            _ = Dispatcher.BeginInvoke(() => BtnMailBack_Click(this, new RoutedEventArgs()));
                        return (IntPtr)1;
                    }
                }
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>前台是本窗或 WebView2 等子 HWND 时都算侧栏在前台（否则打开详情后快捷键全失效）。</summary>
    private bool IsOurWindowForeground()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return false;
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (fg == hwnd) return true;
        return GetAncestor(fg, GaRoot) == hwnd;
    }

    /// <summary>
    /// 键盘钩子约定：
    /// - 侧栏展开且本窗（含 WebView 子 HWND）在前台：Esc 收起 / Ctrl+1..6 / F1 / 网安←→↑↓空格 由钩子吞掉并处理
    ///   （PreviewKeyDown 仅作钩子未装上时的后备）
    /// - 全局：Ctrl 连按两下唤醒侧栏；Alt+空格打开文件；Ctrl+空格打开大模型
    /// </summary>
    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            _lastHookFlags = info.Flags;
            var msg = wParam.ToInt32();
            var down = msg is WmKeyDown or WmSysKeyDown;
            var up = msg is WmKeyUp or WmSysKeyUp;

            // Alt+空格是系统菜单键，必须吞掉。现用来打开文件页。
            // Ctrl+空格打开大模型，同样吞掉，避免输入框吃掉空格。
            if (info.VkCode == VkSpace && (info.Flags & LlKhfInjected) == 0)
            {
                if (down && IsAltSpaceChord())
                {
                    if (!_altSpaceHeld)
                    {
                        _altSpaceHeld = true;
                        Dispatcher.BeginInvoke(FocusFileSearchFromHotkey);
                    }
                    return (IntPtr)1;
                }
                if (up && _altSpaceHeld)
                {
                    _altSpaceHeld = false;
                    return (IntPtr)1;
                }
                if (down && IsCtrlSpaceChord())
                {
                    if (!_ctrlSpaceHeld)
                    {
                        _ctrlSpaceHeld = true;
                        if (_ctrlHeld) _ctrlChorded = true;
                        Dispatcher.BeginInvoke(FocusAgentInputFromHotkey);
                    }
                    return (IntPtr)1;
                }
                if (up && _ctrlSpaceHeld)
                {
                    _ctrlSpaceHeld = false;
                    return (IntPtr)1;
                }
            }

            var ourFg = _expanded && IsOurWindowForeground();
            var isShift = IsShiftVk(info.VkCode);

            if (down && ourFg && !isShift && TryHandleInAppHotkeyFromHook(info.VkCode))
            {
                if (_ctrlHeld)
                    _ctrlChorded = true;
                return (IntPtr)1;
            }

            // 文件页切到别的程序后，搜索框焦点还在，侧栏不会随鼠标离开收起。
            // 这时单独的 Esc 收起文件页，但不吞键，当前程序照常收到 Esc。
            if (down && (info.Flags & LlKhfInjected) == 0
                && _expanded && !ourFg && _tab == TabMode.File
                && info.VkCode == VkEscape
                && !IsAltDown() && !IsCtrlDown() && !IsShiftDown())
            {
                Dispatcher.BeginInvoke(TryHandleEscapeKey, DispatcherPriority.Send);
            }

            // 双击 Ctrl → 唤醒。不吞键。低级钩子给出的是左右 Ctrl。
            // 按住 Ctrl 再按别的键（复制、保存）不算连按。
            var sideCtrl = info.VkCode is VkLControl or VkRControl;
            if (sideCtrl && (info.Flags & LlKhfInjected) == 0)
            {
                if (down && !_ctrlHeld)
                {
                    var now = DateTime.UtcNow;
                    _ctrlHeld = true;
                    _ctrlChorded = false;
                    _ctrlDownUtc = now;
                    if (_lastCtrlTapUtc != DateTime.MinValue
                        && now - _lastCtrlTapUtc <= DoubleEscWindow)
                    {
                        _lastCtrlTapUtc = DateTime.MinValue;
                        Dispatcher.BeginInvoke(WakeFromHotkey);
                    }
                }
                else if (up && _ctrlHeld)
                {
                    _ctrlHeld = false;
                    var held = DateTime.UtcNow - _ctrlDownUtc;
                    _lastCtrlTapUtc = !_ctrlChorded && held <= EscTapMaxHold
                        ? DateTime.UtcNow
                        : DateTime.MinValue;
                    _ctrlChorded = false;
                }
            }
            else if (down && _ctrlHeld && (info.Flags & LlKhfInjected) == 0 && !IsCtrlVk(info.VkCode))
            {
                _ctrlChorded = true;
                _lastCtrlTapUtc = DateTime.MinValue;
            }
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private static bool IsCtrlVk(uint vk) =>
        vk is VkControl or VkLControl or VkRControl;

    /// <summary>Alt 按下、Ctrl/Shift 都没按下。左右 Alt 都算。</summary>
    private static bool IsAltSpaceChord()
    {
        if (!IsAltDown()) return false;
        if (IsCtrlDown()) return false;
        if (IsShiftDown()) return false;
        return true;
    }

    /// <summary>Ctrl 按下、Alt/Shift 都没按下。</summary>
    private static bool IsCtrlSpaceChord()
    {
        if (!IsCtrlDown()) return false;
        if (IsAltDown()) return false;
        if (IsShiftDown()) return false;
        return true;
    }

    private static bool IsShiftVk(uint vk) =>
        vk is VkShift or VkLShift or VkRShift;

    private static bool IsAltDown() =>
        (GetAsyncKeyState((int)VkMenu) & 0x8000) != 0
        || (GetAsyncKeyState(0xA4) & 0x8000) != 0
        || (GetAsyncKeyState(0xA5) & 0x8000) != 0;

    private static bool IsCtrlDown() =>
        (GetAsyncKeyState((int)VkControl) & 0x8000) != 0;

    private static bool IsShiftDown() =>
        (GetAsyncKeyState((int)VkShift) & 0x8000) != 0;

    /// <summary>钩子层：Esc / F1 / 网安←→↑↓空格；Ctrl+1..6 在热键未注册时作后备。</summary>
    private bool TryHandleInAppHotkeyFromHook(uint vk)
    {
        var altDown = (GetAsyncKeyState((int)VkMenu) & 0x8000) != 0
                      || (_lastHookFlags & LlKhfAltDown) != 0;
        var ctrlDown = (GetAsyncKeyState((int)VkControl) & 0x8000) != 0;
        var shiftDown = (GetAsyncKeyState((int)VkShift) & 0x8000) != 0;

        if (vk == VkF1)
        {
            _ = Dispatcher.BeginInvoke(() => BtnPin_Click(this, new RoutedEventArgs()));
            return true;
        }

        if (!altDown && !ctrlDown && !shiftDown && vk == VkEscape)
        {
            _ = Dispatcher.BeginInvoke(TryHandleEscapeKey, DispatcherPriority.Send);
            return true;
        }

        // 不看 _inputFocused：WebView 不会置该标志
        // 网安：→ 下一条，← 刷新；详情 ↓/空格 下翻，↑/Shift+空格 上翻
        if (!altDown && !ctrlDown && _tab == TabMode.Cyber)
        {
            if (!shiftDown && vk == VkRight)
            {
                _ = Dispatcher.BeginInvoke(OpenCyberNewsNext, DispatcherPriority.Send);
                return true;
            }
            if (!shiftDown && vk == VkLeft)
            {
                _ = Dispatcher.BeginInvoke(RefreshCyberNewsFromHotkey, DispatcherPriority.Send);
                return true;
            }
            if (_cyberNewsOpen != null)
            {
                if (!shiftDown && (vk == VkDown || vk == VkUp))
                {
                    var forward = vk == VkDown;
                    _ = Dispatcher.BeginInvoke(() => ScrollCyberNewsPage(forward), DispatcherPriority.Send);
                    return true;
                }
                if (vk == VkSpace)
                {
                    var forward = !shiftDown; // 空格下翻，Shift+空格上翻
                    _ = Dispatcher.BeginInvoke(() => ScrollCyberNewsPage(forward), DispatcherPriority.Send);
                    return true;
                }
            }
        }

        // Ctrl+1..6：优先 RegisterHotKey（展开且前台）；未注册时钩子兜底。小键盘热键未登记，仍走钩子。
        if (!ctrlDown || altDown || shiftDown) return false;
        var numpad = vk is 0x61 or 0x62 or 0x63 or 0x64 or 0x65 or 0x66;
        if (_tabHotkeysRegistered && !numpad) return false;
        var digit = vk switch
        {
            0x31 or 0x61 => 1,
            0x32 or 0x62 => 2,
            0x33 or 0x63 => 3,
            0x34 or 0x64 => 4,
            0x35 or 0x65 => 5,
            0x36 or 0x66 => 6,
            _ => 0,
        };
        if (digit == 0) return false;
        _ = Dispatcher.BeginInvoke(() => TrySwitchTabByNumber(digit));
        return true;
    }

    /// <summary>Ctrl+1..6 / 钩子数字键共用。</summary>
    private bool TrySwitchTabByNumber(int digit) => digit switch
    {
        1 => InvokeTabClick(TabNote_Click),
        2 => InvokeTabClick(TabTodo_Click),
        3 => InvokeTabClick(TabMail_Click),
        4 => InvokeTabClick(TabCyber_Click),
        5 => InvokeTabClick(TabAgent_Click),
        6 => InvokeTabClick(TabFile_Click),
        _ => false,
    };

    private void UpdateTrayHotkeyTip()
    {
        if (_tray == null) return;
        var parts = new List<string> { "方老师的工作区" };
        if (_keyboardHookInstalled)
        {
            parts.Add("连按两下 Ctrl");
            parts.Add("Alt+空格 文件");
            parts.Add("Ctrl+空格 大模型");
        }
        _tray.Text = string.Join(" · ", parts);
    }

    /// <summary>全局：Ctrl+空格，展开侧栏并切到大模型输入框。</summary>
    private void FocusAgentInputFromHotkey()
    {
        _agentFocusRequestedUtc = DateTime.UtcNow;
        if (!_expanded) ExpandFromTray();
        else
        {
            Activate();
            Topmost = true;
        }
        InvokeTabClick(TabAgent_Click);
        FocusAgentInputSoon();
    }

    /// <summary>全局：Alt+空格，展开侧栏并切到文件搜索框。</summary>
    private void FocusFileSearchFromHotkey()
    {
        _fileFocusRequestedUtc = DateTime.UtcNow;
        if (!_expanded) ExpandFromTray();
        else
        {
            Activate();
            Topmost = true;
        }
        InvokeTabClick(TabFile_Click);
        FocusFileSearchSoon();
    }

    private bool ShouldKeepAgentInputFocus() =>
        _expanded
        && _tab == TabMode.Agent
        && DateTime.UtcNow - _agentFocusRequestedUtc < TimeSpan.FromSeconds(2);

    private bool ShouldKeepFileInputFocus() =>
        _expanded
        && _tab == TabMode.File
        && DateTime.UtcNow - _fileFocusRequestedUtc < TimeSpan.FromSeconds(2);

    /// <summary>
    /// Alt+空格从别的程序唤起时，Activate 拿不到前台；Alt 还按着时菜单态也会把焦点打掉。
    /// 所以跟大模型一样抢前台，并在松 Alt 后再多补几次。
    /// </summary>
    private void FocusFileSearchSoon()
    {
        _fileFocusRequestedUtc = DateTime.UtcNow;
        _fileFocusTicks = 8;
        FocusFileSearchCore();
        Dispatcher.BeginInvoke(FocusFileSearchCore, DispatcherPriority.Input);
        Dispatcher.BeginInvoke(FocusFileSearchCore, DispatcherPriority.ContextIdle);
        Dispatcher.BeginInvoke(FocusFileSearchCore, DispatcherPriority.ApplicationIdle);
        if (_fileFocusTimer == null)
        {
            _fileFocusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _fileFocusTimer.Tick += (_, _) =>
            {
                // Alt 还按着时焦点经常粘不住，松手后再放
                if (IsAltDown())
                {
                    FocusFileSearchCore();
                    return;
                }
                if (FileSearchBox.IsKeyboardFocused || --_fileFocusTicks <= 0)
                {
                    _fileFocusTimer?.Stop();
                    return;
                }
                FocusFileSearchCore();
            };
        }
        _fileFocusTimer.Stop();
        _fileFocusTimer.Start();
    }

    private void FocusFileSearchCore()
    {
        if (!ShouldKeepFileInputFocus() || _fileFocusBusy) return;
        _fileFocusBusy = true;
        try
        {
            ForceOurWindowForeground();
            FileSearchBox.Focusable = true;
            FocusManager.SetFocusedElement(this, FileSearchBox);
            Keyboard.Focus(FileSearchBox);
            FileSearchBox.Focus();
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                SetFocus(hwnd);
            Keyboard.Focus(FileSearchBox);
            if (!FileSearchBox.IsKeyboardFocused)
                FileSearchBox.Focus();
        }
        catch { /* ignore */ }
        finally
        {
            _fileFocusBusy = false;
        }
    }

    /// <summary>WebView 子窗会在页签切过来之后把键盘焦点抢走，所以多补几次。</summary>
    private void FocusAgentInputSoon()
    {
        _agentFocusRequestedUtc = DateTime.UtcNow;
        _agentFocusTicks = 4;
        EnsureAgentFocusHooks();
        FocusAgentInputCore();
        Dispatcher.BeginInvoke(FocusAgentInputCore, DispatcherPriority.Input);
        Dispatcher.BeginInvoke(FocusAgentInputCore, DispatcherPriority.ContextIdle);
        Dispatcher.BeginInvoke(FocusAgentInputCore, DispatcherPriority.ApplicationIdle);
        if (_agentFocusTimer == null)
        {
            _agentFocusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _agentFocusTimer.Tick += (_, _) =>
            {
                if (AgentInput.IsKeyboardFocused || --_agentFocusTicks <= 0)
                {
                    _agentFocusTimer?.Stop();
                    return;
                }
                FocusAgentInputCore();
            };
        }
        _agentFocusTimer.Stop();
        _agentFocusTimer.Start();
    }

    private void EnsureAgentFocusHooks()
    {
        if (_agentFocusHooked) return;
        _agentFocusHooked = true;
        AgentTimeline.Focusable = false;
        KeyboardNavigation.SetIsTabStop(AgentTimeline, false);
        AgentTimeline.PreviewGotKeyboardFocus += WebView_PreviewGotKeyboardFocus;
        CyberNewsWeb.PreviewGotKeyboardFocus += WebView_PreviewGotKeyboardFocus;
        CyberNewsWebPrefetch.PreviewGotKeyboardFocus += WebView_PreviewGotKeyboardFocus;
    }

    private void WebView_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!ShouldKeepAgentInputFocus()) return;
        e.Handled = true;
        Dispatcher.BeginInvoke(FocusAgentInputCore, DispatcherPriority.Input);
    }

    private void FocusAgentInputCore()
    {
        if (!ShouldKeepAgentInputFocus() || _agentFocusBusy) return;
        _agentFocusBusy = true;
        try
        {
            ForceOurWindowForeground();
            AgentInput.Focusable = true;
            FocusManager.SetFocusedElement(this, AgentInput);
            Keyboard.Focus(AgentInput);
            AgentInput.Focus();
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                SetFocus(hwnd);
            // SetFocus 会同步触发 WM_SETFOCUS，可能把焦点写回 WebView，再放一次
            Keyboard.Focus(AgentInput);
            if (!AgentInput.IsKeyboardFocused)
                AgentInput.Focus();
        }
        catch { /* ignore */ }
        finally
        {
            _agentFocusBusy = false;
        }
    }

    /// <summary>从别的程序按 Alt+空格 时，普通 Activate 经常拿不到前台，键盘仍留在原窗口。</summary>
    private void ForceOurWindowForeground()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var fg = GetForegroundWindow();
        if (fg != hwnd)
        {
            uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
            var thisThread = GetCurrentThreadId();
            if (fgThread != 0 && fgThread != thisThread)
            {
                AttachThreadInput(fgThread, thisThread, true);
                SetForegroundWindow(hwnd);
                AttachThreadInput(fgThread, thisThread, false);
            }
            else
            {
                SetForegroundWindow(hwnd);
            }
        }
        Activate();
        Topmost = true;
    }

    private void UnregisterWakeHotkey()
    {
        UnregisterTabHotkeys();
        UninstallKeyboardHook();
        if (_hwndSource != null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmWindowPosChanging && _lastPlaceX != int.MinValue && _lastPlaceW > 0 && _lastPlaceH > 0)
        {
            var pos = Marshal.PtrToStructure<WindowPos>(lParam);
            pos.x = _lastPlaceX;
            pos.y = _lastPlaceY;
            pos.cx = _lastPlaceW;
            pos.cy = _lastPlaceH;
            pos.flags &= ~3u; // 清掉不移动、不改大小，按上面的矩形来
            Marshal.StructureToPtr(pos, lParam, false);
        }
        if ((uint)msg != WmHotkey) return IntPtr.Zero;
        var id = wParam.ToInt32();
        if (id is >= HotkeyIdTabBase + 1 and <= HotkeyIdTabBase + 6)
        {
            handled = true;
            var digit = id - HotkeyIdTabBase;
            Dispatcher.BeginInvoke(() =>
            {
                if (!_expanded) ExpandFromTray();
                else
                {
                    Activate();
                    Topmost = true;
                }
                TrySwitchTabByNumber(digit);
                ScheduleShellFocusReclaim();
            });
        }
        return IntPtr.Zero;
    }

    private void Collapse()
    {
        FlushNoteSave();
        UnregisterTabHotkeys();
        _expanded = false;
        _pinned = false;
        _inputFocused = false;
        UpdatePinButton();
        _leaveSince = null;
        _awaitPanelEnter = false;
        PlaceCollapsed();
        SyncEdgeTimerInterval();
    }

    /// <summary>收起 150ms、钉住 200ms、展开未钉 80ms。</summary>
    private void SyncEdgeTimerInterval()
    {
        var ms = !_expanded ? 150 : _pinned ? 200 : 80;
        var want = TimeSpan.FromMilliseconds(ms);
        if (_edgeTimer.Interval != want)
            _edgeTimer.Interval = want;
    }

    /// <summary>仅在侧栏展开且前台时注册，避免全局抢走其它程序的 Ctrl+数字。</summary>
    private void RegisterTabHotkeys()
    {
        if (_tabHotkeysRegistered || !_expanded) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var ok = true;
        for (var i = 1; i <= 6; i++)
        {
            var id = HotkeyIdTabBase + i;
            var vk = (uint)(0x30 + i); // '1'..'6'
            if (!RegisterHotKey(hwnd, id, ModControl | ModNoRepeat, vk)
                && !RegisterHotKey(hwnd, id, ModControl, vk))
                ok = false;
        }
        _tabHotkeysRegistered = ok;
    }

    private void UnregisterTabHotkeys()
    {
        if (!_tabHotkeysRegistered) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            for (var i = 1; i <= 6; i++)
                UnregisterHotKey(hwnd, HotkeyIdTabBase + i);
        }
        _tabHotkeysRegistered = false;
    }

    private void BtnPin_Click(object sender, RoutedEventArgs e)
    {
        if (_pinned)
        {
            _pinned = false;
            UpdatePinButton();
            SyncEdgeTimerInterval();
            return;
        }

        _pinned = true;
        _leaveSince = null;
        _awaitPanelEnter = false;
        if (!_expanded) Expand();
        else
        {
            Topmost = true;
            Activate();
        }
        UpdatePinButton();
        SyncEdgeTimerInterval();
    }

    private void UpdatePinButton()
    {
        var accent = (MediaBrush)FindResource("Accent");
        var muted = (MediaBrush)FindResource("Muted");
        PinIcon.Kind = _pinned
            ? MaterialDesignThemes.Wpf.PackIconKind.Pin
            : MaterialDesignThemes.Wpf.PackIconKind.PinOutline;
        PinIcon.Foreground = _pinned ? accent : muted;
        BtnPin.ToolTip = _pinned
            ? "已钉住：不自动收起（F1 取消钉住，Esc 收起）"
            : "钉子：固定展开、不自动收起（F1）";
    }

    private void EdgeTimer_Tick(object? sender, EventArgs e)
    {
        SyncEdgeTimerInterval();
        var mouse = WinForms.Control.MousePosition; // 物理像素
        var edgeScreen = ScreenNearLeftEdge(mouse);
        var dockLocked = IsDockLocked();

        if (!_expanded)
        {
            if (edgeScreen == null)
            {
                _edgeHoverSince = null;
                return;
            }
            // 只认主屏左缘，扩展屏贴边不把侧栏拉过去
            if (!edgeScreen.Primary)
            {
                _edgeHoverSince = null;
                return;
            }
            // 非桌面（其它应用在前台）不贴边展开；托盘 / 双击 Ctrl 仍可开
            if (ShouldSuppressEdgeExpand(edgeScreen))
            {
                _edgeHoverSince = null;
                return;
            }
            // 需在左缘停留一小段时间，减少路过时误触
            _edgeHoverSince ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - _edgeHoverSince.Value).TotalMilliseconds < EdgeExpandDwellMs)
                return;

            _dockScreen = edgeScreen;
            _awaitPanelEnter = false;
            _edgeHoverSince = null;
            Expand();
            return;
        }

        // 钉住本来就不收，不必每拍算 DPI / 宽度
        if (_pinned)
        {
            _leaveSince = null;
            return;
        }

        var screen = ResolveScreen(_dockScreen)
                     ?? WinForms.Screen.PrimaryScreen
                     ?? PickDefaultDockScreen();
        var scale = DpiScale(screen);
        var dipW = CapPanelWidthDip(screen, ActivePanelWidthDip);
        var widthPx = Math.Max(1, (int)Math.Round(dipW * scale));
        var hit = Math.Max(6, (int)Math.Ceiling(EdgeHitDip * scale));
        var wa = screen.WorkingArea;

        bool nearEdge = mouse.X >= wa.Left - 2 && mouse.X <= wa.Left + hit
                        && mouse.Y >= wa.Top && mouse.Y <= wa.Bottom;
        bool inPanel = mouse.X >= wa.Left && mouse.X <= wa.Left + widthPx
                       && mouse.Y >= wa.Top && mouse.Y <= wa.Bottom;

        if (_awaitPanelEnter)
        {
            if (inPanel || nearEdge)
            {
                _awaitPanelEnter = false;
                _leaveSince = null;
            }
            return; // 尚未进过侧栏：保持展开
        }

        // 投屏抖动时坐标常瞬移出屏，加长离开判定，避免侧栏自己收起又展开
        if (dockLocked)
        {
            _leaveSince = null;
            return;
        }

        // 搜索框等仍记着焦点时，窗口已经不在前台就不要再挡住收起
        var editingHere = _inputFocused && IsOurWindowForeground();
        if (editingHere || inPanel || nearEdge)
        {
            _leaveSince = null;
            return;
        }

        var now = DateTime.UtcNow;
        if (_leaveSince == null) _leaveSince = now;
        else if ((now - _leaveSince.Value).TotalMilliseconds >= 50)
            Collapse();
    }

    private void Input_GotFocus(object sender, RoutedEventArgs e)
    {
        _inputFocused = true;
        // 回到侧栏编辑时关掉文件监视，避免自动保存触发回写导致光标乱跳
        if (ReferenceEquals(sender, NoteEdit))
            DetachNoteWatcher();
    }

    private void Input_LostFocus(object sender, RoutedEventArgs e)
    {
        _inputFocused = false;
        if (ReferenceEquals(sender, NoteEdit))
        {
            FlushNoteSave();
            RefreshNoteChipTitlesIfNeeded();
        }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // F1 → 钉住 / 取消钉住（含 System 键路径，避免输入框吞掉）
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Space
            && (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt
            && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == ModifierKeys.None)
        {
            FocusFileSearchFromHotkey();
            e.Handled = true;
            return;
        }
        if (key == Key.Space
            && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && (Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Shift)) == ModifierKeys.None)
        {
            FocusAgentInputFromHotkey();
            e.Handled = true;
            return;
        }
        if (key == Key.F1)
        {
            BtnPin_Click(sender, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Ctrl+1..6：钩子未装上时的后备
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && (Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Shift)) == ModifierKeys.None)
        {
            var digit = key switch
            {
                Key.D1 or Key.NumPad1 => 1,
                Key.D2 or Key.NumPad2 => 2,
                Key.D3 or Key.NumPad3 => 3,
                Key.D4 or Key.NumPad4 => 4,
                Key.D5 or Key.NumPad5 => 5,
                Key.D6 or Key.NumPad6 => 6,
                _ => 0,
            };
            if (digit != 0 && TrySwitchTabByNumber(digit))
            {
                e.Handled = true;
                return;
            }
        }

        // 网安：→ 下一条，← 刷新；详情 ↓/空格 下翻，↑/Shift+空格 上翻（与钩子一致）
        if (_tab == TabMode.Cyber
            && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == ModifierKeys.None)
        {
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            if (!shift && key == Key.Right)
            {
                OpenCyberNewsNext();
                e.Handled = true;
                return;
            }
            if (!shift && key == Key.Left)
            {
                RefreshCyberNewsFromHotkey();
                e.Handled = true;
                return;
            }
            if (_cyberNewsOpen != null)
            {
                if (!shift && (key == Key.Down || key == Key.Up))
                {
                    ScrollCyberNewsPage(forward: key == Key.Down);
                    e.Handled = true;
                    return;
                }
                if (key == Key.Space)
                {
                    ScrollCyberNewsPage(forward: !shift);
                    e.Handled = true;
                    return;
                }
            }
        }

        // 邮件阅读：Ctrl+D → 标记垃圾并移出列表
        if (!_inputFocused
            && _tab == TabMode.Mail
            && _mailView == MailView.Read
            && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && e.Key == Key.D)
        {
            BtnMailJunk_Click(sender, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // 邮件阅读：Backspace / 浏览器后退键 → 返回列表
        if (!_inputFocused
            && _tab == TabMode.Mail
            && _mailView == MailView.Read
            && (e.Key == Key.Back || e.Key == Key.BrowserBack))
        {
            BtnMailBack_Click(sender, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape) return;
        if (TryHandleEscapeKey())
            e.Handled = true;
    }

    /// <summary>Esc 收起侧栏。行内改标题时先取消编辑。</summary>
    private bool TryHandleEscapeKey()
    {
        if (_inlineEscCancel != null)
        {
            var cancel = _inlineEscCancel;
            _inlineEscCancel = null;
            cancel();
            _inputFocused = false;
            return true;
        }

        Collapse();
        return true;
    }

    private void BlurInputFocus()
    {
        _inputFocused = false;
        FocusManager.SetFocusedElement(this, this);
        Keyboard.ClearFocus();
        Focusable = true;
        Keyboard.Focus(this);
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = VisualTreeHelper.GetParent(node)
                   ?? (node is FrameworkElement fe ? fe.Parent as DependencyObject : null);
        }
        return false;
    }

    /// <summary>鼠标侧键回退：网安页下一篇；阅读邮件时返回列表。网页区域由鼠标钩子处理。</summary>
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.XButton1) return;
        if (_tab == TabMode.Cyber)
        {
            OpenCyberNewsNext();
            e.Handled = true;
            return;
        }
        if (_tab != TabMode.Mail || _mailView != MailView.Read) return;
        BtnMailBack_Click(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    private void SetupTray()
    {
        _trayIcon?.Dispose();
        _trayIcon = CreateTrayIcon();
        _tray = new WinForms.NotifyIcon
        {
            Icon = _trayIcon,
            Visible = true,
            Text = "方老师的工作区",
        };
        var menu = new WinForms.ContextMenuStrip
        {
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9.5f),
            ShowImageMargin = false,
            ShowCheckMargin = true,
            RenderMode = WinForms.ToolStripRenderMode.Professional,
            BackColor = TrayTheme.Panel,
            ForeColor = TrayTheme.Fg,
            Padding = new WinForms.Padding(4, 4, 4, 4),
        };
        menu.Renderer = new TrayMenuRenderer();
        void AddItem(string text, EventHandler onClick)
        {
            var item = new WinForms.ToolStripMenuItem(text)
            {
                ForeColor = TrayTheme.Fg,
                BackColor = TrayTheme.Panel,
                Padding = new WinForms.Padding(2, 4, 8, 4),
            };
            item.Click += onClick;
            menu.Items.Add(item);
        }
        AddItem("展开侧栏\t连按两下 Ctrl", (_, _) => Dispatcher.Invoke(WakeFromHotkey));
        AddItem("文件搜索\tAlt+空格", (_, _) => Dispatcher.Invoke(FocusFileSearchFromHotkey));
        AddItem("大模型输入\tCtrl+空格", (_, _) => Dispatcher.Invoke(FocusAgentInputFromHotkey));
        AddItem("收起侧栏", (_, _) => Dispatcher.Invoke(Collapse));
        menu.Items.Add(new WinForms.ToolStripSeparator { BackColor = TrayTheme.Panel });
        AddItem("登录并同步", async (_, _) => await Dispatcher.InvokeAsync(async () => await RunSyncAsync(true)));
        AddItem("打开同步配置", (_, _) => Dispatcher.Invoke(OpenSyncConfig));
        AddItem("退出微软账号", async (_, _) =>
        {
            await _sync.SignOutAsync();
            Dispatcher.Invoke(UpdateSyncHint);
        });
        menu.Items.Add(new WinForms.ToolStripSeparator { BackColor = TrayTheme.Panel });
        AddItem("邮箱设置", (_, _) => Dispatcher.Invoke(OpenMailSettingsFromTray));
        var pin = new WinForms.ToolStripMenuItem("钉住侧栏")
        {
            ForeColor = TrayTheme.Fg,
            BackColor = TrayTheme.Panel,
            Padding = new WinForms.Padding(2, 4, 8, 4),
        };
        pin.Click += (_, _) => Dispatcher.Invoke(() =>
        {
            BtnPin_Click(this, new RoutedEventArgs());
            pin.Checked = _pinned;
        });
        var auto = new WinForms.ToolStripMenuItem("开机自启")
        {
            CheckOnClick = true,
            ForeColor = TrayTheme.Fg,
            BackColor = TrayTheme.Panel,
            Padding = new WinForms.Padding(2, 4, 8, 4),
        };
        auto.CheckedChanged += (_, _) => Autostart.SetEnabled(auto.Checked);
        menu.Opening += (_, _) =>
        {
            pin.Checked = _pinned;
            auto.Checked = Autostart.IsEnabled();
        };
        menu.Items.Add(pin);
        menu.Items.Add(auto);
        menu.Items.Add(new WinForms.ToolStripSeparator { BackColor = TrayTheme.Panel });
        AddItem("退出", (_, _) => Dispatcher.Invoke(Quit));
        _tray.ContextMenuStrip = menu;
        // 左键单击 / 双击：展开；已展开则置顶。右键仍出菜单。
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button != WinForms.MouseButtons.Left) return;
            Dispatcher.Invoke(() =>
            {
                if (_expanded)
                {
                    Activate();
                    Topmost = true;
                }
                else ExpandFromTray();
            });
        };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ExpandFromTray);
    }

    /// <summary>工作区托盘图标：2×2 分格（笔记/待办/邮件/热点），Accent #0078D4。</summary>
    private static DrawingIcon CreateTrayIcon()
    {
        var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.Clear(DrawingColor.Transparent);

            var soft = DrawingColor.FromArgb(222, 236, 249);   // AccentSoft
            var accent = DrawingColor.FromArgb(0, 120, 212);   // Accent
            var tile = DrawingColor.FromArgb(230, 255, 255, 255);

            using (var softBrush = new System.Drawing.SolidBrush(soft))
                FillRoundRect(g, softBrush, 1, 1, 30, 30, 8);

            // 外框
            using (var accentBrush = new System.Drawing.SolidBrush(accent))
                FillRoundRect(g, accentBrush, 6, 6, 20, 20, 5);

            // 2×2 分格：工作区多页签
            using (var cell = new System.Drawing.SolidBrush(tile))
            {
                g.FillRectangle(cell, 9, 9, 6, 6);
                g.FillRectangle(cell, 17, 9, 6, 6);
                g.FillRectangle(cell, 9, 17, 6, 6);
            }
            using (var highlight = new System.Drawing.SolidBrush(DrawingColor.White))
                g.FillRectangle(highlight, 17, 17, 6, 6);
        }

        // Clone 后释放 HICON，避免句柄泄漏；保留 bmp 直到 Icon 持有像素
        var hIcon = bmp.GetHicon();
        try
        {
            using var tmp = DrawingIcon.FromHandle(hIcon);
            return (DrawingIcon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
            bmp.Dispose();
        }
    }

    private static void FillRoundRect(System.Drawing.Graphics g, System.Drawing.Brush brush, int x, int y, int w, int h, int r)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>与 App.xaml 主界面色板对齐：Panel / Fg / Accent / AccentSoft / Line。</summary>
    private static class TrayTheme
    {
        public static readonly DrawingColor Panel = DrawingColor.White;                         // Panel #FFFFFF
        public static readonly DrawingColor Fg = DrawingColor.FromArgb(0x1C, 0x1B, 0x1F);      // Fg
        public static readonly DrawingColor Muted = DrawingColor.FromArgb(0x60, 0x5E, 0x5C);   // Muted
        public static readonly DrawingColor Accent = DrawingColor.FromArgb(0x00, 0x78, 0xD4);  // Accent
        public static readonly DrawingColor AccentSoft = DrawingColor.FromArgb(0xDE, 0xEC, 0xF9);
        public static readonly DrawingColor Hover = DrawingColor.FromArgb(0x14, 0x00, 0x78, 0xD4); // Hover
        public static readonly DrawingColor Line = DrawingColor.FromArgb(0x1F, 0x00, 0x00, 0x00);
        public static readonly DrawingColor Border = DrawingColor.FromArgb(0x28, 0x00, 0x00, 0x00);
    }

    /// <summary>托盘菜单：白底、微软蓝悬停/勾选，贴近侧栏观感。</summary>
    private sealed class TrayMenuRenderer : WinForms.ToolStripProfessionalRenderer
    {
        public TrayMenuRenderer() : base(new TrayColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(WinForms.ToolStripRenderEventArgs e)
        {
            using var brush = new System.Drawing.SolidBrush(TrayTheme.Panel);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(WinForms.ToolStripRenderEventArgs e)
        {
            using var pen = new System.Drawing.Pen(TrayTheme.Border);
            var r = e.AffectedBounds;
            e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var bounds = new System.Drawing.Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
            // 仅悬停铺软蓝底；已勾选不加底色，靠左侧对勾区分
            var fill = e.Item.Selected ? TrayTheme.AccentSoft : TrayTheme.Panel;
            using var brush = new System.Drawing.SolidBrush(fill);
            FillRoundRect(g, brush, bounds.X, bounds.Y + 1, bounds.Width, bounds.Height - 2, 6);
        }

        protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
        {
            // 文字保持正文色；仅悬停时用 Accent，避免勾选项整行发蓝抢戏
            e.TextFont = new System.Drawing.Font("Microsoft YaHei UI", 9.5f);
            e.TextColor = e.Item.Selected ? TrayTheme.Accent : TrayTheme.Fg;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(WinForms.ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.ContentRectangle.Top + e.Item.ContentRectangle.Height / 2;
            using var pen = new System.Drawing.Pen(TrayTheme.Line);
            e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
        }

        protected override void OnRenderImageMargin(WinForms.ToolStripRenderEventArgs e)
        {
            using var brush = new System.Drawing.SolidBrush(TrayTheme.Panel);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderItemCheck(WinForms.ToolStripItemImageRenderEventArgs e)
        {
            // 不画系统默认勾选框底，只留细对勾，贴近 Fluent 菜单
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

            var cx = e.Item.ContentRectangle.X + 12;
            var cy = e.Item.ContentRectangle.Y + e.Item.Height / 2;
            using var pen = new System.Drawing.Pen(TrayTheme.Accent, 1.8f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
            };
            g.DrawLines(pen, new[]
            {
                new DrawingPoint(cx - 4, cy),
                new DrawingPoint(cx - 1, cy + 3),
                new DrawingPoint(cx + 5, cy - 4),
            });
        }
    }

    private sealed class TrayColorTable : WinForms.ProfessionalColorTable
    {
        public override DrawingColor MenuItemSelected => TrayTheme.AccentSoft;
        public override DrawingColor MenuItemSelectedGradientBegin => TrayTheme.AccentSoft;
        public override DrawingColor MenuItemSelectedGradientEnd => TrayTheme.AccentSoft;
        public override DrawingColor MenuItemBorder => TrayTheme.AccentSoft;
        public override DrawingColor MenuBorder => TrayTheme.Border;
        public override DrawingColor ToolStripDropDownBackground => TrayTheme.Panel;
        public override DrawingColor ImageMarginGradientBegin => TrayTheme.Panel;
        public override DrawingColor ImageMarginGradientMiddle => TrayTheme.Panel;
        public override DrawingColor ImageMarginGradientEnd => TrayTheme.Panel;
        public override DrawingColor SeparatorDark => TrayTheme.Line;
        public override DrawingColor SeparatorLight => TrayTheme.Panel;
        public override DrawingColor CheckBackground => TrayTheme.AccentSoft;
        public override DrawingColor CheckSelectedBackground => TrayTheme.AccentSoft;
        public override DrawingColor CheckPressedBackground => TrayTheme.AccentSoft;
    }

    private void Quit()
    {
        FlushNoteSave();
        UnregisterWakeHotkey();
        _noteWatcher?.Dispose();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _edgeTimer.Stop();
        _syncTimer.Stop();
        _mailPollTimer.Stop();
        _cyberPollTimer.Stop();
        if (_displayDebounce != null)
        {
            _displayDebounce.Stop();
            _displayDebounce.Tick -= OnDisplayDebounceTick;
        }
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        _trayIcon?.Dispose();
        _trayIcon = null;
        _allowClose = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    // ----- calendar -----
    private void PrevMonth_Click(object sender, RoutedEventArgs e)
    {
        _month--;
        if (_month < 1) { _month = 12; _year--; }
        RebuildCalendar();
    }

    private void NextMonth_Click(object sender, RoutedEventArgs e)
    {
        _month++;
        if (_month > 12) { _month = 1; _year++; }
        RebuildCalendar();
    }

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        var t = DateTime.Today;
        _year = t.Year;
        _month = t.Month;
        _selected = t;
        _filterByDay = true;
        _tab = TabMode.Todo;
        StyleTabs();
        RebuildCalendar();
        UpdateDueHint();
        UpdateTodoPlaceholder();
        ReloadTodos();
    }

    private void RebuildCalendar()
    {
        MonthLabel.Text = $"{_year}年{_month}月";
        DayGrid.Children.Clear();
        var counts = _store.DayTodoCounts(_year, _month);
        var first = new DateTime(_year, _month, 1);
        int start = (int)first.DayOfWeek; // 周日为一周起点
        int days = DateTime.DaysInMonth(_year, _month);
        int cells = (int)Math.Ceiling((start + days) / 7.0) * 7;
        // 按周数定高（每格 36），避免 DockPanel 填满时把末行挤没/裁切
        var rows = Math.Max(5, cells / 7);
        DayGrid.Height = rows * 36;
        DayGrid.MinHeight = rows * 36;
        var today = DateTime.Today;
        var accent = (MediaBrush)FindResource("Accent");
        var accentSoft = (MediaBrush)FindResource("AccentSoft");
        var muted = (MediaBrush)FindResource("Muted");
        var fg = (MediaBrush)FindResource("Fg");

        // 去掉默认 Button 铬，避免宽屏下悬停蓝块把格子撑丑
        var bareBtn = new ControlTemplate(typeof(WpfButton));
        bareBtn.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));

        for (int i = 0; i < cells; i++)
        {
            int dayNum = i - start + 1;
            DateTime date;
            bool inMonth = dayNum >= 1 && dayNum <= days;
            if (!inMonth)
            {
                if (dayNum < 1)
                    date = first.AddDays(dayNum - 1);
                else
                    date = new DateTime(_year, _month, days).AddDays(dayNum - days);
            }
            else date = new DateTime(_year, _month, dayNum);

            var key = date.ToString("yyyy-MM-dd");
            bool isSel = _filterByDay && date.Date == _selected.Date;
            bool isToday = date.Date == today && inMonth;
            counts.TryGetValue(key, out int count);
            bool hasDot = count > 0 && inMonth;

            MediaBrush numFg;
            MediaBrush chipBg;
            if (!inMonth)
            {
                numFg = muted;
                chipBg = MediaBrushes.Transparent;
            }
            else if (isSel)
            {
                numFg = MediaBrushes.White;
                chipBg = accent;
            }
            else if (isToday)
            {
                numFg = accent;
                chipBg = accentSoft;
            }
            else
            {
                numFg = fg;
                chipBg = MediaBrushes.Transparent;
            }

            var num = new TextBlock
            {
                Text = date.Day.ToString(),
                FontSize = 13,
                FontWeight = isSel || isToday ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = numFg,
                HorizontalAlignment = WpfHorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, hasDot ? 2 : 0),
            };

            var chipInner = new Grid();
            chipInner.Children.Add(num);
            if (hasDot)
            {
                chipInner.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 4,
                    Height = 4,
                    Fill = isSel ? MediaBrushes.White : accent,
                    HorizontalAlignment = WpfHorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 3),
                });
            }

            var dayChrome = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(16),
                Background = chipBg,
                BorderBrush = MediaBrushes.Transparent,
                BorderThickness = new Thickness(0),
                Opacity = inMonth ? 1 : 0.4,
                Child = chipInner,
                HorizontalAlignment = WpfHorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // 按钮拉满格子，圆点仍居中，与「日一二…」列对齐、铺满卡片宽
            var btn = new WpfButton
            {
                Margin = new Thickness(0),
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = MediaBrushes.Transparent,
                Cursor = WpfCursors.Hand,
                Template = bareBtn,
                Content = dayChrome,
                HorizontalAlignment = WpfHorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = WpfHorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };

            var capture = date;
            btn.Click += (_, _) =>
            {
                _selected = capture;
                _year = capture.Year;
                _month = capture.Month;
                _filterByDay = true;
                _tab = TabMode.Todo;
                StyleTabs();
                RebuildCalendar();
                UpdateDueHint();
                UpdateTodoPlaceholder();
                ReloadTodos();
            };

            DayGrid.Children.Add(btn);
        }
    }

    private void UpdateDueHint()
    {
        if (_filterByDay)
        {
            var isToday = _selected.Date == DateTime.Today;
            DueHint.Text = isToday
                ? $"这天 · 今天 {_selected:MM-dd}"
                : $"这天 · {_selected:yyyy-MM-dd}";
        }
        else DueHint.Text = $"全部未完成 · 新待办挂到 {_selected:MM-dd}";
    }

    // ----- tabs -----
    private bool InvokeTabClick(MouseButtonEventHandler handler)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent,
        };
        handler(this, args);
        return true;
    }

    private void TabNote_Click(object sender, MouseButtonEventArgs e)
    {
        FlushNoteSave();
        _tab = TabMode.Note;
        if (_editingTodoId == null)
        {
            EnsureActiveNote();
            ReloadNoteList();
            LoadActiveNote();
        }
        StyleTabs();
        UpdateNoteChrome();
    }

    private void TabTodo_Click(object sender, MouseButtonEventArgs e)
    {
        FlushNoteSave();
        RefreshNoteChipTitlesIfNeeded();
        ExitTodoEditMode();
        _tab = TabMode.Todo;
        ShowAllTodos();
        StyleTabs();
    }

    private void TodoSectionTitle_Click(object sender, MouseButtonEventArgs e)
    {
        ExitTodoEditMode();
        ShowAllTodos();
        e.Handled = true;
    }

    /// <summary>取消按日筛选，列表与「待办事项」页签一致显示全部。</summary>
    private void ShowAllTodos()
    {
        _filterByDay = false;
        RebuildCalendar();
        UpdateDueHint();
        UpdateTodoPlaceholder();
        ReloadTodos();
    }

    private void TabMail_Click(object sender, MouseButtonEventArgs e)
    {
        FlushNoteSave();
        RefreshNoteChipTitlesIfNeeded();
        ExitTodoEditMode();
        _tab = TabMode.Mail;
        StyleTabs();
        _ = EnterMailTabAsync();
    }

    private void TabAgent_Click(object sender, MouseButtonEventArgs e)
    {
        FlushNoteSave();
        RefreshNoteChipTitlesIfNeeded();
        ExitTodoEditMode();
        _tab = TabMode.Agent;
        StyleTabs();
        // 先占住焦点。Ctrl+5 随后会收回壳层焦点；时间线加载完还会再抢一次。
        FocusAgentInputSoon();
        _ = EnterAgentTabAsync();
    }

    private void TabFile_Click(object sender, MouseButtonEventArgs e)
    {
        FlushNoteSave();
        RefreshNoteChipTitlesIfNeeded();
        ExitTodoEditMode();
        _tab = TabMode.File;
        StyleTabs();
        FocusFileSearchSoon();
        _ = RunFileSearchAsync();
    }

    private readonly DispatcherTimer _fileSearchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private bool _fileSearchBusy;
    private bool _fileSearchQueued;
    private bool _filePickAfterSearch;
    private bool _filePicking;
    private int _fileSelected = -1;
    private string _fileSearchedText = "";
    private List<EverythingHit> _fileHits = new();
    private readonly List<DockPanel> _fileRows = new();
    private static readonly SolidColorBrush FileSelectedBrush = FreezeBrush(System.Windows.Media.Color.FromRgb(0xD6, 0xE8, 0xFF));

    private void FileSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filePicking = false;
        _fileSearchTimer.Stop();
        _fileSearchTimer.Start();
    }

    private void FileSearchBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up)
        {
            if (_fileHits.Count == 0) return;
            e.Handled = true;
            // 结果已实时列出，方向键直接进入选中，不必先回车
            if (!_filePicking || _fileSelected < 0)
                SelectFile(e.Key == Key.Down ? 0 : _fileHits.Count - 1);
            else
                MoveFileSelection(e.Key == Key.Down ? 1 : -1);
            return;
        }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _fileSearchTimer.Stop();
        var text = FileSearchBox.Text.Trim();
        if (_fileSearchBusy || !string.Equals(text, _fileSearchedText, StringComparison.Ordinal))
        {
            _filePickAfterSearch = true;
            _ = RunFileSearchAsync();
            return;
        }
        if (_fileHits.Count == 0) return;
        if (!_filePicking || _fileSelected < 0 || _fileSelected >= _fileHits.Count)
            SelectFile(0);
        OpenEverythingHitAndCollapse(_fileHits[_fileSelected]);
    }

    private void MoveFileSelection(int delta)
    {
        if (_fileHits.Count == 0) return;
        var next = _fileSelected < 0 ? 0 : _fileSelected + delta;
        if (next < 0) next = 0;
        if (next >= _fileHits.Count) next = _fileHits.Count - 1;
        SelectFile(next);
    }

    private void SelectFile(int index)
    {
        if (index < 0 || index >= _fileRows.Count) return;
        _filePicking = true;
        _fileSelected = index;
        for (var i = 0; i < _fileRows.Count; i++)
            _fileRows[i].Background = i == index ? FileSelectedBrush : MediaBrushes.Transparent;
        _fileRows[index].BringIntoView();
    }

    private static SolidColorBrush FreezeBrush(System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void BtnEverythingStart_Click(object sender, RoutedEventArgs e)
    {
        if (!EverythingSearch.TryStart())
        {
            FileSearchHint.Text = "没找到 Everything.exe";
            return;
        }
        FileSearchHint.Text = "正在启动…";
        BtnEverythingStart.IsEnabled = false;
        var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        var tries = 0;
        wait.Tick += (_, _) =>
        {
            tries++;
            if (EverythingSearch.IsRunning() || tries >= 12)
            {
                wait.Stop();
                BtnEverythingStart.IsEnabled = true;
                _ = RunFileSearchAsync();
            }
        };
        wait.Start();
    }

    private async Task RunFileSearchAsync()
    {
        if (_fileSearchBusy)
        {
            _fileSearchQueued = true;
            return;
        }
        _fileSearchBusy = true;
        try
        {
            do
            {
                _fileSearchQueued = false;
                await RunFileSearchOnceAsync();
            }
            while (_fileSearchQueued);
            if (_filePickAfterSearch)
            {
                _filePickAfterSearch = false;
                if (_fileHits.Count > 0)
                {
                    SelectFile(0);
                    OpenEverythingHitAndCollapse(_fileHits[0]);
                }
            }
        }
        finally
        {
            _fileSearchBusy = false;
        }
    }

    private async Task RunFileSearchOnceAsync()
    {
        if (_tab != TabMode.File) return;
        var text = FileSearchBox.Text.Trim();
        FileSearchList.Children.Clear();
        _fileRows.Clear();
        _fileHits = new List<EverythingHit>();
        _fileSelected = -1;
        _filePicking = false;
        _fileSearchedText = text;
        if (!EverythingSearch.IsRunning())
        {
            FileSearchHint.Text = EverythingSearch.InstalledExe() == null
                ? "没找到 Everything"
                : "Everything 没在运行";
            BtnEverythingStart.Visibility = EverythingSearch.InstalledExe() == null
                ? Visibility.Collapsed
                : Visibility.Visible;
            return;
        }
        BtnEverythingStart.Visibility = Visibility.Collapsed;
        if (text.Length == 0)
        {
            FileSearchHint.Text = "输入文件名";
            return;
        }
        IReadOnlyList<EverythingHit> hits;
        try
        {
            hits = await Task.Run(() => EverythingSearch.Query(text));
        }
        catch (Exception ex)
        {
            _fileSearchedText = "";
            FileSearchHint.Text = ShortErr(ex);
            return;
        }
        if (_tab != TabMode.File) return;
        if (!string.Equals(text, FileSearchBox.Text.Trim(), StringComparison.Ordinal))
            return;
        _fileHits = hits.ToList();
        FileSearchHint.Text = hits.Count == 0 ? "没有结果" : $"{hits.Count} 条";
        foreach (var hit in hits)
        {
            var capture = hit;
            var kind = new TextBlock
            {
                Text = capture.IsFolder ? "目录" : "文件",
                Width = 36,
                FontSize = 11,
                Foreground = (MediaBrush)FindResource("Muted"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var name = new TextBlock
            {
                Text = capture.Name,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var dir = new TextBlock
            {
                Text = capture.Directory,
                FontSize = 11,
                Foreground = (MediaBrush)FindResource("Muted"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 0),
            };
            var textCol = new StackPanel();
            textCol.Children.Add(name);
            textCol.Children.Add(dir);
            var row = new DockPanel
            {
                Margin = new Thickness(8, 6, 8, 6),
                Cursor = WpfCursors.Hand,
                ToolTip = capture.FullPath,
            };
            DockPanel.SetDock(kind, Dock.Left);
            row.Children.Add(kind);
            row.Children.Add(textCol);
            row.MouseLeftButtonUp += (_, _) => OpenEverythingHit(capture);
            _fileRows.Add(row);
            FileSearchList.Children.Add(row);
        }
    }

    private void OpenEverythingHit(EverythingHit hit)
    {
        try
        {
            Process.Start(new ProcessStartInfo(hit.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            FileSearchHint.Text = ShortErr(ex);
        }
    }

    /// <summary>回车打开选中项后收起侧栏，把前台让给打开的文件/目录。</summary>
    private void OpenEverythingHitAndCollapse(EverythingHit hit)
    {
        try
        {
            Process.Start(new ProcessStartInfo(hit.FullPath) { UseShellExecute = true });
            Collapse();
        }
        catch (Exception ex)
        {
            FileSearchHint.Text = ShortErr(ex);
        }
    }

    private void TabCyber_Click(object sender, MouseButtonEventArgs e)
    {
        FlushNoteSave();
        RefreshNoteChipTitlesIfNeeded();
        ExitTodoEditMode();
        _tab = TabMode.Cyber;
        StyleTabs();
        _ = EnterCyberTabAsync();
    }

    private async Task EnterCyberTabAsync()
    {
        StyleCyberModeTabs();
        // 进网安页强制满屏重定位（避免沿用其它页签的 560 宽）
        _lastPlaceX = int.MinValue;
        if (_expanded) PlaceExpanded();
        ScheduleShellFocusReclaim(when: () => _tab == TabMode.Cyber);

        if (_cyberListMode == CyberListMode.Sources)
        {
            ShowCyberSourceList();
            return;
        }
        if (_cyberListMode == CyberListMode.Fav)
        {
            ShowCyberFavList();
            return;
        }

        // 热点才初始化主 WebView；预载实例留给 PrefetchNextCyberNews
        _ = EnsureCyberWebAsync();
        // ShowCyberHot… 里若联网刷新，结束时会自己打开第一条；这里只在未刷新时补一次
        var willFetch = _cyberNews.CachedUnread.Count == 0 || _cyberNews.IsFetchDue;
        await ShowCyberHotFromCacheThenMaybeFetchAsync();
        if (!willFetch)
            TryOpenFirstCyberNewsIfEmpty();
    }

    private async Task EnterAgentTabAsync()
    {
        AgentStatus.Text = "加载时间线…";
        await EnsureAgentTimelineAsync();
        await EnsureAgentWelcomeAsync();
        if (!AgentInput.IsKeyboardFocused)
            FocusAgentInputSoon();
    }

    // ----- 邮件（IMAP/SMTP，默认 mail.scu.edu.cn） -----

    private async Task EnterMailTabAsync()
    {
        _mail.Reload();
        if (!_mail.Config.IsConfigured)
        {
            ShowMailView(MailView.Settings);
            LoadMailSettingsForm();
            SetMailStatus("先填写川大邮箱账号（默认 mail.scu.edu.cn）");
            return;
        }
        ShowMailView(MailView.List);
        // 有缓存先秒开，过期再后台拉；无缓存才阻塞拉取
        await ReloadMailInboxAsync(force: false);
    }

    private void SetMailStatus(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            MailStatus.Text = "";
            MailStatus.Visibility = Visibility.Collapsed;
            return;
        }
        MailStatus.Text = text;
        MailStatus.Visibility = Visibility.Visible;
    }

    private void ShowMailView(MailView view)
    {
        var wasWide = _mailView is MailView.Read or MailView.Compose;
        var willWide = view is MailView.Read or MailView.Compose;
        _mailView = view;
        MailListHost.Visibility = view == MailView.List ? Visibility.Visible : Visibility.Collapsed;
        MailReadHost.Visibility = view == MailView.Read ? Visibility.Visible : Visibility.Collapsed;
        MailComposeHost.Visibility = view == MailView.Compose ? Visibility.Visible : Visibility.Collapsed;
        MailSettingsHost.Visibility = view == MailView.Settings ? Visibility.Visible : Visibility.Collapsed;
        BtnMailRefresh.Visibility = view == MailView.List ? Visibility.Visible : Visibility.Collapsed;

        void Paint(Border b, TextBlock t, bool on)
        {
            var accent = (MediaBrush)FindResource("Accent");
            var muted = (MediaBrush)FindResource("Muted");
            var fg = (MediaBrush)FindResource("Fg");
            b.BorderBrush = on ? accent : MediaBrushes.Transparent;
            t.Foreground = on ? fg : muted;
            t.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
        Paint(MailTabInbox, MailTabInboxText, view is MailView.List or MailView.Read);
        Paint(MailTabCompose, MailTabComposeText, view == MailView.Compose);
        Paint(MailTabSettings, MailTabSettingsText, view == MailView.Settings);
        UpdateMailSettingsTabVisibility();

        if (view == MailView.Compose)
            UpdateMailComposeHints();

        if (_expanded) PlaceExpanded();

        // 宽→窄时鼠标常还在「原加宽区域」，会被当成离开；先锁自动隐藏
        if (wasWide && !willWide && _expanded && !_pinned)
        {
            _awaitPanelEnter = true;
            _leaveSince = null;
        }
    }

    private void MailTabInbox_Click(object sender, MouseButtonEventArgs e)
    {
        if (!_mail.Config.IsConfigured)
        {
            ShowMailView(MailView.Settings);
            LoadMailSettingsForm();
            SetMailStatus("请先配置邮箱");
            return;
        }
        ShowMailView(MailView.List);
        _ = ReloadMailInboxAsync(force: false);
    }

    private void MailTabCompose_Click(object sender, MouseButtonEventArgs e)
    {
        if (!_mail.Config.IsConfigured)
        {
            ShowMailView(MailView.Settings);
            LoadMailSettingsForm();
            SetMailStatus("发送前请先配置邮箱");
            return;
        }
        _mailCurrent = null;
        MailTo.Text = "";
        MailSubject.Text = "";
        MailBody.Text = "";
        ShowMailView(MailView.Compose);
        SetMailStatus($"发件人 {_mail.Config.Email}");
        UpdateMailComposeHints();
        MailTo.Focus();
    }

    private void MailTabSettings_Click(object sender, MouseButtonEventArgs e)
    {
        ShowMailView(MailView.Settings);
        LoadMailSettingsForm();
        SetMailStatus(null);
    }

    /// <summary>已配置邮箱后隐藏「设置」页签，改由托盘菜单进入。</summary>
    private void UpdateMailSettingsTabVisibility()
    {
        _mail.Reload();
        var showTab = !_mail.Config.IsConfigured || _mailView == MailView.Settings;
        MailTabSettings.Visibility = showTab ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenMailSettingsFromTray()
    {
        ExpandFromTray();
        FlushNoteSave();
        RefreshNoteChipTitlesIfNeeded();
        ExitTodoEditMode();
        _tab = TabMode.Mail;
        StyleTabs();
        ShowMailView(MailView.Settings);
        LoadMailSettingsForm();
        SetMailStatus(null);
        // 托盘进入设置时强制显示页签，方便看到当前位置
        MailTabSettings.Visibility = Visibility.Visible;
    }

    private void MailComposeField_TextChanged(object sender, TextChangedEventArgs e)
        => UpdateMailComposeHints();

    private void UpdateMailComposeHints()
    {
        MailToHint.Visibility = string.IsNullOrEmpty(MailTo.Text) ? Visibility.Visible : Visibility.Collapsed;
        MailSubjectHint.Visibility = string.IsNullOrEmpty(MailSubject.Text) ? Visibility.Visible : Visibility.Collapsed;
        MailBodyHint.Visibility = string.IsNullOrEmpty(MailBody.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MailBody_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            e.Handled = true;
            BtnMailSend_Click(sender, new RoutedEventArgs());
        }
    }

    private void LoadMailSettingsForm()
    {
        var c = _mail.Config;
        MailCfgEmail.Text = c.Email;
        MailCfgDisplayName.Text = c.DisplayName;
        MailCfgImapHost.Text = string.IsNullOrWhiteSpace(c.ImapHost) ? "mail.scu.edu.cn" : c.ImapHost;
        MailCfgImapPort.Text = c.ImapPort <= 0 ? "993" : c.ImapPort.ToString();
        MailCfgSmtpHost.Text = string.IsNullOrWhiteSpace(c.SmtpHost) ? "mail.scu.edu.cn" : c.SmtpHost;
        MailCfgSmtpPort.Text = c.SmtpPort <= 0 ? "465" : c.SmtpPort.ToString();
        MailCfgPassword.Password = ""; // 不回填明文
    }

    private async void BtnMailSaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_mailBusy) return;
        var email = MailCfgEmail.Text.Trim();
        if (string.IsNullOrEmpty(email) || !email.Contains('@'))
        {
            SetMailStatus("请填写完整邮箱，例如 name@scu.edu.cn");
            return;
        }
        if (string.IsNullOrEmpty(MailCfgPassword.Password) && string.IsNullOrEmpty(_mail.Config.PasswordProtected))
        {
            SetMailStatus("请填写邮箱密码");
            return;
        }

        var cfg = MailAccountConfig.Load();
        cfg.Email = email;
        cfg.DisplayName = MailCfgDisplayName.Text.Trim();
        cfg.ImapHost = string.IsNullOrWhiteSpace(MailCfgImapHost.Text) ? "mail.scu.edu.cn" : MailCfgImapHost.Text.Trim();
        cfg.SmtpHost = string.IsNullOrWhiteSpace(MailCfgSmtpHost.Text) ? "mail.scu.edu.cn" : MailCfgSmtpHost.Text.Trim();
        if (!int.TryParse(MailCfgImapPort.Text.Trim(), out var imapPort)) imapPort = 993;
        if (!int.TryParse(MailCfgSmtpPort.Text.Trim(), out var smtpPort)) smtpPort = 465;
        cfg.ImapPort = imapPort;
        cfg.SmtpPort = smtpPort;
        cfg.ImapSsl = imapPort == 993;
        cfg.SmtpSsl = smtpPort is 465 or 994;
        if (!string.IsNullOrEmpty(MailCfgPassword.Password))
            cfg.SetPassword(MailCfgPassword.Password);
        cfg.Save();
        _mail.Reload();

        _mailBusy = true;
        SetMailStatus("正在测试 IMAP / SMTP…");
        try
        {
            await _mail.TestAsync();
            SetMailStatus("连接成功，已保存");
            MailCfgPassword.Password = "";
            ShowMailView(MailView.List);
            UpdateMailSettingsTabVisibility();
            await ReloadMailInboxAsync(force: true);
        }
        catch (Exception ex)
        {
            SetMailStatus("连接失败：" + ShortErr(ex));
        }
        finally { _mailBusy = false; }
    }

    private async void BtnMailRefresh_Click(object sender, RoutedEventArgs e)
        => await ReloadMailInboxAsync(force: true);

    private async Task ReloadMailInboxAsync(bool force)
    {
        if (_mailBusy) return;
        if (!_mail.Config.IsConfigured)
        {
            ShowMailView(MailView.Settings);
            LoadMailSettingsForm();
            return;
        }

        var cacheFresh = _mailCache.Count > 0
                         && DateTime.UtcNow - _mailCacheAtUtc < MailCacheTtl;

        // 有缓存：立刻渲染；未强制且未过期则不再联网
        if (_mailCache.Count > 0)
        {
            RenderMailList(_mailCache);
            SetMailStatus(FormatMailListStatus(_mailCache, fromCache: true));
            if (!force && cacheFresh)
            {
                _ = PollMailUnreadAsync(silent: true);
                return;
            }
        }

        if (!force && cacheFresh) return;

        _mailBusy = true;
        if (_mailCache.Count == 0)
            SetMailStatus("正在拉取收件箱…");
        else
            SetMailStatus(FormatMailListStatus(_mailCache, fromCache: true) + " · 更新中…");

        try
        {
            var items = await Task.Run(async () => await _mail.ListInboxAsync(40));
            _mailCache = items.ToList();
            _mailCacheAtUtc = DateTime.UtcNow;
            RenderMailList(_mailCache);
            SetMailStatus(FormatMailListStatus(_mailCache, fromCache: false));
            _ = PollMailUnreadAsync(silent: true);
        }
        catch (Exception ex)
        {
            if (_mailCache.Count > 0)
                SetMailStatus("更新失败，仍显示缓存 · " + ShortErr(ex));
            else
            {
                SetMailStatus("拉取失败：" + ShortErr(ex));
                MailList.Children.Clear();
                MailList.Children.Add(new TextBlock
                {
                    Text = "无法连接邮箱。可到「设置」检查账号，或点刷新重试。",
                    Foreground = (MediaBrush)FindResource("Muted"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(12, 24, 12, 24),
                });
            }
        }
        finally { _mailBusy = false; }
    }

    private static string FormatMailListStatus(IReadOnlyList<MailMessageSummary> items, bool fromCache)
    {
        var unread = items.Count(m => !m.IsRead);
        var head = fromCache ? "缓存" : "最新";
        return unread > 0
            ? $"{head} · {items.Count} 封 · 未读 {unread}"
            : $"{head} · {items.Count} 封";
    }

    private void RenderMailList(IReadOnlyList<MailMessageSummary> items)
    {
        MailList.Children.Clear();
        if (items.Count == 0)
        {
            MailList.Children.Add(new Grid
            {
                MinHeight = 120,
                Children =
                {
                    new TextBlock
                    {
                        Text = "收件箱是空的",
                        Foreground = (MediaBrush)FindResource("Muted"),
                        FontSize = 12.5,
                        HorizontalAlignment = WpfHorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            });
            return;
        }
        foreach (var m in items)
            MailList.Children.Add(BuildMailRow(m));
    }

    private async Task PollMailUnreadAsync(bool silent)
    {
        try
        {
            _mail.Reload();
            if (!_mail.Config.IsConfigured)
            {
                UpdateMailBadges(0);
                return;
            }
            var n = await Task.Run(async () => await _mail.CountUnreadAsync());
            UpdateMailBadges(n);
            if (!silent && _tab == TabMode.Mail && _mailView == MailView.List)
                SetMailStatus($"未读 {n} 封");
        }
        catch
        {
            if (!silent) SetMailStatus("未读数刷新失败");
        }
    }

    private void UpdateMailBadges(int unread)
    {
        _mailUnread = Math.Max(0, unread);
        var text = _mailUnread.ToString();
        var show = _mailUnread > 0 ? Visibility.Visible : Visibility.Collapsed;

        MailTabBadgeText.Text = text;
        MailTabBadge.Visibility = show;
        MailInboxBadgeText.Text = text;
        MailInboxBadge.Visibility = show;

        if (_tray != null)
            _tray.Text = _mailUnread > 0
                ? $"方老师的工作区 · 未读 {_mailUnread}"
                : "方老师的工作区";
    }

    private UIElement BuildMailRow(MailMessageSummary m)
    {
        var muted = (MediaBrush)FindResource("Muted");
        var fg = (MediaBrush)FindResource("Fg");
        var hover = (MediaBrush)FindResource("Hover");
        var line = (MediaBrush)FindResource("Line");
        // 未读：浅紫底；已读：透明
        MediaBrush idleBg = m.IsRead
            ? MediaBrushes.Transparent
            : new SolidColorBrush(MediaColor.FromArgb(0x1A, 0x00, 0x78, 0xD4));

        var row = new Border
        {
            Background = idleBg,
            BorderBrush = line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 10, 8, 10),
            Cursor = WpfCursors.Hand,
            Tag = m,
        };
        row.MouseEnter += (_, _) => row.Background = hover;
        row.MouseLeave += (_, _) => row.Background = idleBg;
        row.MouseLeftButtonUp += async (_, _) =>
        {
            if (row.Tag is MailMessageSummary s)
                await OpenMailAsync(s.Uid);
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = m.Subject,
            FontSize = 13,
            FontWeight = m.IsRead ? FontWeights.Normal : FontWeights.SemiBold,
            Foreground = fg,
            TextWrapping = TextWrapping.Wrap,
        });
        var meta = m.From;
        if (m.Received != null)
            meta += " · " + m.Received.Value.ToLocalTime().ToString("MM-dd HH:mm");
        stack.Children.Add(new TextBlock
        {
            Text = meta,
            FontSize = 11,
            Foreground = muted,
            Margin = new Thickness(0, 3, 0, 0),
        });
        if (!string.IsNullOrWhiteSpace(m.Preview))
        {
            stack.Children.Add(new TextBlock
            {
                Text = m.Preview,
                FontSize = 11.5,
                Foreground = muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }
        row.Child = stack;
        return row;
    }

    private async Task OpenMailAsync(MailKit.UniqueId uid)
    {
        if (_mailBusy) return;
        _mailBusy = true;
        SetMailStatus("正在打开…");
        try
        {
            var detail = await Task.Run(async () => await _mail.GetMessageAsync(uid));
            _mailCurrent = detail;
            MailReadSubject.Text = detail.Subject;
            var meta = $"发件人 {detail.From}";
            if (!string.IsNullOrEmpty(detail.To)) meta += $"\n收件人 {detail.To}";
            if (detail.Received != null)
                meta += $"\n时间 {detail.Received.Value.ToLocalTime():yyyy-MM-dd HH:mm}";
            MailReadMeta.Text = meta;
            RenderMailAttachments(detail);
            ShowMailView(MailView.Read);
            await ShowMailBodyAsync(detail);
            SetMailStatus(detail.Attachments.Count > 0 ? $"附件 {detail.Attachments.Count} 个" : null);
            // 缓存里标已读，避免返回列表又整页重拉
            for (var i = 0; i < _mailCache.Count; i++)
            {
                if (_mailCache[i].Uid != uid) continue;
                var old = _mailCache[i];
                if (!old.IsRead)
                {
                    _mailCache[i] = new MailMessageSummary
                    {
                        Id = old.Id,
                        Uid = old.Uid,
                        Subject = old.Subject,
                        From = old.From,
                        Preview = old.Preview,
                        Received = old.Received,
                        IsRead = true,
                    };
                    if (_mailUnread > 0) UpdateMailBadges(_mailUnread - 1);
                }
                break;
            }
            _ = PollMailUnreadAsync(silent: true);
        }
        catch (Exception ex)
        {
            SetMailStatus("打开失败：" + ShortErr(ex));
        }
        finally { _mailBusy = false; }
    }

    private void RenderMailAttachments(MailMessageDetail detail)
    {
        MailReadAttachments.Children.Clear();
        if (detail.Attachments.Count == 0) return;

        MailReadAttachments.Children.Add(new TextBlock
        {
            Text = $"附件（{detail.Attachments.Count}）",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 0, 6),
        });

        var muted = (MediaBrush)FindResource("Muted");
        foreach (var att in detail.Attachments)
        {
            var row = new Border
            {
                Background = new SolidColorBrush(MediaColor.FromArgb(0x14, 0x00, 0x78, 0xD4)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalAlignment = WpfHorizontalAlignment.Stretch,
            };

            // DockPanel 避开 Grid * 列在无限宽测量下变成 0 宽的问题
            var dock = new DockPanel { LastChildFill = true };
            var saveBtn = new WpfButton
            {
                Content = "下载",
                Style = (Style)FindResource("MailActionBtn"),
                Tag = att,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var captureUid = detail.Uid;
            saveBtn.Click += async (_, _) =>
            {
                if (saveBtn.Tag is not MailAttachmentInfo info) return;
                await DownloadMailAttachmentAsync(captureUid, info);
            };
            DockPanel.SetDock(saveBtn, Dock.Right);
            dock.Children.Add(saveBtn);

            var sizeText = att.Size > 0 ? FormatBytes(att.Size) : (string.IsNullOrEmpty(att.ContentType) ? "" : att.ContentType);
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(att.FileName) ? "未命名附件" : att.FileName,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top,
            });
            if (!string.IsNullOrEmpty(sizeText))
            {
                left.Children.Add(new TextBlock
                {
                    Text = sizeText,
                    FontSize = 11,
                    Foreground = muted,
                    Margin = new Thickness(0, 2, 0, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                });
            }
            dock.Children.Add(left);
            row.Child = dock;
            MailReadAttachments.Children.Add(row);
        }
    }

    private async Task DownloadMailAttachmentAsync(MailKit.UniqueId uid, MailAttachmentInfo att)
    {
        var safeName = SanitizeFileName(att.FileName);
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = safeName,
            Title = "保存附件",
            Filter = "所有文件|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        SetMailStatus("正在下载 " + att.FileName + "…");
        try
        {
            var path = dlg.FileName;
            await Task.Run(async () => await _mail.SaveAttachmentAsync(uid, att.Index, path));
            SetMailStatus("已保存：" + path);
            try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
            catch { /* 打开失败就算了 */ }
        }
        catch (Exception ex)
        {
            SetMailStatus("下载失败：" + ShortErr(ex));
        }
    }

    private static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "attachment";
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "attachment" : cleaned;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
        return (bytes / (1024.0 * 1024.0)).ToString("0.##") + " MB";
    }

    private async Task ShowMailBodyAsync(MailMessageDetail detail)
    {
        var html = detail.BodyHtml?.Trim() ?? "";
        if (!string.IsNullOrEmpty(html))
        {
            try
            {
                await EnsureMailHtmlAsync();
                MailReadHtml.Visibility = Visibility.Visible;
                MailReadPlainHost.Visibility = Visibility.Collapsed;
                MailReadHtml.NavigateToString(WrapMailHtml(html));
                // WebView2 加载后常把窗口撑宽，再锁回窄栏
                if (_expanded) PlaceExpanded();
                return;
            }
            catch (Exception ex)
            {
                // 未装 WebView2 Runtime 等：退回纯文本
                SetMailStatus("HTML 预览不可用，已显示纯文本 · " + ShortErr(ex));
            }
        }

        ClearMailHtmlView();
        MailReadHtml.Visibility = Visibility.Collapsed;
        MailReadPlainHost.Visibility = Visibility.Visible;
        MailReadBody.Text = string.IsNullOrWhiteSpace(detail.BodyText) ? "（无正文）" : detail.BodyText;
    }

    private async Task EnsureMailHtmlAsync()
    {
        if (_mailHtmlReady && MailReadHtml.CoreWebView2 != null) return;
        await MailReadHtml.EnsureCoreWebView2Async();
        if (MailReadHtml.CoreWebView2 == null)
            throw new InvalidOperationException("WebView2 初始化失败");

        var settings = MailReadHtml.CoreWebView2.Settings;
        settings.IsStatusBarEnabled = false;
        settings.AreDefaultContextMenusEnabled = true;
        settings.AreDevToolsEnabled = false;
        settings.IsZoomControlEnabled = true;

        if (!_mailHtmlHooksBound)
        {
            MailReadHtml.CoreWebView2.NavigationStarting += MailHtml_NavigationStarting;
            MailReadHtml.CoreWebView2.NewWindowRequested += MailHtml_NewWindowRequested;
            // 每封信：压掉横向滚动，并强制正文可选中复制（覆盖邮件里的 user-select:none）
            _ = MailReadHtml.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                """
                (function(){
                  function apply(){
                    var root=document.documentElement;
                    if(root){root.style.overflowX='hidden';root.style.maxWidth='100%';root.style.width='100%';}
                    if(document.body){document.body.style.overflowX='hidden';document.body.style.maxWidth='100%';document.body.style.width='100%';}
                    var s=document.getElementById('dw-mail-select');
                    if(!s){s=document.createElement('style');s.id='dw-mail-select';document.documentElement.appendChild(s);}
                    s.textContent='*{ -webkit-user-select:text!important; user-select:text!important; }';
                  }
                  apply();
                  document.addEventListener('DOMContentLoaded', apply);
                })();
                """);
            _mailHtmlHooksBound = true;
        }
        _mailHtmlReady = true;
    }

    private void MailHtml_NavigationStarting(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
    {
        var uri = e.Uri ?? "";
        if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            OpenExternalUrl(uri);
        }
    }

    private void MailHtml_NewWindowRequested(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternalUrl(e.Uri);
    }

    private static void OpenExternalUrl(string? uri)
    {
        if (!IsSafeExternalUri(uri)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });
        }
        catch { /* 系统打不开就算了 */ }
    }

    /// <summary>仅允许 http/https/mailto，拒绝 file: 与自定义协议进 ShellExecute。</summary>
    private static bool IsSafeExternalUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return false;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return false;
        return u.Scheme is "http" or "https" or "mailto";
    }

    private static bool IsHttpOrHttps(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return false;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return false;
        return u.Scheme is "http" or "https";
    }

    private static string WrapMailHtml(string bodyHtml)
    {
        // 强制铺满阅读区宽度，禁止左右滑动（覆盖邮件里写死的 600/700px）
        var hasHtmlTag = bodyHtml.Contains("<html", StringComparison.OrdinalIgnoreCase);
        const string headBits = """
            <meta charset="utf-8"/>
            <meta name="viewport" content="width=device-width,initial-scale=1"/>
            <style>
            html,body{margin:0!important;padding:0!important;width:100%!important;max-width:100%!important;overflow-x:hidden!important;background:#fff;color:#1c1b1f;}
            body{font-family:"Segoe UI","Microsoft YaHei UI",sans-serif;font-size:14px;line-height:1.55;padding:4px 2px 16px!important;word-wrap:break-word;overflow-wrap:anywhere;box-sizing:border-box;}
            *,*::before,*::after{box-sizing:border-box;max-width:100%!important;}
            html,body,div,p,span,td,th,li,a,font,center,table,tr,tbody,thead,section,article,main,header,footer,pre,code,label,h1,h2,h3,h4,h5,h6,blockquote{-webkit-user-select:text!important;user-select:text!important;}
            img,video,iframe,svg{max-width:100%!important;height:auto!important;}
            table{width:100%!important;max-width:100%!important;table-layout:fixed!important;}
            td,th{word-wrap:break-word!important;overflow-wrap:anywhere!important;}
            pre,code{white-space:pre-wrap!important;word-break:break-word!important;}
            a{color:#0078D4;}
            </style>
            """;

        if (hasHtmlTag)
        {
            if (bodyHtml.Contains("</head>", StringComparison.OrdinalIgnoreCase))
                return bodyHtml.Replace("</head>", headBits + "</head>", StringComparison.OrdinalIgnoreCase);
            if (bodyHtml.Contains("<body", StringComparison.OrdinalIgnoreCase))
                return bodyHtml.Replace("<body", "<head>" + headBits + "</head><body", StringComparison.OrdinalIgnoreCase);
            return "<head>" + headBits + "</head>" + bodyHtml;
        }

        return "<!DOCTYPE html><html><head>" + headBits + "</head><body>" + bodyHtml + "</body></html>";
    }

    private void ClearMailHtmlView()
    {
        try
        {
            if (_mailHtmlReady && MailReadHtml.CoreWebView2 != null)
                MailReadHtml.NavigateToString("<!DOCTYPE html><html><body></body></html>");
        }
        catch { /* 忽略 */ }
    }

    private void BtnMailCopyBody_Click(object sender, RoutedEventArgs e)
    {
        var text = "";
        if (_mailCurrent != null)
        {
            text = !string.IsNullOrWhiteSpace(_mailCurrent.BodyText)
                ? _mailCurrent.BodyText
                : StripMailHtmlApprox(_mailCurrent.BodyHtml);
        }
        if (string.IsNullOrWhiteSpace(text)
            && MailReadPlainHost.Visibility == Visibility.Visible)
            text = MailReadBody.Text ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            SetMailStatus("没有可复制的正文");
            return;
        }
        try
        {
            System.Windows.Clipboard.SetText(text.Trim());
            SetMailStatus("正文已复制");
        }
        catch (Exception ex)
        {
            SetMailStatus("复制失败 · " + ShortErr(ex));
        }
    }

    private static string StripMailHtmlApprox(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var t = System.Net.WebUtility.HtmlDecode(html);
        t = System.Text.RegularExpressions.Regex.Replace(t, "<[^>]+>", " ");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();
        return t;
    }

    private void BtnMailBack_Click(object sender, RoutedEventArgs e)
    {
        ClearMailHtmlView();
        // 返回只渲染缓存；ShowMailView 会处理宽→窄后的误收起
        ShowMailView(MailView.List);
        if (_mailCache.Count > 0)
        {
            RenderMailList(_mailCache);
            SetMailStatus(FormatMailListStatus(_mailCache, fromCache: true));
        }
        else
            _ = ReloadMailInboxAsync(force: false);
    }

    private async void BtnMailJunk_Click(object sender, RoutedEventArgs e)
    {
        if (_mailBusy || _mailCurrent == null) return;
        var detail = _mailCurrent;
        var wasUnread = _mailCache.FirstOrDefault(m => m.Uid == detail.Uid) is { IsRead: false };

        _mailBusy = true;
        SetMailStatus("正在移到垃圾邮件…");
        try
        {
            await Task.Run(async () => await _mail.MarkAsJunkAsync(detail.Uid));
            _mailCache.RemoveAll(m => m.Uid == detail.Uid);
            _mailCurrent = null;
            ClearMailHtmlView();
            if (wasUnread && _mailUnread > 0)
                UpdateMailBadges(_mailUnread - 1);
            ShowMailView(MailView.List);
            RenderMailList(_mailCache);
            SetMailStatus("已标记为垃圾邮件");
            _ = PollMailUnreadAsync(silent: true);
        }
        catch (Exception ex)
        {
            SetMailStatus("标记垃圾失败：" + ShortErr(ex));
        }
        finally { _mailBusy = false; }
    }

    private void BtnMailReply_Click(object sender, RoutedEventArgs e)
    {
        if (_mailCurrent == null) return;
        var addr = _mailCurrent.FromAddress;
        if (string.IsNullOrEmpty(addr)) addr = _mailCurrent.From;
        MailTo.Text = addr;
        var sub = _mailCurrent.Subject;
        MailSubject.Text = sub.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? sub : "Re: " + sub;
        MailBody.Text = "\n\n----- 原文 -----\n" + _mailCurrent.BodyText;
        ShowMailView(MailView.Compose);
        SetMailStatus("回复 · " + _mail.Config.Email);
        UpdateMailComposeHints();
        MailBody.Focus();
        MailBody.CaretIndex = 0;
    }

    private async void BtnMailSend_Click(object sender, RoutedEventArgs e)
    {
        if (_mailBusy) return;
        if (string.IsNullOrWhiteSpace(MailTo.Text))
        {
            SetMailStatus("请填写收件人");
            MailTo.Focus();
            return;
        }
        _mailBusy = true;
        SetMailStatus("正在发送…");
        try
        {
            var to = MailTo.Text;
            var subject = MailSubject.Text;
            var body = MailBody.Text;
            await Task.Run(async () => await _mail.SendAsync(to, subject, body));
            MailTo.Text = "";
            MailSubject.Text = "";
            MailBody.Text = "";
            ShowMailView(MailView.List);
            SetMailStatus("已发送");
            await ReloadMailInboxAsync(force: true);
        }
        catch (Exception ex)
        {
            SetMailStatus("发送失败：" + ShortErr(ex));
        }
        finally { _mailBusy = false; }
    }

    private static string ShortErr(Exception ex)
    {
        var msg = ex.Message;
        if (msg.Length > 120) msg = msg[..120] + "…";
        return msg;
    }

    private void StyleTabs()
    {
        void Paint(Border border, TextBlock text, bool on)
        {
            var accent = (MediaBrush)FindResource("Accent");
            var muted = (MediaBrush)FindResource("Muted");
            var fg = (MediaBrush)FindResource("Fg");
            border.BorderBrush = on ? accent : MediaBrushes.Transparent;
            text.Foreground = on ? fg : muted;
            text.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }

        Paint(TabNoteBorder, TabNoteText, _tab == TabMode.Note);
        Paint(TabTodoBorder, TabTodoText, _tab == TabMode.Todo);
        Paint(TabMailBorder, TabMailText, _tab == TabMode.Mail);
        Paint(TabCyberBorder, TabCyberText, _tab == TabMode.Cyber);
        Paint(TabAgentBorder, TabAgentText, _tab == TabMode.Agent);
        Paint(TabFileBorder, TabFileText, _tab == TabMode.File);

        NotePanel.Visibility = _tab == TabMode.Note ? Visibility.Visible : Visibility.Collapsed;
        TodoPanel.Visibility = _tab == TabMode.Todo ? Visibility.Visible : Visibility.Collapsed;
        MailPanel.Visibility = _tab == TabMode.Mail ? Visibility.Visible : Visibility.Collapsed;
        CyberNewsPanel.Visibility = _tab == TabMode.Cyber ? Visibility.Visible : Visibility.Collapsed;
        AgentPanel.Visibility = _tab == TabMode.Agent ? Visibility.Visible : Visibility.Collapsed;
        FilePanel.Visibility = _tab == TabMode.File ? Visibility.Visible : Visibility.Collapsed;
        // 日历只在「待办事项」显示
        CalendarHost.Visibility = _tab == TabMode.Todo ? Visibility.Visible : Visibility.Collapsed;
        if (_tab != TabMode.Cyber)
            HideCyberNewsDetail();
        if (_expanded) PlaceExpanded();
    }

    // ----- 网安热点（独立页签：上列表点开即消失；下详情可原文/收藏） -----

    private void StyleCyberModeTabs()
    {
        var accent = (MediaBrush)FindResource("Accent");
        var muted = (MediaBrush)FindResource("Muted");
        var fg = (MediaBrush)FindResource("Fg");
        var hot = _cyberListMode == CyberListMode.Hot;
        var fav = _cyberListMode == CyberListMode.Fav;
        var sources = _cyberListMode == CyberListMode.Sources;
        CyberModeHotBorder.BorderBrush = hot ? accent : MediaBrushes.Transparent;
        CyberModeHotText.Foreground = hot ? fg : muted;
        CyberModeHotText.FontWeight = hot ? FontWeights.SemiBold : FontWeights.Normal;
        CyberModeFavBorder.BorderBrush = fav ? accent : MediaBrushes.Transparent;
        CyberModeFavText.Foreground = fav ? fg : muted;
        CyberModeFavText.FontWeight = fav ? FontWeights.SemiBold : FontWeights.Normal;
        CyberModeSourceBorder.BorderBrush = sources ? accent : MediaBrushes.Transparent;
        CyberModeSourceText.Foreground = sources ? fg : muted;
        CyberModeSourceText.FontWeight = sources ? FontWeights.SemiBold : FontWeights.Normal;
        CyberNewsBody.Visibility = sources ? Visibility.Collapsed : Visibility.Visible;
        CyberSourceHost.Visibility = sources ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void CyberModeHot_Click(object sender, MouseButtonEventArgs e)
    {
        _cyberListMode = CyberListMode.Hot;
        StyleCyberModeTabs();
        HideCyberNewsDetail();
        await ShowCyberHotFromCacheThenMaybeFetchAsync();
        TryOpenFirstCyberNewsIfEmpty();
    }

    private void BtnCyberNewsRefresh_Click(object sender, RoutedEventArgs e) =>
        RefreshCyberNewsFromHotkey();

    /// <summary>← / 刷新按钮：热点强制拉取；收藏只重画列表。</summary>
    private void RefreshCyberNewsFromHotkey()
    {
        if (_tab != TabMode.Cyber) return;
        if (_cyberListMode == CyberListMode.Fav)
        {
            ShowCyberFavList();
            return;
        }
        _ = RefreshCyberNewsAsync(force: true);
    }

    private void CyberModeFav_Click(object sender, MouseButtonEventArgs e)
    {
        _cyberListMode = CyberListMode.Fav;
        StyleCyberModeTabs();
        HideCyberNewsDetail();
        ShowCyberFavList();
    }

    private void CyberModeSource_Click(object sender, MouseButtonEventArgs e)
    {
        _cyberListMode = CyberListMode.Sources;
        StyleCyberModeTabs();
        HideCyberNewsDetail();
        ShowCyberSourceList();
    }

    private void ShowCyberSourceList()
    {
        CyberNewsHint.Text = $"{_cyberNews.Sources.Count} 个源";
        CyberSourceStatus.Text = "名称可空，地址必填";
        CyberSourceList.Children.Clear();
        foreach (var feed in _cyberNews.Sources)
        {
            var capture = feed;
            var name = new TextBlock
            {
                Text = capture.Label,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var url = new TextBlock
            {
                Text = capture.Url,
                FontSize = 11,
                Foreground = (MediaBrush)FindResource("Muted"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
            };
            var text = new StackPanel();
            text.Children.Add(name);
            text.Children.Add(url);
            var del = new System.Windows.Controls.Button
            {
                Content = "删除",
                Style = (Style)FindResource("MailGhostBtn"),
                Height = 28,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            del.Click += (_, _) =>
            {
                _cyberNews.RemoveSource(capture.Url);
                ShowCyberSourceList();
                UpdateCyberBadge();
            };
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);
            row.Children.Add(text);
            CyberSourceList.Children.Add(row);
        }
    }

    private void BtnCyberSourceAdd_Click(object sender, RoutedEventArgs e)
    {
        var error = _cyberNews.TryAddSource(CyberSourceName.Text, CyberSourceUrl.Text);
        if (error != null)
        {
            CyberSourceStatus.Text = error;
            return;
        }
        CyberSourceName.Text = "";
        CyberSourceUrl.Text = "";
        CyberSourceStatus.Text = "已添加";
        ShowCyberSourceList();
        _ = RefreshCyberNewsAsync(force: true);
    }

    private void SetCyberHotHint(int? count = null)
    {
        var unread = count ?? _cyberNews.UnreadCount;
        var total = _cyberNews.CachedUnread.Count;
        var when = _cyberNews.LastFetchLocalText;
        if (total == 0)
        {
            CyberNewsHint.Text = string.IsNullOrEmpty(when)
                ? "暂无资讯（可点刷新）"
                : $"暂无资讯 · 上次拉取 {when}（可点刷新）";
            return;
        }
        CyberNewsHint.Text = unread > 0
            ? $"未读 {unread} / 共 {total}"
            : string.IsNullOrEmpty(when)
                ? $"共 {total} · 全部已读"
                : $"共 {total} · 全部已读 · 拉取 {when}";
    }

    private void SetCyberFavHint()
    {
        var n = _cyberNews.Favorites.Count;
        CyberNewsHint.Text = n == 0 ? "暂无收藏" : $"{n} 条";
    }

    private void ShowCyberFavList()
    {
        RenderCyberNewsList(_cyberNews.Favorites, fromHot: false);
        SetCyberFavHint();
    }

    private void ShowCyberHotList(IReadOnlyList<CyberNewsItem>? items = null)
    {
        var list = items ?? _cyberNews.CachedUnread;
        RenderCyberNewsList(list, fromHot: true);
        SetCyberHotHint(list.Count);
    }

    /// <summary>先画本地收件箱；无缓存或已到间隔再联网。</summary>
    private async Task ShowCyberHotFromCacheThenMaybeFetchAsync()
    {
        var cached = _cyberNews.CachedUnread;
        ShowCyberHotList(cached);
        UpdateCyberBadge();
        if (cached.Count == 0 || _cyberNews.IsFetchDue)
            await RefreshCyberNewsAsync(force: cached.Count == 0);
    }

    /// <summary>热点页无详情时，优先打开第一条未读；不立刻标已读。</summary>
    private void TryOpenFirstCyberNewsIfEmpty()
    {
        if (_tab != TabMode.Cyber || _cyberListMode != CyberListMode.Hot) return;
        if (_cyberNewsOpen != null) return;
        var list = _cyberNews.CachedUnread;
        if (list.Count == 0) return;
        var first = list.FirstOrDefault(i => !_cyberNews.IsRead(i)) ?? list[0];
        OpenCyberNewsDetail(first, dismissFromHot: false);
    }

    private async Task PollCyberNewsAsync(bool silent)
    {
        try
        {
            // 静默/整点遵守拉取间隔；手动刷新走 RefreshCyberNewsAsync(force: true)
            await _cyberNews.RefreshAsync(force: false);
            UpdateCyberBadge();
            // 停留在热点页时刷新列表（手动/整点共用）
            if (_tab == TabMode.Cyber && _cyberListMode == CyberListMode.Hot
                && (!silent || !_cyberNewsLoading))
                ShowCyberHotList();
        }
        catch
        {
            if (!silent) CyberNewsHint.Text = "拉取失败";
        }
    }

    private void UpdateCyberBadge()
    {
        var n = _cyberNews.UnreadCount;
        var text = n.ToString();
        CyberTabBadgeText.Text = text;
        CyberTabBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_tab == TabMode.Cyber && _cyberListMode == CyberListMode.Hot)
            SetCyberHotHint(n);
    }

    private async Task RefreshCyberNewsAsync(bool force = false)
    {
        if (_cyberNewsLoading) return;
        _cyberNewsLoading = true;
        CyberNewsHint.Text = "拉取中…";
        try
        {
            var items = await _cyberNews.RefreshAsync(force);
            UpdateCyberBadge();
            if (_cyberListMode == CyberListMode.Sources)
            {
                CyberNewsHint.Text = $"{_cyberNews.Sources.Count} 个源";
                return;
            }
            if (_cyberListMode != CyberListMode.Hot) return;
            ShowCyberHotList(items);
            // 进页/刷新后若还没打开详情，默认看第一条
            if (_tab == TabMode.Cyber)
                TryOpenFirstCyberNewsIfEmpty();
        }
        catch
        {
            CyberNewsHint.Text = "拉取失败";
            if (_cyberListMode == CyberListMode.Hot && CyberNewsList.Children.Count == 0)
                ShowCyberHotList(Array.Empty<CyberNewsItem>());
        }
        finally
        {
            _cyberNewsLoading = false;
        }
    }

    private void RenderCyberNewsList(IReadOnlyList<CyberNewsItem> items, bool fromHot)
    {
        CyberNewsList.Children.Clear();
        var muted = (MediaBrush)FindResource("Muted");
        var fg = (MediaBrush)FindResource("Fg");
        if (items.Count == 0)
        {
            CyberNewsList.Children.Add(new TextBlock
            {
                Text = fromHot ? "暂无热点，稍后再试" : "还没有收藏，在详情里点「收藏」",
                FontSize = 12,
                Foreground = muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 12, 8, 8),
            });
            return;
        }

        foreach (var item in items)
        {
            var capture = item;
            var read = fromHot && _cyberNews.IsRead(capture);
            var titleFg = read ? muted : fg;
            var row = new Border
            {
                Padding = new Thickness(8, 8, 8, 8),
                Cursor = WpfCursors.Hand,
                Background = MediaBrushes.Transparent,
                BorderBrush = (MediaBrush)FindResource("Line"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Opacity = read ? 0.72 : 1,
            };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = capture.Title,
                FontSize = 13,
                FontWeight = read ? FontWeights.Normal : FontWeights.SemiBold,
                Foreground = titleFg,
                TextWrapping = TextWrapping.Wrap,
            });
            var preview = FormatCyberListPreview(capture);
            if (!string.IsNullOrWhiteSpace(preview))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = preview,
                    FontSize = 11.5,
                    Foreground = muted,
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 36,
                    TextTrimming = TextTrimming.WordEllipsis,
                    Margin = new Thickness(0, 4, 0, 0),
                });
            }
            var meta = capture.Published?.ToLocalTime().ToString("MM-dd HH:mm");
            var metaText = string.IsNullOrEmpty(meta) ? capture.Source : $"{capture.Source} · {meta}";
            if (read) metaText += " · 已读";
            stack.Children.Add(new TextBlock
            {
                Text = metaText,
                FontSize = 10.5,
                Foreground = muted,
                Margin = new Thickness(0, 4, 0, 0),
            });
            row.Child = stack;
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                OpenCyberNewsDetail(capture, dismissFromHot: fromHot);
            };
            CyberNewsList.Children.Add(row);
        }
    }

    private static string FormatCyberListPreview(CyberNewsItem item)
    {
        var s = (item.Summary ?? "").Trim();
        if (s.Length == 0) return "";
        if (s.Length > 110) s = s[..110] + "…";
        return s;
    }

    private async void OpenCyberNewsDetail(CyberNewsItem item, bool dismissFromHot)
    {
        if (dismissFromHot)
        {
            _cyberNews.MarkRead(item.Id);
            UpdateCyberBadge();
            RenderCyberNewsList(_cyberNews.CachedUnread, fromHot: true);
        }

        var wasEmpty = _cyberNewsOpen == null;
        _cyberNewsOpen = item;
        var when = item.Published?.ToLocalTime().ToString("MM-dd HH:mm");
        CyberNewsDetailSource.Text = string.IsNullOrEmpty(when)
            ? $"{item.Source} · {item.Title}"
            : $"{item.Source} · {when} · {item.Title}";
        BtnCyberNewsFav.Visibility = Visibility.Visible;
        BtnCyberNewsWeixin.Visibility = Visibility.Visible;
        BtnCyberNewsWeixin.Content = "分享";
        UpdateCyberFavButton();
        await NavigateCyberWebAsync(item.Link);
        // 当前页展示时后台预载下一条，加快 → 切换
        PrefetchNextCyberNews();
        // 仅首次打开详情时 Activate；连切新闻时抢焦点会让 WebView 停在旧页
        if (wasEmpty)
        {
            ScheduleShellFocusReclaim(
                activate: true,
                when: () => _expanded && _tab == TabMode.Cyber);
        }
    }

    private void UpdateCyberFavButton()
    {
        if (_cyberNewsOpen == null) return;
        var fav = _cyberNews.IsFavorite(_cyberNewsOpen.Id);
        BtnCyberNewsFav.Content = fav ? "取消收藏" : "收藏";
    }

    private void HideCyberNewsDetail()
    {
        _cyberNewsOpen = null;
        _cyberWebTargetUrl = null;
        _cyberPrefetchUrl = null;
        _cyberPrefetchReady = false;
        _cyberWebNavEpoch++;
        _cyberPrefetchEpoch++;
        try { _cyberNavCts?.Cancel(); } catch { /* ignore */ }
        CyberNewsDetailSource.Text = "→ 下一条 · ← 刷新 · ↓/空格下翻 · ↑/Shift+空格上翻";
        BtnCyberNewsFav.Content = "收藏";
        BtnCyberNewsFav.Visibility = Visibility.Collapsed;
        BtnCyberNewsWeixin.Content = "分享";
        BtnCyberNewsWeixin.Visibility = Visibility.Collapsed;
        CyberNewsWebHint.Text = "→ 下一条 · ← 刷新 · ↓/空格下翻 · ↑上翻";
        CyberNewsWebHint.Visibility = Visibility.Visible;
        CyberNewsWeb.Visibility = Visibility.Collapsed;
        CyberNewsWebPrefetch.Visibility = Visibility.Collapsed;
        BlankCyberWeb(CyberNewsWeb);
        BlankCyberWeb(CyberNewsWebPrefetch);
    }

    private static void BlankCyberWeb(Microsoft.Web.WebView2.Wpf.WebView2 web)
    {
        try
        {
            if (web.CoreWebView2 == null) return;
            web.CoreWebView2.Stop();
            web.CoreWebView2.Navigate("about:blank");
        }
        catch { /* ignore */ }
    }

    private void BtnCyberNewsNext_Click(object sender, RoutedEventArgs e) => OpenCyberNewsNext();

    private Microsoft.Web.WebView2.Wpf.WebView2 CyberActiveWeb =>
        _cyberWebFlipped ? CyberNewsWebPrefetch : CyberNewsWeb;

    private Microsoft.Web.WebView2.Wpf.WebView2 CyberPrefetchWeb =>
        _cyberWebFlipped ? CyberNewsWeb : CyberNewsWebPrefetch;

    /// <summary>详情页翻页：↓/空格 下翻，↑/Shift+空格 上翻。走 WM_MOUSEWHEEL，滑动曲线与鼠标滚轮相同。</summary>
    private void ScrollCyberNewsPage(bool forward)
    {
        if (_tab != TabMode.Cyber || _cyberNewsOpen == null) return;
        var web = CyberActiveWeb;
        if (web.CoreWebView2 == null || web.Visibility != Visibility.Visible) return;

        // 按住方向键时系统会连发；略作间隔，避免一秒钟滚出十几格。
        // 间隔短于 Chromium 滚轮动画时长，连续格会接在同一段滑动上，而不是一跳一跳。
        var now = DateTime.UtcNow;
        if (now - _cyberScrollAtUtc < TimeSpan.FromMilliseconds(80)) return;
        _cyberScrollAtUtc = now;

        // 滚轮向前（离开用户）是正值，页面向下的内容上移；下翻用负值。
        var delta = forward ? -WheelDeltaUnit : WheelDeltaUnit;
        if (!TryPostMouseWheel(web, delta))
            SmoothScrollCyberFallback(web, forward);
    }

    /// <summary>把滚轮消息投到 WebView2 的渲染窗。命中点取页面中部，和光标停在正文上滚轮一样。</summary>
    private static bool TryPostMouseWheel(Microsoft.Web.WebView2.Wpf.WebView2 web, int delta)
    {
        try
        {
            var host = web.Handle;
            if (host == IntPtr.Zero) return false;
            var target = FindRenderWidgetHost(host);
            if (target == IntPtr.Zero) target = host;
            if (!GetClientRect(target, out var rc)) return false;
            var width = rc.Right - rc.Left;
            var height = rc.Bottom - rc.Top;
            if (width < 2 || height < 2) return false;

            var pt = new NativePoint { X = width / 2, Y = height / 2 };
            if (!ClientToScreen(target, ref pt)) return false;
            return PostMessage(target, WmMouseWheel, MakeWheelWParam(delta), MakeLParam(pt.X, pt.Y));
        }
        catch
        {
            return false;
        }
    }

    private static IntPtr FindRenderWidgetHost(IntPtr root)
    {
        IntPtr found = IntPtr.Zero;
        var bestArea = 0;
        EnumChildProc proc = (hwnd, _) =>
        {
            var sb = new System.Text.StringBuilder(256);
            if (GetClassName(hwnd, sb, sb.Capacity) > 0
                && sb.ToString().Contains("Chrome_RenderWidgetHostHWND", StringComparison.Ordinal)
                && GetClientRect(hwnd, out var rc))
            {
                var area = Math.Max(0, rc.Right - rc.Left) * Math.Max(0, rc.Bottom - rc.Top);
                if (area >= bestArea)
                {
                    bestArea = area;
                    found = hwnd;
                }
            }
            return true;
        };
        EnumChildWindows(root, proc, IntPtr.Zero);
        return found;
    }

    private static IntPtr MakeWheelWParam(int delta)
    {
        unchecked
        {
            return (IntPtr)((uint)(ushort)(short)delta << 16);
        }
    }

    private static IntPtr MakeLParam(int x, int y)
    {
        unchecked
        {
            var packed = ((uint)(ushort)(short)y << 16) | (ushort)(short)x;
            return (IntPtr)(int)packed;
        }
    }

    /// <summary>渲染窗还没建好时的退路：追着目标滚动，连续按键接在同一段上。</summary>
    private static void SmoothScrollCyberFallback(Microsoft.Web.WebView2.Wpf.WebView2 web, bool forward)
    {
        var step = forward ? 100 : -100;
        _ = web.CoreWebView2.ExecuteScriptAsync(
            "(function(dy){"
            + "var s=window.__dwWheel||(window.__dwWheel={raf:0,target:null,el:null});"
            + "var cx=Math.floor(innerWidth/2),cy=Math.floor(innerHeight/2);"
            + "function find(x,y){var n=document.elementFromPoint(x,y),h=0;"
            + "while(n&&h++<25){if(n===document.body||n===document.documentElement)break;"
            + "var oy=getComputedStyle(n).overflowY;"
            + "if((oy==='auto'||oy==='scroll'||oy==='overlay')&&n.scrollHeight>n.clientHeight+4)return n;"
            + "n=n.parentElement;}"
            + "return document.scrollingElement||document.documentElement;}"
            + "var el=find(cx,cy);if(!el)return;"
            + "if(s.el!==el){s.el=el;s.target=null;}"
            + "var max=Math.max(0,el.scrollHeight-el.clientHeight);"
            + "var base=s.target==null?el.scrollTop:s.target;"
            + "s.target=Math.max(0,Math.min(max,base+dy));"
            + "if(s.raf)return;"
            + "var last=performance.now();"
            + "function frame(now){"
            + "var dt=Math.min(48,now-last);last=now;"
            + "var live=Math.max(0,el.scrollHeight-el.clientHeight);"
            + "s.target=Math.max(0,Math.min(live,s.target));"
            + "var left=s.target-el.scrollTop;"
            + "if(Math.abs(left)<0.6){el.scrollTop=s.target;s.raf=0;s.target=null;return;}"
            + "el.scrollTop+=left*(1-Math.exp(-dt/80));"
            + "s.raf=requestAnimationFrame(frame);}"
            + "s.raf=requestAnimationFrame(frame);"
            + "})(" + step + ")");
    }

    /// <summary>当前列表里「下一条」候选（热点=未读头；收藏=下一项）。</summary>
    private CyberNewsItem? PeekNextCyberNews()
    {
        if (_cyberListMode == CyberListMode.Hot)
            return _cyberNews.CachedUnread.Count > 0 ? _cyberNews.CachedUnread[0] : null;

        var favs = _cyberNews.Favorites;
        if (favs.Count == 0) return null;
        if (_cyberNewsOpen == null) return favs[0];
        for (var i = 0; i < favs.Count - 1; i++)
        {
            if (string.Equals(favs[i].Id, _cyberNewsOpen.Id, StringComparison.OrdinalIgnoreCase))
                return favs[i + 1];
        }
        return null;
    }

    /// <summary>后台 WebView 预载下一条链接（始终 Collapsed）。</summary>
    private async void PrefetchNextCyberNews()
    {
        var next = PeekNextCyberNews();
        var link = next?.Link?.Trim();
        if (!IsHttpOrHttps(link))
        {
            _cyberPrefetchUrl = null;
            _cyberPrefetchReady = false;
            BlankCyberWeb(CyberPrefetchWeb);
            return;
        }

        var safeLink = link!;
        if (!string.IsNullOrEmpty(_cyberPrefetchUrl)
            && UrlsLooselyMatch(_cyberPrefetchUrl, safeLink)
            && _cyberPrefetchReady)
            return;

        var epoch = ++_cyberPrefetchEpoch;
        _cyberPrefetchUrl = safeLink;
        _cyberPrefetchReady = false;

        try
        {
            await EnsureOneCyberWebAsync(CyberNewsWebPrefetch, isPrefetch: true);
            if (epoch != _cyberPrefetchEpoch) return;
            var web = CyberPrefetchWeb;
            if (web.CoreWebView2 == null) return;
            web.Visibility = Visibility.Collapsed;
            try { web.CoreWebView2.Stop(); } catch { /* ignore */ }
            var cur = web.CoreWebView2.Source ?? "";
            if (UrlsLooselyMatch(cur, safeLink))
            {
                _cyberPrefetchReady = true;
                return;
            }
            web.CoreWebView2.Navigate(safeLink);
        }
        catch { /* ignore */ }
    }

    /// <summary>无详情 → 列表第一条；已有详情 → 下一条。</summary>
    private void OpenCyberNewsNext()
    {
        if (_tab != TabMode.Cyber) return;

        if (_cyberListMode == CyberListMode.Hot)
        {
            // → ：当前条标已读，优先下一条未读；没有未读则按列表顺序下一条
            if (_cyberNewsOpen != null)
                _cyberNews.MarkRead(_cyberNewsOpen.Id);
            var list = _cyberNews.CachedUnread;
            if (list.Count == 0)
            {
                UpdateCyberBadge();
                ShowCyberHotList();
                CyberNewsHint.Text = "没有更多资讯";
                return;
            }
            CyberNewsItem? next = null;
            if (_cyberNewsOpen != null)
            {
                var hotIdx = -1;
                for (var i = 0; i < list.Count; i++)
                {
                    if (string.Equals(list[i].Id, _cyberNewsOpen.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        hotIdx = i;
                        break;
                    }
                }
                for (var i = hotIdx + 1; i < list.Count; i++)
                {
                    if (!_cyberNews.IsRead(list[i]))
                    {
                        next = list[i];
                        break;
                    }
                }
                if (next == null && hotIdx >= 0 && hotIdx + 1 < list.Count)
                    next = list[hotIdx + 1];
            }
            next ??= list.FirstOrDefault(i => !_cyberNews.IsRead(i)) ?? list[0];
            OpenCyberNewsDetail(next, dismissFromHot: false);
            UpdateCyberBadge();
            ShowCyberHotList();
            return;
        }

        var favs = _cyberNews.Favorites;
        if (favs.Count == 0)
        {
            CyberNewsHint.Text = "暂无收藏";
            return;
        }

        // 无详情：打开第一条
        if (_cyberNewsOpen == null)
        {
            OpenCyberNewsDetail(favs[0], dismissFromHot: false);
            return;
        }

        var idx = -1;
        for (var i = 0; i < favs.Count; i++)
        {
            if (string.Equals(favs[i].Id, _cyberNewsOpen.Id, StringComparison.OrdinalIgnoreCase))
            {
                idx = i;
                break;
            }
        }

        if (idx < 0)
        {
            OpenCyberNewsDetail(favs[0], dismissFromHot: false);
            return;
        }

        if (idx + 1 >= favs.Count)
        {
            CyberNewsHint.Text = "已是最后一条";
            return;
        }

        OpenCyberNewsDetail(favs[idx + 1], dismissFromHot: false);
    }

    /// <summary>只初始化主阅读 WebView；预载实例由 PrefetchNextCyberNews 单独拉起。</summary>
    private async Task EnsureCyberWebAsync()
    {
        await EnsureOneCyberWebAsync(CyberNewsWeb, isPrefetch: false);
    }

    private async Task EnsureOneCyberWebAsync(Microsoft.Web.WebView2.Wpf.WebView2 web, bool isPrefetch)
    {
        if (isPrefetch)
        {
            if (_cyberPrefetchWebReady && web.CoreWebView2 != null) return;
        }
        else if (_cyberWebReady && web.CoreWebView2 != null) return;

        try
        {
            await web.EnsureCoreWebView2Async();
            if (web.CoreWebView2 == null) return;
            var settings = web.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = !isPrefetch;
            settings.IsStatusBarEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsZoomControlEnabled = !isPrefetch;
            web.ZoomFactor = 1.0;
            web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (IsHttpOrHttps(e.Uri))
                    OpenExternalUrl(e.Uri);
            };

            var hooked = isPrefetch ? _cyberPrefetchNavHooked : _cyberWebNavHooked;
            if (!hooked)
            {
                if (isPrefetch) _cyberPrefetchNavHooked = true;
                else _cyberWebNavHooked = true;
                web.CoreWebView2.NavigationCompleted += CyberWeb_NavigationCompleted;
                _ = web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(CyberPageCleaner.Script);
                try
                {
                    await web.CoreWebView2.CallDevToolsProtocolMethodAsync(
                        "Emulation.clearDeviceMetricsOverride", "{}");
                }
                catch { /* ignore */ }
            }

            if (isPrefetch) _cyberPrefetchWebReady = true;
            else _cyberWebReady = true;
        }
        catch
        {
            if (isPrefetch) _cyberPrefetchWebReady = false;
            else _cyberWebReady = false;
        }
    }

    private void CyberWeb_NavigationCompleted(
        object? sender,
        Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        try
        {
            if (e.IsSuccess && sender is Microsoft.Web.WebView2.Core.CoreWebView2 core)
                _ = core.ExecuteScriptAsync("window.__dwSweep&&window.__dwSweep()");

            // 预载完成：只记就绪，不改 UI
            if (ReferenceEquals(sender, CyberPrefetchWeb.CoreWebView2))
            {
                if (e.IsSuccess
                    && !string.IsNullOrEmpty(_cyberPrefetchUrl)
                    && UrlsLooselyMatch(CyberPrefetchWeb.CoreWebView2?.Source ?? "", _cyberPrefetchUrl))
                    _cyberPrefetchReady = true;
                return;
            }

            if (!ReferenceEquals(sender, CyberActiveWeb.CoreWebView2)) return;
            if (_tab != TabMode.Cyber || _cyberNewsOpen == null) return;
            var src = CyberActiveWeb.CoreWebView2?.Source;
            if (string.IsNullOrEmpty(src) || src.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                return;
            if (!string.IsNullOrEmpty(_cyberWebTargetUrl)
                && !UrlsLooselyMatch(src, _cyberWebTargetUrl))
                return;

            if (e.IsSuccess)
            {
                CyberNewsWebHint.Visibility = Visibility.Collapsed;
                CyberActiveWeb.Visibility = Visibility.Visible;
                CyberPrefetchWeb.Visibility = Visibility.Collapsed;
            }
            else
            {
                CyberNewsWebHint.Text = "页面加载失败，可点「浏览器」打开";
                CyberNewsWebHint.Visibility = Visibility.Visible;
            }
        }
        catch { /* ignore */ }
    }

    private static bool UrlsLooselyMatch(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var ua = new Uri(a);
            var ub = new Uri(b);
            return string.Equals(ua.GetLeftPart(UriPartial.Path).TrimEnd('/'),
                       ub.GetLeftPart(UriPartial.Path).TrimEnd('/'),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task NavigateCyberWebAsync(string? url)
    {
        if (!IsHttpOrHttps(url))
        {
            CyberNewsWebHint.Text = "无有效链接，可点「浏览器」或换下一条";
            CyberNewsWebHint.Visibility = Visibility.Visible;
            return;
        }

        // 连按切换：合并到最后一条
        _cyberNavCts?.Cancel();
        var cts = new CancellationTokenSource();
        _cyberNavCts = cts;
        var epoch = ++_cyberWebNavEpoch;
        var target = url!.Trim();
        _cyberWebTargetUrl = target;

        try
        {
            await Task.Delay(50, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (epoch != _cyberWebNavEpoch) return;

        await EnsureCyberWebAsync();
        if (epoch != _cyberWebNavEpoch) return;

        // 预载命中：对调前后台 WebView，免重新 Navigate
        if (!string.IsNullOrEmpty(_cyberPrefetchUrl)
            && UrlsLooselyMatch(_cyberPrefetchUrl, target)
            && CyberPrefetchWeb.CoreWebView2 != null)
        {
            _cyberWebFlipped = !_cyberWebFlipped;
            _cyberPrefetchUrl = null;
            _cyberPrefetchReady = false;
            CyberPrefetchWeb.Visibility = Visibility.Collapsed;
            CyberActiveWeb.Visibility = Visibility.Visible;
            CyberActiveWeb.ZoomFactor = 1.0;

            var src = CyberActiveWeb.CoreWebView2?.Source ?? "";
            if (UrlsLooselyMatch(src, target)
                && !src.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            {
                CyberNewsWebHint.Visibility = Visibility.Collapsed;
            }
            else
            {
                CyberNewsWebHint.Text = "加载中…";
                CyberNewsWebHint.Visibility = Visibility.Visible;
            }
            return;
        }

        var active = CyberActiveWeb;
        if (active.CoreWebView2 == null)
        {
            CyberNewsWebHint.Text = "WebView 不可用，请点「浏览器」打开";
            CyberNewsWebHint.Visibility = Visibility.Visible;
            active.Visibility = Visibility.Collapsed;
            return;
        }

        CyberNewsWebHint.Text = "加载中…";
        CyberNewsWebHint.Visibility = Visibility.Visible;
        active.Visibility = Visibility.Visible;
        CyberPrefetchWeb.Visibility = Visibility.Collapsed;
        try
        {
            var core = active.CoreWebView2;
            active.ZoomFactor = 1.0;
            try { core.Stop(); } catch { /* ignore */ }

            var cur = core.Source ?? "";
            if (UrlsLooselyMatch(cur, target))
                core.Reload();
            else
                core.Navigate(target);
        }
        catch
        {
            if (epoch != _cyberWebNavEpoch) return;
            CyberNewsWebHint.Text = "页面加载失败，可点「浏览器」打开";
            CyberNewsWebHint.Visibility = Visibility.Visible;
        }
    }

    private void BtnCyberNewsOpen_Click(object sender, RoutedEventArgs e)
    {
        var link = _cyberNewsOpen?.Link;
        if (!IsHttpOrHttps(link)) return;
        OpenExternalUrl(link);
    }

    /// <summary>
    /// 分享到微信：先复制标题+链接，再发 Ctrl+Alt+W（微信唤醒热键），会话里粘贴即可。
    /// </summary>
    private void BtnCyberNewsWeixin_Click(object sender, RoutedEventArgs e)
    {
        var item = _cyberNewsOpen;
        if (item == null) return;
        var link = (item.Link ?? "").Trim();
        if (string.IsNullOrWhiteSpace(link))
        {
            BtnCyberNewsWeixin.Content = "无链接";
            return;
        }

        var text = string.IsNullOrWhiteSpace(item.Source)
            ? $"{item.Title}\n{link}"
            : $"【{item.Source}】{item.Title}\n{link}";
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch
        {
            BtnCyberNewsWeixin.Content = "复制失败";
            return;
        }

        // 稍后再发热键，避免本窗还占着键盘焦点时被吃掉
        _ = Dispatcher.BeginInvoke(() =>
        {
            SendCtrlAltW();
            BtnCyberNewsWeixin.Content = "已复制";
            var reset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            reset.Tick += (_, _) =>
            {
                reset.Stop();
                if (BtnCyberNewsWeixin.Visibility == Visibility.Visible)
                    BtnCyberNewsWeixin.Content = "分享";
            };
            reset.Start();
        }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>模拟 Ctrl+Alt+W，走微信桌面端已登记的唤醒快捷键。</summary>
    private static void SendCtrlAltW()
    {
        try
        {
            keybd_event((byte)VkControl, 0, 0, UIntPtr.Zero);
            keybd_event((byte)VkMenu, 0, 0, UIntPtr.Zero);
            keybd_event(VkW, 0, 0, UIntPtr.Zero);
            keybd_event(VkW, 0, KeyeventfKeyup, UIntPtr.Zero);
            keybd_event((byte)VkMenu, 0, KeyeventfKeyup, UIntPtr.Zero);
            keybd_event((byte)VkControl, 0, KeyeventfKeyup, UIntPtr.Zero);
        }
        catch { /* ignore */ }
    }

    private void BtnCyberNewsFav_Click(object sender, RoutedEventArgs e)
    {
        if (_cyberNewsOpen == null) return;
        if (_cyberNews.IsFavorite(_cyberNewsOpen.Id))
        {
            _cyberNews.RemoveFavorite(_cyberNewsOpen.Id);
            if (_cyberListMode == CyberListMode.Fav)
            {
                ShowCyberFavList();
                HideCyberNewsDetail();
                return;
            }
        }
        else
        {
            _cyberNews.AddFavorite(_cyberNewsOpen);
        }
        UpdateCyberFavButton();
    }

    // ----- 大模型（cursor agent · WebView 时间线） -----

    private Task EnsureAgentTimelineAsync()
    {
        if (_agentTimelineReady && AgentTimeline.CoreWebView2 != null)
            return Task.CompletedTask;
        return _agentTimelineInitTask ??= InitAgentTimelineCoreAsync();
    }

    private async Task InitAgentTimelineCoreAsync()
    {
        try
        {
            await AgentTimeline.EnsureCoreWebView2Async();
            if (AgentTimeline.CoreWebView2 == null)
                throw new InvalidOperationException("WebView2 初始化失败");

            var settings = AgentTimeline.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsZoomControlEnabled = false;

            var htmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "agent-timeline.html");
            if (!File.Exists(htmlPath))
                throw new FileNotFoundException("缺少 agent-timeline.html", htmlPath);

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnNav(object? s, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
            {
                AgentTimeline.CoreWebView2.NavigationCompleted -= OnNav;
                if (e.IsSuccess) tcs.TrySetResult();
                else tcs.TrySetException(new InvalidOperationException("时间线页面加载失败"));
            }
            AgentTimeline.CoreWebView2.NavigationCompleted += OnNav;
            AgentTimeline.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
            _agentTimelineReady = true;
        }
        catch (Exception ex)
        {
            AgentStatus.Text = "时间线加载失败：" + ShortErr(ex);
            _agentTimelineReady = false;
            _agentTimelineInitTask = null;
        }
    }

    private void PostAgentUi(object payload)
    {
        if (!_agentTimelineReady || AgentTimeline.CoreWebView2 == null) return;
        try
        {
            AgentTimeline.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
        }
        catch { /* ignore */ }
    }

    private static string AgentMarkdownToHtml(string markdown) =>
        Markdown.ToHtml(markdown ?? "", AgentMdPipeline);

    private async Task EnsureAgentWelcomeAsync()
    {
        if (_agentWelcomeShown)
        {
            UpdateAgentStatusLine();
            return;
        }

        AgentStatus.Text = "检测 agent…";
        var ws = CursorAgentClient.WorkspaceDir;
        // 探测 WSL / 扫 skills 都放到后台，避免卡死 UI
        var resolveTask = _agent.EnsureResolvedAsync();
        var skillsTask = Task.Run(AgentSkillCatalog.List);
        await Task.WhenAll(resolveTask, skillsTask);

        var path = _agent.ResolveAgentPath();
        var skills = (await skillsTask)
            .Select(s => new { id = s.Id, summary = s.Summary })
            .ToList();
        PostAgentUi(new
        {
            op = "welcome",
            ready = path != null,
            workspace = ws,
            skills,
        });
        _agentWelcomeShown = true;
        UpdateAgentStatusLine();
    }

    private void UpdateAgentStatusLine()
    {
        if (!_agent.IsResolved)
        {
            AgentStatus.Text = "检测 agent…";
            return;
        }
        var path = _agent.ResolveAgentPath();
        var kind = _agent.RuntimeKind;
        if (path == null)
        {
            AgentStatus.Text = "未检测到 agent";
            return;
        }
        var runtime = kind == AgentRuntimeKind.Windows ? "Win" : "WSL";
        AgentStatus.Text = $"{runtime} · Agent · {path}";
    }

    private async void BtnAgentNew_Click(object sender, RoutedEventArgs e)
    {
        if (_agentBusy) BtnAgentStop_Click(sender, e);
        _agentHasSession = false;
        _agentStreaming = false;
        _agentStreamText = "";
        _agentWelcomeShown = false;
        await EnsureAgentTimelineAsync();
        PostAgentUi(new { op = "clear" });
        _agent.Refresh();
        await EnsureAgentWelcomeAsync();
        FocusAgentInputSoon();
    }

    private void BtnAgentStop_Click(object sender, RoutedEventArgs e)
    {
        try { _agentCts?.Cancel(); } catch { /* ignore */ }
        AgentStatus.Text = "已停止";
    }

    private void AgentInput_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            var tb = AgentInput;
            var i = tb.CaretIndex;
            tb.Text = tb.Text.Insert(i, Environment.NewLine);
            tb.CaretIndex = i + Environment.NewLine.Length;
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            BtnAgentSend_Click(sender, new RoutedEventArgs());
        }
    }

    private async void BtnAgentSend_Click(object sender, RoutedEventArgs e)
    {
        if (_agentBusy) return;
        var prompt = (AgentInput.Text ?? "").Trim();
        if (prompt.Length == 0) return;

        await EnsureAgentTimelineAsync();
        if (!_agentTimelineReady) return;

        AgentInput.Text = "";
        AppendAgentUserBubble(prompt);

        _agentBusy = true;
        BtnAgentStop.Visibility = Visibility.Visible;
        AgentStatus.Text = "思考中…";
        _agentCts = new CancellationTokenSource();
        var continueSession = _agentHasSession;
        _agentStreaming = false;
        _agentStreamText = "";

        try
        {
            var final = await Task.Run(async () =>
                await _agent.ChatAsync(
                    prompt,
                    continueSession,
                    AgentChatMode.Agent,
                    delta => Dispatcher.BeginInvoke(() => AppendAgentDelta(delta)),
                    onLog: null,
                    onTool: info => Dispatcher.BeginInvoke(() => ApplyAgentToolCall(info)),
                    _agentCts.Token));

            SealAgentStreamBubble(fallbackIfEmpty: final);
            _agentHasSession = true;
            UpdateAgentStatusLine();
        }
        catch (OperationCanceledException)
        {
            SealAgentStreamBubble(fallbackIfEmpty: "（已取消）");
            AgentStatus.Text = "已取消";
        }
        catch (Exception ex)
        {
            if (_agentStreaming && !string.IsNullOrWhiteSpace(_agentStreamText))
            {
                SealAgentStreamBubble();
                AppendAgentSystem("出错：" + ShortErr(ex));
            }
            else
            {
                SealAgentStreamBubble(fallbackIfEmpty: "出错：" + ShortErr(ex));
            }
            AgentStatus.Text = "失败";
        }
        finally
        {
            _agentBusy = false;
            _agentStreaming = false;
            _agentStreamText = "";
            BtnAgentStop.Visibility = Visibility.Collapsed;
            _agentCts?.Dispose();
            _agentCts = null;
            if (AgentStatus.Text is "已取消" or "失败") { /* keep */ }
            else UpdateAgentStatusLine();
            PostAgentUi(new { op = "scroll" });
        }
    }

    private void AppendAgentDelta(string delta)
    {
        _agentStreaming = true;
        _agentStreamText += delta;
        PostAgentUi(new { op = "delta", text = delta });
    }

    private void SealAgentStreamBubble(string? fallbackIfEmpty = null)
    {
        var raw = _agentStreamText;
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (!string.IsNullOrWhiteSpace(fallbackIfEmpty))
                raw = fallbackIfEmpty!;
            else
            {
                if (_agentStreaming) PostAgentUi(new { op = "dropStream" });
                _agentStreaming = false;
                _agentStreamText = "";
                return;
            }
        }

        PostAgentUi(new { op = "seal", html = AgentMarkdownToHtml(raw) });
        _agentStreaming = false;
        _agentStreamText = "";
    }

    private void ApplyAgentToolCall(AgentToolCallInfo info)
    {
        SealAgentStreamBubble();
        var label = AgentToolLabel(info.Kind);
        var state = !info.Completed ? "running" : (info.Failed ? "fail" : "done");
        PostAgentUi(new
        {
            op = "tool",
            callId = info.CallId,
            kind = info.Kind,
            label,
            detail = info.Detail ?? "",
            state,
            summary = info.ResultSummary ?? "",
        });
        AgentStatus.Text = info.Completed
            ? (info.Failed ? label + " 失败" : label + " 完成")
            : label + (string.IsNullOrEmpty(info.Detail) ? "…" : " " + TruncateUi(info.Detail, 28));
    }

    private static string AgentToolLabel(string kind) => kind.ToLowerInvariant() switch
    {
        "read" => "读取",
        "write" => "写入",
        "edit" => "编辑",
        "delete" => "删除",
        "shell" => "Shell",
        "grep" => "搜索",
        "glob" => "匹配文件",
        "ls" or "list" => "列出目录",
        "todo" => "待办",
        "semsearch" or "semanticsearch" => "语义搜索",
        _ => string.IsNullOrEmpty(kind) ? "工具" : kind,
    };

    private static string TruncateUi(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");

    private void AppendAgentSystem(string text) =>
        PostAgentUi(new { op = "system", text });

    private void AppendAgentUserBubble(string text) =>
        PostAgentUi(new { op = "user", text });

    private void ShowDoneHit_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ShowDoneCheck.IsChecked = ShowDoneCheck.IsChecked != true;
        PaintShowDoneBox();
    }

    private void PaintShowDoneBox()
    {
        var on = ShowDoneCheck.IsChecked == true;
        var accent = (MediaBrush)FindResource("Accent");
        var muted = (MediaBrush)FindResource("Muted");
        ShowDoneBox.Background = on ? accent : MediaBrushes.Transparent;
        ShowDoneBox.BorderBrush = on ? accent : muted;
        ShowDoneBox.Child = on
            ? new TextBlock
            {
                Text = "✓",
                Foreground = MediaBrushes.White,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = WpfHorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -1, 0, 0),
            }
            : null;
    }

    private void ShowDone_Changed(object sender, RoutedEventArgs e)
    {
        _showDone = ShowDoneCheck.IsChecked == true;
        PaintShowDoneBox();
        if (_tab == TabMode.Todo) ReloadTodos();
    }

    // ----- todos -----
    private void TodoInput_TextChanged(object sender, TextChangedEventArgs e) => UpdateTodoPlaceholder();

    private void UpdateTodoPlaceholder()
    {
        TodoPlaceholder.Text = _filterByDay
            ? $"新任务（{_selected:MM-dd}），回车添加"
            : "新任务，回车添加";
        TodoPlaceholder.Visibility = string.IsNullOrEmpty(TodoInput.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateTodoSectionHeader();
    }

    private void UpdateTodoSectionHeader()
    {
        if (_filterByDay && _selected.Date == DateTime.Today)
            TodoSectionTitle.Text = "今日待办";
        else if (_filterByDay)
            TodoSectionTitle.Text = $"{_selected:MM-dd} 待办";
        else
            TodoSectionTitle.Text = "全部待办";
        TodoSectionTitle.ToolTip = _filterByDay ? "点击查看全部待办" : null;
    }

    private void BtnAddTodo_Click(object sender, RoutedEventArgs e) => CommitTodoInput();

    private void TodoInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitTodoInput();
        e.Handled = true;
    }

    private void CommitTodoInput()
    {
        var title = TodoInput.Text.Trim();
        if (title.Length == 0) return;
        _store.AddTodo(title, _selected.ToString("yyyy-MM-dd"));
        TodoInput.Clear();
        UpdateTodoPlaceholder();
        RebuildCalendar();
        ReloadTodos();
        _ = TrySyncSilentAsync();
    }

    private void ReloadTodos()
    {
        CancelTodoDrag();
        TodoList.Children.Clear();
        UpdateTodoSectionHeader();
        var day = _selected.ToString("yyyy-MM-dd");
        var items = _store.ListTodos(true)
            .Where(t =>
            {
                if (_filterByDay) return t.DueDate == day;
                return true;
            })
            .Where(t => _showDone || !t.Done)
            .ToList();

        // 进度：当天（或全部）已完成/总数
        var scope = _store.ListTodos(true).Where(t => !_filterByDay || t.DueDate == day).ToList();
        int done = scope.Count(t => t.Done);
        TodoProgress.Text = scope.Count == 0 ? "" : $"{done}/{scope.Count}";

        if (items.Count == 0)
        {
            // 空状态居中，避免贴左或莫名缩进
            TodoList.Children.Add(new Grid
            {
                MinHeight = 96,
                Children =
                {
                    new TextBlock
                    {
                        Text = _filterByDay ? "这天还没有任务" : "暂无任务，在下方添加",
                        Foreground = (MediaBrush)FindResource("Muted"),
                        FontSize = 12.5,
                        HorizontalAlignment = WpfHorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextAlignment = TextAlignment.Center,
                    },
                },
            });
            return;
        }

        foreach (var todo in items)
            TodoList.Children.Add(BuildTodoRow(todo));
    }

    private UIElement BuildTodoRow(TodoItem todo)
    {
        // 两行结构：第1行 勾选+标题+删除（垂直居中）；第2行 副标题缩进在标题列
        var accent = (MediaBrush)FindResource("Accent");
        var muted = (MediaBrush)FindResource("Muted");
        var fg = (MediaBrush)FindResource("Fg");
        var hover = (MediaBrush)FindResource("Hover");
        var line = (MediaBrush)FindResource("Line");

        var row = new Border
        {
            Background = MediaBrushes.Transparent,
            BorderBrush = line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 10, 8, 10),
            Tag = todo,
            Cursor = todo.Done ? WpfCursors.Arrow : WpfCursors.SizeAll,
            ToolTip = todo.Done ? null : "拖拽调整顺序",
        };
        row.MouseEnter += (_, _) =>
        {
            if (!_todoDragging) row.Background = hover;
        };
        row.MouseLeave += (_, _) =>
        {
            if (!_todoDragging || !ReferenceEquals(row, _todoDragRow))
                row.Background = MediaBrushes.Transparent;
        };
        if (!todo.Done)
        {
            row.PreviewMouseLeftButtonDown += TodoRow_PreviewMouseLeftButtonDown;
            row.PreviewMouseMove += TodoRow_PreviewMouseMove;
            row.PreviewMouseLeftButtonUp += TodoRow_PreviewMouseLeftButtonUp;
            row.LostMouseCapture += (_, _) =>
            {
                if (_todoDragging) FinishTodoDrag();
                else CancelTodoDrag();
            };
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var check = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1.5),
            BorderBrush = todo.Done ? accent : new SolidColorBrush(MediaColor.FromArgb(0x70, 0, 0, 0)),
            Background = todo.Done ? accent : MediaBrushes.Transparent,
            Cursor = WpfCursors.Hand,
            Tag = "nodrag:" + todo.Id,
            VerticalAlignment = VerticalAlignment.Center,
            Child = todo.Done
                ? new TextBlock
                {
                    Text = "✓",
                    Foreground = MediaBrushes.White,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = WpfHorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, -1, 0, 0),
                }
                : null,
        };
        check.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_todoDragging) return;
            _store.ToggleTodo(todo.Id);
            RebuildCalendar();
            ReloadTodos();
            _ = TrySyncSilentAsync();
        };
        Grid.SetColumn(check, 0);
        Grid.SetRow(check, 0);

        var titleHost = new Grid
        {
            Margin = new Thickness(10, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var title = new TextBlock
        {
            Text = todo.Title,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = todo.Done ? muted : fg,
            TextDecorations = todo.Done ? TextDecorations.Strikethrough : null,
            Cursor = todo.Done ? WpfCursors.Hand : WpfCursors.SizeAll,
            ToolTip = todo.Done ? "单击编辑" : "拖拽排序 · 单击编辑",
        };
        title.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_todoDragging || _todoDragSuppressClick) return;
            BeginInlineTodoEdit(todo, titleHost, title);
        };
        titleHost.Children.Add(title);
        Grid.SetColumn(titleHost, 1);
        Grid.SetRow(titleHost, 0);

        var actions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = "nodrag",
        };
        var edit = new TextBlock
        {
            Text = "编辑",
            FontSize = 11.5,
            Foreground = accent,
            Cursor = WpfCursors.Hand,
            Padding = new Thickness(6, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "编辑标题与备注",
            Tag = "nodrag",
        };
        edit.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_todoDragging) return;
            OpenTodoInNotes(todo.Id);
        };
        var del = new TextBlock
        {
            Text = "×",
            FontSize = 15,
            Foreground = muted,
            Opacity = 0.45,
            Cursor = WpfCursors.Hand,
            Padding = new Thickness(4, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = "nodrag:" + todo.Id,
            ToolTip = "删除",
        };
        del.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_todoDragging) return;
            _store.DeleteTodo(todo.Id);
            RebuildCalendar();
            ReloadTodos();
            _ = TrySyncSilentAsync();
        };
        actions.Children.Add(edit);
        actions.Children.Add(del);
        Grid.SetColumn(actions, 2);
        Grid.SetRow(actions, 0);

        grid.Children.Add(check);
        grid.Children.Add(titleHost);
        grid.Children.Add(actions);

        var subParts = new List<string>();
        if (!string.IsNullOrEmpty(todo.DueDate)) subParts.Add(todo.DueDate);
        if (!string.IsNullOrWhiteSpace(todo.Body))
        {
            var preview = todo.Body.Replace("\r\n", " ").Replace("\n", " ").Trim();
            if (preview.Length > 32) preview = preview[..32] + "…";
            subParts.Add(preview);
        }
        if (subParts.Count > 0)
        {
            var sub = new TextBlock
            {
                Text = string.Join(" · ", subParts),
                FontSize = 11,
                Foreground = muted,
                Margin = new Thickness(10, 4, 8, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(sub, 1);
            Grid.SetRow(sub, 1);
            grid.Children.Add(sub);
        }

        row.Child = grid;
        return row;
    }

    private static bool IsTodoNoDragSource(DependencyObject? src, DependencyObject row)
    {
        while (src != null && !ReferenceEquals(src, row))
        {
            if (src is FrameworkElement fe)
            {
                var tag = fe.Tag as string;
                if (tag != null && tag.StartsWith("nodrag", StringComparison.Ordinal))
                    return true;
            }
            src = VisualTreeHelper.GetParent(src)
                  ?? (src is FrameworkElement f ? f.Parent as DependencyObject : null);
        }
        return false;
    }

    private void TodoRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border row || row.Tag is not TodoItem todo || todo.Done) return;
        if (IsTodoNoDragSource(e.OriginalSource as DependencyObject, row)) return;
        _todoDragId = todo.Id;
        _todoDragStart = e.GetPosition(TodoList);
        _todoDragging = false;
        _todoDragSuppressClick = false;
        _todoDragRow = row;
        _todoInsertIndex = -1;
        row.CaptureMouse();
    }

    private void TodoRow_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_todoDragId == null || e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not Border row || !ReferenceEquals(row, _todoDragRow)) return;

        var pos = e.GetPosition(TodoList);
        if (!_todoDragging)
        {
            if (Math.Abs(pos.Y - _todoDragStart.Y) < TodoDragThreshold
                && Math.Abs(pos.X - _todoDragStart.X) < TodoDragThreshold)
                return;
            _todoDragging = true;
            _todoDragSuppressClick = true;
            row.Opacity = 0.55;
            row.Background = (MediaBrush)FindResource("AccentSoft");
            EnsureTodoInsertLine();
        }

        UpdateTodoInsertIndicator(pos.Y);
    }

    private void TodoRow_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_todoDragId == null) return;
        if (_todoDragging) FinishTodoDrag();
        else CancelTodoDrag();
    }

    private void EnsureTodoInsertLine()
    {
        if (_todoInsertLine != null) return;
        _todoInsertLine = new Border
        {
            Height = 2,
            Margin = new Thickness(8, 0, 8, 0),
            Background = (MediaBrush)FindResource("Accent"),
            IsHitTestVisible = false,
            CornerRadius = new CornerRadius(1),
        };
    }

    private List<Border> GetTodoDragRows()
    {
        var rows = new List<Border>();
        foreach (UIElement child in TodoList.Children)
        {
            if (child is Border b && b.Tag is TodoItem t && !t.Done
                && !ReferenceEquals(b, _todoInsertLine))
                rows.Add(b);
        }
        return rows;
    }

    private void UpdateTodoInsertIndicator(double yInList)
    {
        var rows = GetTodoDragRows();
        if (rows.Count == 0 || _todoInsertLine == null) return;

        // 插入下标：落在行上半 → 该行前；下半 → 该行后
        var insert = rows.Count;
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var top = r.TranslatePoint(new System.Windows.Point(0, 0), TodoList).Y;
            var mid = top + r.ActualHeight / 2;
            if (yInList < mid)
            {
                insert = i;
                break;
            }
        }

        // 拖的是自己：插在自己前后等价于不动
        var from = rows.FindIndex(r => ReferenceEquals(r, _todoDragRow));
        if (from >= 0 && (insert == from || insert == from + 1))
        {
            _todoInsertIndex = from;
            if (TodoList.Children.Contains(_todoInsertLine))
                TodoList.Children.Remove(_todoInsertLine);
            return;
        }

        _todoInsertIndex = insert;
        if (TodoList.Children.Contains(_todoInsertLine))
            TodoList.Children.Remove(_todoInsertLine);

        // 插入线放在目标行之前；若目标是「末尾」则加在最后一条未完成行之后
        var visualIndex = 0;
        for (var i = 0; i < TodoList.Children.Count; i++)
        {
            if (ReferenceEquals(TodoList.Children[i], _todoInsertLine)) continue;
            if (TodoList.Children[i] is not Border b || b.Tag is not TodoItem t || t.Done)
                continue;
            if (visualIndex == insert)
            {
                TodoList.Children.Insert(i, _todoInsertLine);
                return;
            }
            visualIndex++;
        }
        TodoList.Children.Add(_todoInsertLine);
    }

    private void FinishTodoDrag()
    {
        var dragId = _todoDragId;
        var insert = _todoInsertIndex;
        var dragRow = _todoDragRow;
        if (dragRow != null)
        {
            dragRow.Opacity = 1;
            dragRow.Background = MediaBrushes.Transparent;
            if (dragRow.IsMouseCaptured) dragRow.ReleaseMouseCapture();
        }
        if (_todoInsertLine != null && TodoList.Children.Contains(_todoInsertLine))
            TodoList.Children.Remove(_todoInsertLine);

        _todoDragId = null;
        _todoDragging = false;
        _todoDragRow = null;
        _todoInsertIndex = -1;

        if (string.IsNullOrEmpty(dragId) || insert < 0)
        {
            _todoDragSuppressClick = false;
            return;
        }

        ApplyTodoReorder(dragId, insert);
        _ = Dispatcher.BeginInvoke(() => { _todoDragSuppressClick = false; }, DispatcherPriority.Input);
    }

    private void CancelTodoDrag()
    {
        if (_todoDragRow != null)
        {
            _todoDragRow.Opacity = 1;
            _todoDragRow.Background = MediaBrushes.Transparent;
            if (_todoDragRow.IsMouseCaptured) _todoDragRow.ReleaseMouseCapture();
        }
        if (_todoInsertLine != null && TodoList.Children.Contains(_todoInsertLine))
            TodoList.Children.Remove(_todoInsertLine);
        _todoDragId = null;
        _todoDragging = false;
        _todoDragRow = null;
        _todoInsertIndex = -1;
        _todoDragSuppressClick = false;
    }

    /// <summary>
    /// 把 dragId 挪到可见未完成列表的 insert 下标，再合并进全局未完成顺序并写库。
    /// </summary>
    private void ApplyTodoReorder(string dragId, int insertAmongVisible)
    {
        var day = _selected.ToString("yyyy-MM-dd");
        var visible = _store.ListTodos(includeDone: false)
            .Where(t => !_filterByDay || t.DueDate == day)
            .Select(t => t.Id)
            .ToList();
        if (visible.Count == 0) return;

        var from = visible.IndexOf(dragId);
        if (from < 0) return;

        // insert 是「含拖拽行」时的目标缝；先去掉自身再插入
        var adjusted = insertAmongVisible;
        if (adjusted > from) adjusted--;
        adjusted = Math.Clamp(adjusted, 0, visible.Count - 1);

        visible.RemoveAt(from);
        visible.Insert(adjusted, dragId);

        // 全局未完成：可见子集按新顺序填回原坑位
        var global = _store.ListTodos(includeDone: false).Select(t => t.Id).ToList();
        var visibleSet = visible.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var q = new Queue<string>(visible);
        var merged = global
            .Select(id => visibleSet.Contains(id) ? q.Dequeue() : id)
            .ToList();
        if (q.Count > 0) return; // 数据不一致则放弃

        // 顺序未变则不写库
        if (merged.SequenceEqual(global, StringComparer.OrdinalIgnoreCase)) return;

        _store.ReorderTodos(merged);
        ReloadTodos();
    }

    /// <summary>行内改标题：回车保存，Esc 取消。</summary>
    private void BeginInlineTodoEdit(TodoItem todo, Grid titleHost, TextBlock titleBlock)
    {
        titleHost.Children.Clear();
        var box = new System.Windows.Controls.TextBox
        {
            Text = todo.Title,
            FontSize = 13.5,
            Style = (Style)FindResource("PlainInput"),
            MinHeight = 26,
            VerticalContentAlignment = VerticalAlignment.Center,
            Tag = todo.Id,
        };
        var cancelled = false;
        void EndInline()
        {
            _inlineEscCancel = null;
            _inputFocused = false;
        }
        void Commit()
        {
            if (cancelled || !titleHost.Children.Contains(box)) return;
            EndInline();
            var newTitle = (box.Text ?? "").Trim();
            if (newTitle.Length == 0)
            {
                titleHost.Children.Clear();
                titleHost.Children.Add(titleBlock);
                return;
            }
            if (newTitle != todo.Title)
            {
                _store.UpdateTodoContent(todo.Id, newTitle, todo.Body ?? "");
                RebuildCalendar();
                ReloadTodos();
                _ = TrySyncSilentAsync();
                return;
            }
            titleHost.Children.Clear();
            titleBlock.Text = newTitle;
            titleHost.Children.Add(titleBlock);
        }
        void Cancel()
        {
            if (!titleHost.Children.Contains(box)) return;
            cancelled = true;
            EndInline();
            titleHost.Children.Clear();
            titleHost.Children.Add(titleBlock);
        }
        _inlineEscCancel = Cancel;
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Commit();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Cancel();
            }
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            // 失焦保存；Esc 取消时 cancelled=true，跳过
            if (!cancelled && titleHost.Children.Contains(box))
                Commit();
        };
        titleHost.Children.Add(box);
        box.Focus();
        box.SelectAll();
        _inputFocused = true;
    }

    private void OpenTodoInNotes(string todoId)
    {
        FlushNoteSave();
        var todo = _store.GetTodo(todoId);
        if (todo == null) return;

        _editingTodoId = todoId;
        _activeNoteId = null;
        _tab = TabMode.Note;
        StyleTabs();
        UpdateNoteChrome();

        DetachNoteWatcher();
        _noteLoading = true;
        NoteEdit.Text = NoteFiles.JoinTitleBody(todo.Title, todo.Body);
        _noteLoading = false;
        if (_notePreviewMode) _ = SetNotePreviewModeAsync(false);
        NoteEdit.Focus();
        NoteEdit.CaretIndex = NoteEdit.Text?.Length ?? 0;

        // 仅落盘，不监视；点「记事本」时再监视
        _notes.Write(TodoEditFileId(todoId), NoteEdit.Text ?? "");
    }

    private static string TodoEditFileId(string todoId) => "todoedit_" + todoId;

    private void ExitTodoEditMode()
    {
        if (_editingTodoId == null) return;
        _editingTodoId = null;
        DetachNoteWatcher();
    }

    // ----- notes -----
    private void UpdateNoteChrome()
    {
        if (_editingTodoId != null)
        {
            BtnAddTodoFromNote.Content = "完成编辑";
            NoteListHost.Visibility = Visibility.Collapsed;
        }
        else
        {
            BtnAddTodoFromNote.Content = "加入待办";
            NoteListHost.Visibility = Visibility.Visible;
        }
    }

    private void EnsureActiveNote()
    {
        var notes = _store.ListNotes();
        if (notes.Count == 0)
        {
            var n = _store.CreateNote("未命名笔记", "");
            _notes.Write(n.Id, "");
            _activeNoteId = n.Id;
            return;
        }
        if (_activeNoteId == null || notes.All(n => n.Id != _activeNoteId))
            _activeNoteId = notes[0].Id;
    }

    private void BtnNewNote_Click(object sender, RoutedEventArgs e)
    {
        FlushNoteSave();
        ExitTodoEditMode();
        var n = _store.CreateNote("未命名笔记", "");
        _notes.Write(n.Id, "");
        _activeNoteId = n.Id;
        UpdateNoteChrome();
        ReloadNoteList();
        LoadActiveNote();
    }

    private void BtnAddTodoFromNote_Click(object sender, RoutedEventArgs e)
    {
        FlushNoteSave();

        if (_editingTodoId != null)
        {
            // 完成编辑 → 回待办
            ExitTodoEditMode();
            _tab = TabMode.Todo;
            StyleTabs();
            EnsureActiveNote();
            RebuildCalendar();
            ReloadTodos();
            return;
        }

        var text = NoteEdit.Text ?? "";
        var (title, body) = NoteFiles.SplitTitleBody(text);
        if (string.IsNullOrWhiteSpace(title)) return;

        var today = DateTime.Today.ToString("yyyy-MM-dd");
        _store.AddTodo(title, today, body);
        _selected = DateTime.Today;
        _year = _selected.Year;
        _month = _selected.Month;
        _filterByDay = true;
        _tab = TabMode.Todo;
        StyleTabs();
        RebuildCalendar();
        UpdateDueHint();
        UpdateTodoPlaceholder();
        ReloadTodos();
        _ = TrySyncSilentAsync();
    }

    private void ReloadNoteList()
    {
        NoteList.Children.Clear();
        if (_editingTodoId != null) return;

        foreach (var note in _store.ListNotes())
        {
            var on = note.Id == _activeNoteId;
            var chip = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 6, 2),
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = on
                    ? new SolidColorBrush(MediaColor.FromArgb(0x2E, 0x00, 0x78, 0xD4))
                    : new SolidColorBrush(MediaColor.FromArgb(0x12, 0x00, 0x78, 0xD4)),
                Cursor = WpfCursors.Hand,
                Tag = note.Id,
            };
            var title = string.IsNullOrWhiteSpace(note.Title) ? "未命名笔记" : note.Title;
            var sp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            sp.Children.Add(new TextBlock
            {
                Text = title.Length > 10 ? title[..10] + "…" : title,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
                Foreground = on ? (MediaBrush)FindResource("Accent") : (MediaBrush)FindResource("Fg"),
                FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
            });
            var del = new TextBlock
            {
                Text = " ×",
                FontSize = 11,
                Foreground = (MediaBrush)FindResource("Muted"),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.55,
            };
            del.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                DeleteNote(note.Id);
            };
            sp.Children.Add(del);
            chip.Child = sp;
            chip.MouseLeftButtonUp += (_, _) =>
            {
                FlushNoteSave();
                ExitTodoEditMode();
                _activeNoteId = note.Id;
                UpdateNoteChrome();
                ReloadNoteList();
                LoadActiveNote();
            };
            NoteList.Children.Add(chip);
        }
    }

    private void DeleteNote(string id)
    {
        FlushNoteSave();
        _store.SoftDeleteNote(id);
        _notes.Delete(id);
        if (_activeNoteId == id) _activeNoteId = null;
        EnsureActiveNote();
        ReloadNoteList();
        LoadActiveNote();
    }

    private void LoadActiveNote()
    {
        if (_editingTodoId != null) return;
        if (_activeNoteId == null)
        {
            _noteLoading = true;
            NoteEdit.Text = "";
            _noteLoading = false;
            if (_notePreviewMode) _ = RefreshNotePreviewAsync();
            return;
        }

        var note = _store.GetNote(_activeNoteId);
        var fromFile = _notes.Read(_activeNoteId);
        var body = fromFile.Length > 0 ? fromFile : (note?.Body ?? "");
        if (fromFile.Length == 0 && body.Length > 0)
            _notes.Write(_activeNoteId, body);

        DetachNoteWatcher();
        _noteLoading = true;
        NoteEdit.Text = body;
        _noteLoading = false;
        if (_notePreviewMode) _ = RefreshNotePreviewAsync();
    }

    private void BtnNotepad_Click(object sender, RoutedEventArgs e)
    {
        FlushNoteSave();
        if (_editingTodoId != null)
        {
            var fid = TodoEditFileId(_editingTodoId);
            _notes.OpenInNotepad(fid, NoteEdit.Text);
            AttachNoteWatcher(fid); // 仅记事本外部编辑时监视
            Collapse();
            return;
        }
        if (_activeNoteId == null) EnsureActiveNote();
        if (_activeNoteId == null) return;
        _notes.OpenInNotepad(_activeNoteId, NoteEdit.Text);
        AttachNoteWatcher(_activeNoteId);
        // 与 Esc 一样收起，把前台让给记事本
        Collapse();
    }

    private void NoteEdit_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_noteLoading) return;
        _noteSaveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _noteSaveTimer.Stop();
        _noteSaveTimer.Tick -= NoteSaveTick;
        _noteSaveTimer.Tick += NoteSaveTick;
        _noteSaveTimer.Start();
    }

    private void NoteModeEdit_Click(object sender, MouseButtonEventArgs e)
    {
        _ = SetNotePreviewModeAsync(false);
    }

    private void NoteModePreview_Click(object sender, MouseButtonEventArgs e)
    {
        _ = SetNotePreviewModeAsync(true);
    }

    private void StyleNoteModeTabs()
    {
        var accent = (MediaBrush)FindResource("Accent");
        var muted = (MediaBrush)FindResource("Muted");
        var fg = (MediaBrush)FindResource("Fg");
        NoteModeEdit.BorderBrush = _notePreviewMode ? MediaBrushes.Transparent : accent;
        NoteModePreview.BorderBrush = _notePreviewMode ? accent : MediaBrushes.Transparent;
        NoteModeEditText.Foreground = _notePreviewMode ? muted : fg;
        NoteModePreviewText.Foreground = _notePreviewMode ? fg : muted;
    }

    private async Task SetNotePreviewModeAsync(bool preview)
    {
        if (preview == _notePreviewMode && preview)
        {
            await RefreshNotePreviewAsync();
            return;
        }
        _notePreviewMode = preview;
        StyleNoteModeTabs();
        if (!preview)
        {
            NotePreview.Visibility = Visibility.Collapsed;
            NoteEdit.Visibility = Visibility.Visible;
            NoteEdit.Focus();
            return;
        }

        FlushNoteSave();
        NoteEdit.Visibility = Visibility.Collapsed;
        _inputFocused = false;
        try
        {
            await RefreshNotePreviewAsync();
            NotePreview.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _notePreviewMode = false;
            StyleNoteModeTabs();
            NoteEdit.Visibility = Visibility.Visible;
            NotePreview.Visibility = Visibility.Collapsed;
            NoteEdit.Focus();
            System.Diagnostics.Debug.WriteLine("Note preview: " + ShortErr(ex));
        }
    }

    private async Task RefreshNotePreviewAsync()
    {
        await EnsureNotePreviewAsync();
        if (NotePreview.CoreWebView2 == null)
            throw new InvalidOperationException("WebView2 初始化失败");
        var md = NoteEdit.Text ?? "";
        var body = string.IsNullOrWhiteSpace(md)
            ? "<p style=\"color:#8A8A8A\">空笔记</p>"
            : AgentMarkdownToHtml(md);
        NotePreview.NavigateToString(WrapNoteMarkdownHtml(body));
    }

    private async Task EnsureNotePreviewAsync()
    {
        if (_notePreviewReady && NotePreview.CoreWebView2 != null) return;
        await NotePreview.EnsureCoreWebView2Async();
        if (NotePreview.CoreWebView2 == null)
            throw new InvalidOperationException("WebView2 初始化失败");
        var settings = NotePreview.CoreWebView2.Settings;
        settings.IsStatusBarEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsZoomControlEnabled = false;
        if (!_notePreviewHooksBound)
        {
            _notePreviewHooksBound = true;
            NotePreview.CoreWebView2.NavigationStarting += NotePreview_NavigationStarting;
            NotePreview.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (IsHttpOrHttps(e.Uri))
                    OpenExternalUrl(e.Uri);
            };
        }
        _notePreviewReady = true;
    }

    private void NotePreview_NavigationStarting(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
    {
        var uri = e.Uri ?? "";
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(uri))
            return;
        e.Cancel = true;
        if (IsHttpOrHttps(uri))
            OpenExternalUrl(uri);
    }

    private static string WrapNoteMarkdownHtml(string bodyHtml) =>
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"/>" +
        "<style>" +
        "html,body{margin:0;padding:0;background:#fff;}" +
        "body{font-family:\"Microsoft YaHei UI\",Segoe UI,sans-serif;font-size:15px;" +
        "line-height:1.55;color:#1a1a1a;padding:2px 4px;}" +
        "h1,h2,h3,h4{margin:0.8em 0 0.35em;line-height:1.3;font-weight:600;}" +
        "h1{font-size:1.35em;}h2{font-size:1.2em;}h3{font-size:1.08em;}" +
        "p{margin:0 0 0.65em;}ul,ol{margin:0.2em 0 0.65em;padding-left:1.35em;}" +
        "li{margin:0.15em 0;}blockquote{margin:0.4em 0;padding:0.2em 0.8em;" +
        "border-left:3px solid #D0D0D5;color:#555;}" +
        "code{font-family:Cascadia Mono,Consolas,monospace;font-size:0.92em;" +
        "background:#F3F3F5;padding:0.1em 0.35em;border-radius:4px;}" +
        "pre{background:#F3F3F5;padding:10px 12px;border-radius:8px;overflow:auto;}" +
        "pre code{background:transparent;padding:0;}" +
        "a{color:#2563EB;text-decoration:none;}a:hover{text-decoration:underline;}" +
        "table{border-collapse:collapse;margin:0.5em 0;}th,td{border:1px solid #E5E5EA;" +
        "padding:4px 8px;}th{background:#F7F7F9;}" +
        "hr{border:none;border-top:1px solid #E5E5EA;margin:1em 0;}" +
        "img{max-width:100%;}" +
        "</style></head><body>" + bodyHtml + "</body></html>";

    private void NoteSaveTick(object? sender, EventArgs e)
    {
        _noteSaveTimer?.Stop();
        // 编辑中只写库/文件，不刷新标题 chip，避免列表重排导致正文跳动
        FlushNoteSave();
    }

    /// <summary>离开当前笔记时再刷新标题 chip（编辑过程中不调用）。</summary>
    private void RefreshNoteChipTitlesIfNeeded()
    {
        if (_editingTodoId != null || _tab != TabMode.Note) return;
        ReloadNoteList();
    }

    private void FlushNoteSave()
    {
        _noteSaveTimer?.Stop();
        if (_noteLoading) return;
        var body = NoteEdit.Text ?? "";

        if (_editingTodoId != null)
        {
            var (title, noteBody) = NoteFiles.SplitTitleBody(body);
            _noteWriting = true;
            try
            {
                _notes.Write(TodoEditFileId(_editingTodoId), body);
                _store.UpdateTodoContent(_editingTodoId, title, noteBody);
            }
            finally { _noteWriting = false; }
            // 不在按键保存时同步，避免 RebuildCalendar 导致编辑区跳动
            return;
        }

        if (_activeNoteId == null) return;
        var titleN = NoteFiles.TitleFromBody(body);
        _noteWriting = true;
        try
        {
            _notes.Write(_activeNoteId, body);
            _store.SaveNote(_activeNoteId, titleN, body);
        }
        finally { _noteWriting = false; }
    }

    private void DetachNoteWatcher()
    {
        _noteWatcher?.Dispose();
        _noteWatcher = null;
        _noteReloadDebounce?.Stop();
    }

    private void AttachNoteWatcher(string fileId)
    {
        DetachNoteWatcher();
        _noteWatcher = _notes.Watch(fileId, () =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_noteWriting) return;
                // 正在侧栏里打字时忽略外部回写
                if (NoteEdit.IsKeyboardFocused) return;
                var expect = _editingTodoId != null
                    ? TodoEditFileId(_editingTodoId)
                    : _activeNoteId;
                if (expect != fileId) return;

                _noteReloadDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                _noteReloadDebounce.Stop();
                _noteReloadDebounce.Tick -= NoteReloadTick;
                _noteReloadDebounce.Tick += NoteReloadTick;
                _noteReloadDebounce.Tag = fileId;
                _noteReloadDebounce.Start();
            });
        });
    }

    private void NoteReloadTick(object? sender, EventArgs e)
    {
        _noteReloadDebounce?.Stop();
        if (NoteEdit.IsKeyboardFocused) return;
        var fileId = _noteReloadDebounce?.Tag as string;
        if (fileId == null) return;
        var text = _notes.Read(fileId);
        var cur = NoteEdit.Text ?? "";
        if (NormalizeNewlines(text) == NormalizeNewlines(cur)) return;

        var caret = NoteEdit.CaretIndex;
        _noteLoading = true;
        NoteEdit.Text = text;
        _noteLoading = false;
        NoteEdit.CaretIndex = Math.Clamp(caret, 0, NoteEdit.Text?.Length ?? 0);
        if (_notePreviewMode) _ = RefreshNotePreviewAsync();
        FlushNoteSave();
    }

    private static string NormalizeNewlines(string s) =>
        (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    // ----- Microsoft sync -----
    private void UpdateSyncHint()
    {
        if (!_sync.HasClientId)
        {
            SyncHint.Text = "未配置微软同步：托盘 → 打开同步配置";
            BtnSync.ToolTip = "同步配置";
            return;
        }
        if (_sync.IsSignedIn)
        {
            SyncHint.Text = $"已登录 {_sync.AccountLabel}";
            BtnSync.ToolTip = _syncing ? "同步中…" : $"同步 · {_sync.AccountLabel}";
        }
        else
        {
            SyncHint.Text = "已配置 ClientId，点击菜单可登录同步";
            BtnSync.ToolTip = _syncing ? "同步中…" : "登录并同步";
        }
    }

    private async void BtnSync_Click(object sender, RoutedEventArgs e)
    {
        if (!_sync.HasClientId)
        {
            OpenSyncConfig();
            return;
        }
        await RunSyncAsync(true);
    }

    private void OpenSyncConfig()
    {
        var cfg = MsSyncConfig.Load();
        if (string.IsNullOrWhiteSpace(cfg.ClientId))
            cfg.Save();

        var helpPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeskWidgets",
            "微软同步说明.txt");
        File.WriteAllText(helpPath, MsSyncConfig.SetupHelpText + "\n\n配置文件：\n" + MsSyncConfig.ConfigPath);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = "\"" + MsSyncConfig.ConfigPath + "\"",
                UseShellExecute = true,
            });
            Process.Start(new ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = "\"" + helpPath + "\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            System.Windows.MessageBox.Show(
                MsSyncConfig.SetupHelpText + "\n\n" + MsSyncConfig.ConfigPath,
                "微软同步配置");
        }

        _sync.ReloadConfig();
        UpdateSyncHint();
    }

    private async Task RunSyncAsync(bool showErrors)
    {
        if (_syncing) return;
        _sync.ReloadConfig();
        if (!_sync.HasClientId)
        {
            if (showErrors) OpenSyncConfig();
            return;
        }

        _syncing = true;
        UpdateSyncHint();
        try
        {
            await _sync.SignInAsync();
            await _sync.SyncAsync();
            RebuildCalendar();
            if (_tab == TabMode.Todo) ReloadTodos();
            SyncHint.Text = showErrors
                ? $"同步完成 · {_sync.AccountLabel} · {DateTime.Now:HH:mm:ss}"
                : SyncHint.Text;
            if (!showErrors) UpdateSyncHint();
        }
        catch (Exception ex)
        {
            SyncHint.Text = "同步失败：" + ex.Message;
            if (showErrors)
                System.Windows.MessageBox.Show(ex.Message, "同步失败");
        }
        finally
        {
            _syncing = false;
            UpdateSyncHint();
        }
    }

    private async Task TrySyncSilentAsync()
    {
        if (!_sync.HasClientId || _syncing) return;
        try
        {
            if (await _sync.TrySyncSilentAsync())
            {
                RebuildCalendar();
                if (_tab == TabMode.Todo) ReloadTodos();
                UpdateSyncHint();
            }
        }
        catch { /* silent */ }
    }
}
