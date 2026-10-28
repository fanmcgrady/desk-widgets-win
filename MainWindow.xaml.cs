using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DeskWidgets.Services;
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
    private const double PanelWidthDip = 320;
    /// <summary>阅读/写信时临时加宽，减轻侧栏窄屏读长文的问题。</summary>
    private const double PanelWidthMailDip = 560;
    private const int EdgeHitDip = 8;

    private enum TabMode { Note, Todo, Mail }

    private readonly Store _store = new();
    private readonly NoteFiles _notes = new();
    private readonly MsToDoSync _sync;
    private readonly ImapSmtpMailService _mail = new();
    private enum MailView { List, Read, Compose, Settings }
    private MailView _mailView = MailView.List;
    private MailMessageDetail? _mailCurrent;
    private bool _mailBusy;
    private int _mailUnread;
    private List<MailMessageSummary> _mailCache = new();
    private DateTime _mailCacheAtUtc = DateTime.MinValue;
    private static readonly TimeSpan MailCacheTtl = TimeSpan.FromMinutes(3);
    private readonly DispatcherTimer _edgeTimer;
    private readonly DispatcherTimer _syncTimer;
    private readonly DispatcherTimer _mailPollTimer;
    private DispatcherTimer? _displayDebounce;
    private WinForms.NotifyIcon? _tray;
    private DrawingIcon? _trayIcon;
    private FileSystemWatcher? _noteWatcher;
    private WinForms.Screen? _dockScreen;
    /// <summary>投屏/分辨率抖动期间禁止自动换屏，避免侧栏跳来跳去。</summary>
    private DateTime _dockLockUntilUtc = DateTime.MinValue;
    private int _lastPlaceX = int.MinValue;
    private int _lastPlaceY = int.MinValue;
    private int _lastPlaceW = int.MinValue;
    private int _lastPlaceH = int.MinValue;

    private bool _expanded;
    private bool _allowClose;
    private DateTime? _leaveSince;
    /// <summary>托盘打开后：鼠标尚未进入侧栏前不自动收起，避免一点开就被 EdgeTimer 关掉。</summary>
    private bool _awaitPanelEnter;
    private int _year;
    private int _month;
    private DateTime _selected;
    private TabMode _tab = TabMode.Note;
    private bool _showDone;
    private bool _filterByDay;
    private bool _inputFocused;
    private bool _syncing;

    private string? _activeNoteId;
    private string? _editingTodoId; // 从代办「编辑」进入时，笔记区改的是这条代办
    private bool _noteLoading;
    private bool _noteWriting;
    private DispatcherTimer? _noteSaveTimer;
    private DispatcherTimer? _noteReloadDebounce;

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
        Autostart.ApplyPreferred(); // 默认开机自启；托盘可关，并记住选择
        SetupTray();
        StyleTabs();
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
        Expand();
        _edgeTimer.Start();
        _syncTimer.Start();
        _mailPollTimer.Start();
        _ = PollMailUnreadAsync(silent: true);

        var hide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        hide.Tick += (_, _) =>
        {
            hide.Stop();
            if (!_inputFocused) Collapse();
        };
        hide.Start();
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

        // 尽量留在原屏；原屏没了才退回主屏（比「最右侧」在投屏时更稳）
        var resolved = ResolveScreen(_dockScreen);
        _dockScreen = resolved
                      ?? WinForms.Screen.PrimaryScreen
                      ?? PickDefaultDockScreen();

        // 强制重放一次（忽略上次坐标缓存）
        _lastPlaceX = int.MinValue;
        if (_expanded) PlaceExpanded();
        else PlaceCollapsed();
    }

    private bool IsDockLocked() => DateTime.UtcNow < _dockLockUntilUtc;

    // ----- multi-monitor dock (physical pixels + MoveWindow) -----
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private static WinForms.Screen PickDefaultDockScreen()
    {
        // 默认停靠「最右侧」显示器（扩展屏常在主屏右边）
        WinForms.Screen? best = null;
        foreach (var s in WinForms.Screen.AllScreens)
        {
            if (best == null || s.Bounds.Right > best.Bounds.Right)
                best = s;
        }
        return best ?? WinForms.Screen.PrimaryScreen!;
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

    private static WinForms.Screen? ScreenNearRightEdge(DrawingPoint mouse)
    {
        foreach (var s in WinForms.Screen.AllScreens)
        {
            var scale = DpiScale(s);
            var hit = Math.Max(6, (int)Math.Ceiling(EdgeHitDip * scale));
            var wa = s.WorkingArea;
            if (mouse.X >= wa.Right - hit && mouse.X <= wa.Right + 2
                && mouse.Y >= wa.Top && mouse.Y <= wa.Bottom)
                return s;
        }
        return null;
    }

    private void EnsureHwnd()
    {
        var helper = new WindowInteropHelper(this);
        if (helper.Handle == IntPtr.Zero)
            helper.EnsureHandle();
    }

    /// <summary>当前侧栏目标宽度：邮件阅读/写信用宽版，其余用窄版。</summary>
    private double ActivePanelWidthDip
    {
        get
        {
            if (_tab == TabMode.Mail && _mailView is MailView.Read or MailView.Compose)
                return PanelWidthMailDip;
            return PanelWidthDip;
        }
    }

    private void PlaceOnScreen(WinForms.Screen screen, bool expanded)
    {
        _dockScreen = screen;
        EnsureHwnd();
        var hwnd = new WindowInteropHelper(this).Handle;
        var scale = DpiScale(screen);
        var dipW = expanded ? ActivePanelWidthDip : PanelWidthDip;
        // 宽屏不超过工作区约 55%，避免小笔记本被占满
        var maxDip = screen.WorkingArea.Width / scale * 0.55;
        if (dipW > maxDip) dipW = Math.Max(PanelWidthDip, maxDip);
        var widthPx = Math.Max(1, (int)Math.Round(dipW * scale));
        var wa = screen.WorkingArea;
        var x = expanded ? wa.Right - widthPx : wa.Right;
        var y = wa.Top;
        var h = Math.Max(1, wa.Height);

        // 坐标未变则跳过 MoveWindow，减少投屏时闪跳
        if (x == _lastPlaceX && y == _lastPlaceY && widthPx == _lastPlaceW && h == _lastPlaceH
            && Math.Abs(Width - dipW) < 0.5)
        {
            Opacity = 1;
            if (expanded) Show();
            return;
        }

        _lastPlaceX = x;
        _lastPlaceY = y;
        _lastPlaceW = widthPx;
        _lastPlaceH = h;

        // WPF 尺寸用 DIP；实际位置用像素 MoveWindow，避免多屏 DPI 错位
        Width = dipW;
        Height = h / scale;
        MoveWindow(hwnd, x, y, widthPx, h, true);
        Opacity = 1;
        if (expanded) Show();
    }

    private void PlaceExpanded()
    {
        var screen = ResolveScreen(_dockScreen)
                     ?? WinForms.Screen.PrimaryScreen
                     ?? PickDefaultDockScreen();
        PlaceOnScreen(screen, expanded: true);
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
    }

    /// <summary>托盘 / 菜单唤起：展开后等鼠标进侧栏再启用自动收起。</summary>
    private void ExpandFromTray()
    {
        _awaitPanelEnter = true;
        _leaveSince = null;
        Expand();
    }

    private void Collapse()
    {
        FlushNoteSave();
        _expanded = false;
        _leaveSince = null;
        _awaitPanelEnter = false;
        PlaceCollapsed();
    }

    private void EdgeTimer_Tick(object? sender, EventArgs e)
    {
        var mouse = WinForms.Control.MousePosition; // 物理像素
        var edgeScreen = ScreenNearRightEdge(mouse);
        var dockLocked = IsDockLocked();

        if (!_expanded)
        {
            if (edgeScreen == null) return;
            // 投屏抖动时：只响应当前停靠屏（或尚无停靠时的主屏）右缘，避免跳到虚拟/镜像屏
            if (dockLocked)
            {
                var prefer = ResolveScreen(_dockScreen) ?? WinForms.Screen.PrimaryScreen;
                if (prefer != null && edgeScreen.DeviceName != prefer.DeviceName)
                    return;
            }
            _dockScreen = edgeScreen;
            _awaitPanelEnter = false;
            Expand();
            return;
        }

        var screen = ResolveScreen(_dockScreen)
                     ?? WinForms.Screen.PrimaryScreen
                     ?? PickDefaultDockScreen();
        var scale = DpiScale(screen);
        var dipW = ActivePanelWidthDip;
        var maxDip = screen.WorkingArea.Width / scale * 0.55;
        if (dipW > maxDip) dipW = Math.Max(PanelWidthDip, maxDip);
        var widthPx = Math.Max(1, (int)Math.Round(dipW * scale));
        var hit = Math.Max(6, (int)Math.Ceiling(EdgeHitDip * scale));
        var wa = screen.WorkingArea;

        bool nearEdge = mouse.X >= wa.Right - hit && mouse.X <= wa.Right + 2
                        && mouse.Y >= wa.Top && mouse.Y <= wa.Bottom;
        bool inPanel = mouse.X >= wa.Right - widthPx && mouse.X <= wa.Right
                       && mouse.Y >= wa.Top && mouse.Y <= wa.Bottom;

        // 鼠标到了另一块屏的右缘：切换停靠屏（投屏锁定期间禁止，防止乱跳）
        if (!dockLocked
            && edgeScreen != null
            && edgeScreen.DeviceName != screen.DeviceName)
        {
            _dockScreen = edgeScreen;
            PlaceExpanded();
            _leaveSince = null;
            return;
        }

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

        if (_inputFocused || inPanel || nearEdge)
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
        if (_inputFocused) return;
        // 阅读中优先返回列表，而不是直接收起侧栏
        if (_tab == TabMode.Mail && _mailView == MailView.Read)
        {
            BtnMailBack_Click(sender, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        Collapse();
        e.Handled = true;
    }

    /// <summary>鼠标侧键回退（XButton1）在阅读邮件时返回列表。</summary>
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.XButton1) return;
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
            Text = "方老师的桌面工作台",
        };
        var menu = new WinForms.ContextMenuStrip
        {
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9f),
            ShowImageMargin = false,
            RenderMode = WinForms.ToolStripRenderMode.Professional,
            BackColor = DrawingColor.FromArgb(255, 250, 250, 250),
            ForeColor = DrawingColor.FromArgb(255, 28, 27, 31),
        };
        menu.Renderer = new TrayMenuRenderer();
        menu.Items.Add("展开侧栏", null, (_, _) => Dispatcher.Invoke(ExpandFromTray));
        menu.Items.Add("收起侧栏", null, (_, _) => Dispatcher.Invoke(Collapse));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("登录并同步", null, async (_, _) => await Dispatcher.InvokeAsync(async () => await RunSyncAsync(true)));
        menu.Items.Add("打开同步配置", null, (_, _) => Dispatcher.Invoke(OpenSyncConfig));
        menu.Items.Add("退出微软账号", null, async (_, _) =>
        {
            await _sync.SignOutAsync();
            Dispatcher.Invoke(UpdateSyncHint);
        });
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("邮箱设置", null, (_, _) => Dispatcher.Invoke(OpenMailSettingsFromTray));
        var auto = new WinForms.ToolStripMenuItem("开机自启")
        {
            Checked = Autostart.IsEnabled(),
            CheckOnClick = true,
        };
        auto.CheckedChanged += (_, _) => Autostart.SetEnabled(auto.Checked);
        menu.Items.Add(auto);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Quit));
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

    /// <summary>与主界面一致的紫色日历托盘图标（Accent #6750A4 / Soft #E8DEF8）。</summary>
    private static DrawingIcon CreateTrayIcon()
    {
        var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.Clear(DrawingColor.Transparent);

            var soft = DrawingColor.FromArgb(232, 222, 248);   // AccentSoft
            var accent = DrawingColor.FromArgb(103, 80, 164);  // Accent
            var white = DrawingColor.White;

            using (var softBrush = new System.Drawing.SolidBrush(soft))
                FillRoundRect(g, softBrush, 1, 1, 30, 30, 8);

            // 日历外框
            using (var accentBrush = new System.Drawing.SolidBrush(accent))
                FillRoundRect(g, accentBrush, 7, 9, 18, 17, 4);

            // 顶部装订条
            using (var bar = new System.Drawing.SolidBrush(DrawingColor.FromArgb(232, 222, 248)))
                g.FillRectangle(bar, 7, 9, 18, 5);

            // 装订环
            using (var ring = new System.Drawing.SolidBrush(accent))
            {
                g.FillEllipse(ring, 10, 6, 4, 5);
                g.FillEllipse(ring, 18, 6, 4, 5);
            }

            // 日期格（白点），其中一格高亮表示「今天」
            using (var cell = new System.Drawing.SolidBrush(DrawingColor.FromArgb(230, 255, 255, 255)))
            {
                g.FillRectangle(cell, 10, 16, 3, 3);
                g.FillRectangle(cell, 15, 16, 3, 3);
                g.FillRectangle(cell, 20, 16, 3, 3);
                g.FillRectangle(cell, 10, 21, 3, 3);
                g.FillRectangle(cell, 20, 21, 3, 3);
            }
            using (var today = new System.Drawing.SolidBrush(white))
                g.FillRectangle(today, 15, 21, 3, 3);
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

    /// <summary>托盘菜单：悬停用主界面紫色软底。</summary>
    private sealed class TrayMenuRenderer : WinForms.ToolStripProfessionalRenderer
    {
        public TrayMenuRenderer() : base(new TrayColorTable()) { }

        protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextFont = new System.Drawing.Font("Microsoft YaHei UI", 9f);
            base.OnRenderItemText(e);
        }
    }

    private sealed class TrayColorTable : WinForms.ProfessionalColorTable
    {
        private static readonly DrawingColor Soft = DrawingColor.FromArgb(232, 222, 248);
        private static readonly DrawingColor SoftHover = DrawingColor.FromArgb(220, 210, 240);
        private static readonly DrawingColor Bg = DrawingColor.FromArgb(250, 250, 250);

        public override DrawingColor MenuItemSelected => SoftHover;
        public override DrawingColor MenuItemSelectedGradientBegin => Soft;
        public override DrawingColor MenuItemSelectedGradientEnd => SoftHover;
        public override DrawingColor MenuItemBorder => DrawingColor.FromArgb(103, 80, 164);
        public override DrawingColor MenuBorder => DrawingColor.FromArgb(40, 0, 0, 0);
        public override DrawingColor ToolStripDropDownBackground => Bg;
        public override DrawingColor ImageMarginGradientBegin => Bg;
        public override DrawingColor ImageMarginGradientMiddle => Bg;
        public override DrawingColor ImageMarginGradientEnd => Bg;
        public override DrawingColor SeparatorDark => DrawingColor.FromArgb(30, 0, 0, 0);
        public override DrawingColor SeparatorLight => DrawingColor.FromArgb(20, 0, 0, 0);
    }

    private void Quit()
    {
        FlushNoteSave();
        _noteWatcher?.Dispose();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _edgeTimer.Stop();
        _syncTimer.Stop();
        _mailPollTimer.Stop();
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
        int start = (int)first.DayOfWeek; // 周日为一周起点（对齐设计图）
        int days = DateTime.DaysInMonth(_year, _month);
        int cells = (int)Math.Ceiling((start + days) / 7.0) * 7;
        var today = DateTime.Today;
        var accent = (MediaBrush)FindResource("Accent");
        var accentSoft = (MediaBrush)FindResource("AccentSoft");
        var muted = (MediaBrush)FindResource("Muted");
        var fg = (MediaBrush)FindResource("Fg");

        for (int i = 0; i < cells; i++)
        {
            int dayNum = i - start + 1;
            var btn = new WpfButton
            {
                Height = 28,
                Margin = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = MediaBrushes.Transparent,
                Cursor = WpfCursors.Hand,
                Padding = new Thickness(0),
            };

            DateTime date;
            bool inMonth = dayNum >= 1 && dayNum <= days;
            if (!inMonth)
            {
                // 淡显邻月日期
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

            // 选中：实心圆 + 白字；今天未选：描边圆；有事：圆下小点
            MediaBrush numFg;
            if (!inMonth) numFg = muted;
            else if (isSel) numFg = MediaBrushes.White;
            else if (isToday) numFg = accent;
            else numFg = fg;

            var dayChrome = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = isSel ? accent : MediaBrushes.Transparent,
                BorderBrush = isToday && !isSel ? accent : MediaBrushes.Transparent,
                BorderThickness = new Thickness(isToday && !isSel ? 1.5 : 0),
                Child = new TextBlock
                {
                    Text = date.Day.ToString(),
                    FontSize = 12,
                    FontWeight = isSel || isToday ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = numFg,
                    HorizontalAlignment = WpfHorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            var stack = new StackPanel { HorizontalAlignment = WpfHorizontalAlignment.Center };
            stack.Children.Add(dayChrome);
            stack.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 3.5,
                Height = 3.5,
                Margin = new Thickness(0, 2, 0, 0),
                Fill = hasDot
                    ? (isSel ? accentSoft : accent)
                    : MediaBrushes.Transparent,
                HorizontalAlignment = WpfHorizontalAlignment.Center,
            });

            btn.Height = 34;
            btn.Content = stack;

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
        else DueHint.Text = $"全部未完成 · 新代办挂到 {_selected:MM-dd}";
    }

    // ----- tabs -----
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

        // 阅读/写信加宽，列表/设置收回窄栏
        if (_expanded) PlaceExpanded();

        // 宽→窄时，鼠标常还停在「原来宽出来的区域」，会被当成离开而收起。
        // 收窄后先锁住自动隐藏，等鼠标再次进入侧栏再恢复。
        if (wasWide && !willWide && _expanded)
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
        var text = _mailUnread > 99 ? "99+" : _mailUnread.ToString();
        var show = _mailUnread > 0 ? Visibility.Visible : Visibility.Collapsed;

        MailTabBadgeText.Text = text;
        MailTabBadge.Visibility = show;
        MailInboxBadgeText.Text = text;
        MailInboxBadge.Visibility = show;

        if (_tray != null)
            _tray.Text = _mailUnread > 0
                ? $"方老师的桌面工作台 · 未读 {_mailUnread}"
                : "方老师的桌面工作台";
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
            : new SolidColorBrush(MediaColor.FromArgb(0x1A, 0x67, 0x50, 0xA4));

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
            MailReadBody.Text = string.IsNullOrWhiteSpace(detail.BodyText) ? "（无正文）" : detail.BodyText;
            RenderMailAttachments(detail);
            ShowMailView(MailView.Read);
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
            Margin = new Thickness(0, 0, 0, 6),
        });

        var accent = (MediaBrush)FindResource("Accent");
        var muted = (MediaBrush)FindResource("Muted");
        foreach (var att in detail.Attachments)
        {
            var row = new Border
            {
                Background = new SolidColorBrush(MediaColor.FromArgb(0x10, 0x67, 0x50, 0xA4)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var sizeText = att.Size > 0 ? FormatBytes(att.Size) : "";
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock
            {
                Text = att.FileName,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
            });
            if (!string.IsNullOrEmpty(sizeText))
            {
                left.Children.Add(new TextBlock
                {
                    Text = sizeText,
                    FontSize = 11,
                    Foreground = muted,
                    Margin = new Thickness(0, 2, 0, 0),
                });
            }
            Grid.SetColumn(left, 0);

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
            Grid.SetColumn(saveBtn, 1);

            grid.Children.Add(left);
            grid.Children.Add(saveBtn);
            row.Child = grid;
            MailReadAttachments.Children.Add(row);
        }
    }

    private async Task DownloadMailAttachmentAsync(MailKit.UniqueId uid, MailAttachmentInfo att)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = att.FileName,
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

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
        return (bytes / (1024.0 * 1024.0)).ToString("0.##") + " MB";
    }

    private void BtnMailBack_Click(object sender, RoutedEventArgs e)
    {
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

        NotePanel.Visibility = _tab == TabMode.Note ? Visibility.Visible : Visibility.Collapsed;
        TodoPanel.Visibility = _tab == TabMode.Todo ? Visibility.Visible : Visibility.Collapsed;
        MailPanel.Visibility = _tab == TabMode.Mail ? Visibility.Visible : Visibility.Collapsed;
        // 日历只在「代办事项」显示
        CalendarHost.Visibility = _tab == TabMode.Todo ? Visibility.Visible : Visibility.Collapsed;
    }

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
        };
        row.MouseEnter += (_, _) => row.Background = hover;
        row.MouseLeave += (_, _) => row.Background = MediaBrushes.Transparent;

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
            Tag = todo.Id,
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
            _store.ToggleTodo((string)check.Tag);
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
            Cursor = WpfCursors.Hand,
            ToolTip = "单击编辑",
        };
        title.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            BeginInlineTodoEdit(todo, titleHost, title);
        };
        titleHost.Children.Add(title);
        Grid.SetColumn(titleHost, 1);
        Grid.SetRow(titleHost, 0);

        var actions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
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
        };
        edit.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
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
            Tag = todo.Id,
            ToolTip = "删除",
        };
        del.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _store.DeleteTodo((string)del.Tag);
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
        void Commit()
        {
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
            titleHost.Children.Clear();
            titleHost.Children.Add(titleBlock);
        }
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
            // 失焦也保存（若仍挂在树上）
            if (titleHost.Children.Contains(box))
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
            BtnAddTodoFromNote.Content = "加入代办";
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
            // 完成编辑 → 回代办
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
                    ? new SolidColorBrush(MediaColor.FromArgb(0x2E, 0x67, 0x50, 0xA4))
                    : new SolidColorBrush(MediaColor.FromArgb(0x12, 0x67, 0x50, 0xA4)),
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
    }

    private void BtnNotepad_Click(object sender, RoutedEventArgs e)
    {
        FlushNoteSave();
        if (_editingTodoId != null)
        {
            var fid = TodoEditFileId(_editingTodoId);
            _notes.OpenInNotepad(fid, NoteEdit.Text);
            AttachNoteWatcher(fid); // 仅记事本外部编辑时监视
            return;
        }
        if (_activeNoteId == null) EnsureActiveNote();
        if (_activeNoteId == null) return;
        _notes.OpenInNotepad(_activeNoteId, NoteEdit.Text);
        AttachNoteWatcher(_activeNoteId);
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
