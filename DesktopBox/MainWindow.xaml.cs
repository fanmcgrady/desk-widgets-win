using System.Windows;
using System.Windows.Threading;
using DesktopBox.Services;
using DesktopBox.Services.Shell;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace DesktopBox;

public partial class MainWindow : Window
{
    private AppConfig _config;
    private bool _paused;
    private bool _fencesShown = true;
    private bool _syncQueued;
    private bool _closing;
    private ForegroundWatcher? _foregroundWatcher;
    private ShellMouseRouting? _mouseRouting;
    private StatsAppBarWindow? _statsBar;
    private readonly Dictionary<string, ZoneView> _zones = new();
    private readonly Dictionary<string, Rect> _zoneRects = new();
    private readonly DispatcherTimer _syncDebounce;
    private double _lastLeft = double.NaN;
    private double _lastTop = double.NaN;
    private double _lastWidth = double.NaN;
    private double _lastHeight = double.NaN;
    private string _lastRegionKey = "";

    public MainWindow(AppConfig config)
    {
        _config = config;
        InitializeComponent();
        BuildZones();

        SourceInitialized += (_, _) =>
        {
            EnsureFenceStyle();
            FitPrimaryScreen();
            RefreshRegion();
            ClickThrough.PlaceAboveDesktopIcons(this);
            _mouseRouting = new ShellMouseRouting(this, ZoneFromLocalPoint);
            _mouseRouting.Attach();
        };

        Loaded += (_, _) =>
        {
            EnsureFenceStyle();
            Visibility = Visibility.Visible;
            EnsureStatsBar();
            FitPrimaryScreen();
            RefreshRegion();
            UpdateLayout();
            ClickThrough.PlaceAboveDesktopIcons(this);

            // 前台变化时只纠正 Z 序，不再 Hide/Show、不再 Topmost
            _foregroundWatcher = new ForegroundWatcher(this, QueueSyncZOrder);
            _foregroundWatcher.Start();

            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                // 不预热三区 Shell；等鼠标移入或点击再懒创建
                SyncZOrder();
            });
        };

        SizeChanged += (_, _) =>
        {
            LayoutZones();
            RefreshRegion();
        };

        // Z 序靠前台事件；重摆靠分辨率变化，不再 10s/15s 轮询
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _syncDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _syncDebounce.Tick += (_, _) =>
        {
            _syncDebounce.Stop();
            _syncQueued = false;
            SyncZOrder();
        };

        // 关闭后必须停表，否则会继续 EnsureHandle 并刷崩溃日志
        Closing += (_, _) =>
        {
            _closing = true;
            DisposeHooks();
        };
        Closed += (_, _) => DisposeHooks();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_closing || _paused || !_fencesShown)
            return;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (_closing || _paused)
                return;
            _statsBar?.RefreshPosition();
            FitPrimaryScreen(force: true);
            SyncZOrder(force: true);
        });
    }

    public void DisposeHooks()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _syncDebounce.Stop();
        _mouseRouting?.Dispose();
        _mouseRouting = null;
        _foregroundWatcher?.Dispose();
        _foregroundWatcher = null;
        DisposeStatsBar();
        foreach (var z in _zones.Values)
            z.DisposeService();
    }

    private void EnsureStatsBar()
    {
        if (_closing)
            return;
        if (_statsBar is null)
        {
            _statsBar = new StatsAppBarWindow();
            _statsBar.Closed += (_, _) =>
            {
                if (!_closing)
                    _statsBar = null;
            };
        }

        _statsBar.Start();
    }

    private void DisposeStatsBar()
    {
        if (_statsBar is null)
            return;
        try
        {
            _statsBar.Pause();
            _statsBar.Close();
        }
        catch { /* ignore */ }
        _statsBar = null;
    }

    private bool HasHwnd =>
        !_closing
        && new System.Windows.Interop.WindowInteropHelper(this).Handle != IntPtr.Zero;

    /// <summary>有 HWND、未暂停、未关闭时才可改样式/Z 序。</summary>
    private bool CanTouchWindow => HasHwnd && !_paused;

    private ZoneView? ZoneFromLocalPoint(System.Windows.Point local)
    {
        foreach (var (id, rect) in _zoneRects)
        {
            if (rect.Contains(local) && _zones.TryGetValue(id, out var zone))
                return zone;
        }

        return null;
    }

    public void SetPaused(bool paused)
    {
        if (_closing)
            return;

        _paused = paused;
        if (paused)
        {
            _fencesShown = false;
            if (HasHwnd)
                Visibility = Visibility.Hidden;
            _statsBar?.Pause();
            return;
        }

        // 恢复显示：Hidden 后再 Show，并等布局完成再打 Region（否则 Actual 为 0 会打成空区）
        _fencesShown = true;
        if (!IsVisible)
            Show();
        Visibility = Visibility.Visible;
        EnsureStatsBar();
        _statsBar?.Resume();
        RestoreVisibleLayout();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, RestoreVisibleLayout);
    }

    private void RestoreVisibleLayout()
    {
        if (_paused || _closing)
            return;

        UpdateLayout();
        FitPrimaryScreen(force: true);
        UpdateLayout();
        LayoutZones();
        RefreshRegion(force: true);
        SyncZOrder(force: true);
        foreach (var z in _zones.Values)
        {
            // Hidden 期间 HwndHost 可能已失效，重建 Shell 视图
            z.DisposeService();
            z.Reload();
        }
    }

    public void ReloadConfig()
    {
        foreach (var z in _zones.Values)
            z.DisposeService();

        _config = AppConfig.LoadOrCreate();
        _config.EnsureFolders();
        BuildZones();
        FitPrimaryScreen(force: true);
        RefreshRegion(force: true);
        SyncZOrder(force: true);
    }

    public void PreviewZones(TimeSpan duration)
    {
        foreach (var z in _zones.Values)
            z.Reload();
    }

    private void QueueSyncZOrder()
    {
        if (_paused || !CanTouchWindow)
            return;
        if (_syncQueued)
            return;
        _syncQueued = true;
        _syncDebounce.Stop();
        _syncDebounce.Start();
    }

    /// <summary>常显围栏，仅维持桌面层 Z 序；暂停时 Hidden。已在正确层则不动。</summary>
    private void SyncZOrder(bool force = false)
    {
        if (_paused || _closing || !CanTouchWindow)
            return;

        try
        {
            if (Visibility != Visibility.Visible)
                Visibility = Visibility.Visible;

            _fencesShown = true;
            EnsureFenceStyle();
            _ = force;
            ClickThrough.PlaceAboveDesktopIcons(this);
        }
        catch (InvalidOperationException)
        {
            // 窗口已关闭时忽略
            _closing = true;
            DisposeHooks();
        }
    }

    private void EnsureFenceStyle()
    {
        if (!CanTouchWindow)
            return;
        // ApplyFenceStyle 内部已做幂等；样式未变时不 SetWindowPos
        ClickThrough.ApplyFenceStyle(this);
    }

    private void FitPrimaryScreen(bool force = false)
    {
        if (!CanTouchWindow)
            return;

        var screen = WinForms.Screen.PrimaryScreen;
        if (screen is null)
            return;

        var bounds = screen.Bounds;
        var src = PresentationSource.FromVisual(this);
        var dpiX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var dpiY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        var left = bounds.Left / dpiX;
        var top = bounds.Top / dpiY;
        var width = bounds.Width / dpiX;
        var height = bounds.Height / dpiY;

        if (!force
            && NearlyEqual(left, _lastLeft)
            && NearlyEqual(top, _lastTop)
            && NearlyEqual(width, _lastWidth)
            && NearlyEqual(height, _lastHeight))
        {
            return;
        }

        _lastLeft = left;
        _lastTop = top;
        _lastWidth = width;
        _lastHeight = height;

        Left = left;
        Top = top;
        Width = width;
        Height = height;
        LayoutZones();
        RefreshRegion(force: true);
    }

    private void RefreshRegion(bool force = false)
    {
        if (_paused || !_fencesShown)
            return;

        var key = BuildRegionKey();
        if (!force && key == _lastRegionKey)
            return;
        _lastRegionKey = key;

        // 直角分区：细线圆角在 DPI 下易发虚，Region 用矩形更干净
        ClickThrough.ApplyZoneRegion(this, _zoneRects.Values, cornerRadiusDip: 0);
    }

    private string BuildRegionKey()
    {
        if (_zoneRects.Count == 0)
            return "";
        var parts = new List<string>(_zoneRects.Count);
        foreach (var id in _zoneRects.Keys.OrderBy(x => x))
        {
            var r = _zoneRects[id];
            parts.Add($"{id}:{r.X:0.#},{r.Y:0.#},{r.Width:0.#},{r.Height:0.#}");
        }
        return string.Join("|", parts);
    }

    private static bool NearlyEqual(double a, double b) =>
        Math.Abs(a - b) < 0.5;

    private void BuildZones()
    {
        RootGrid.Children.Clear();
        _zones.Clear();
        _zoneRects.Clear();
        _lastRegionKey = "";

        foreach (var cfg in _config.Zones)
        {
            var view = new ZoneView();
            view.Bind(cfg, _config.DropMode);
            RootGrid.Children.Add(view);
            _zones[cfg.Id] = view;
        }

        LayoutZones();
    }

    private void LayoutZones()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var layout = _config.Layout;
        var w = ActualWidth;
        var h = ActualHeight;
        // 只占右下角，其余留给桌面图标
        var gx = w * layout.GutterX;
        var gy = h * layout.GutterY;
        var cx = w / 2;
        var cy = h / 2;

        var bottomPad = 0.0;
        var screen = WinForms.Screen.PrimaryScreen;
        if (screen is not null)
        {
            var src = PresentationSource.FromVisual(this);
            var dpiY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            bottomPad = Math.Max(0, (screen.Bounds.Bottom - screen.WorkingArea.Bottom) / dpiY);
        }

        var lowTop = cy + gy;
        var lowBottom = h - bottomPad;
        Place("todo", cx + gx, lowTop, w - (cx + gx), lowBottom - lowTop);
    }

    private void Place(string id, double x, double y, double width, double height)
    {
        if (!_zones.TryGetValue(id, out var view))
            return;
        if (width < 40 || height < 40)
            return;

        // 尺寸未变则不改 Margin/Width，减少 HwndHost OnWindowPositionChanged
        if (_zoneRects.TryGetValue(id, out var old)
            && NearlyEqual(old.X, x)
            && NearlyEqual(old.Y, y)
            && NearlyEqual(old.Width, width)
            && NearlyEqual(old.Height, height)
            && NearlyEqual(view.Width, width)
            && NearlyEqual(view.Height, height))
        {
            return;
        }

        view.Width = width;
        view.Height = height;
        view.Margin = new Thickness(x, y, 0, 0);
        view.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        view.VerticalAlignment = VerticalAlignment.Top;
        _zoneRects[id] = new Rect(x, y, width, height);
    }
}
