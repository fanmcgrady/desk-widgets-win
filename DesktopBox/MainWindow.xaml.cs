using System.Windows;
using System.Windows.Threading;
using DesktopBox.Services;
using DesktopBox.Services.Shell;
using WinForms = System.Windows.Forms;

namespace DesktopBox;

public partial class MainWindow : Window
{
    private AppConfig _config;
    private bool _paused;
    private bool _fencesShown = true;
    private bool _syncQueued;
    private ForegroundWatcher? _foregroundWatcher;
    private ShellMouseRouting? _mouseRouting;
    private readonly Dictionary<string, ZoneView> _zones = new();
    private readonly Dictionary<string, Rect> _zoneRects = new();
    private readonly DispatcherTimer _repositionTimer;
    private readonly DispatcherTimer _zOrderTimer;
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
            FitPrimaryScreen();
            RefreshRegion();
            UpdateLayout();
            ClickThrough.PlaceAboveDesktopIcons(this);

            // 前台变化时只纠正 Z 序，不再 Hide/Show、不再 Topmost
            _foregroundWatcher = new ForegroundWatcher(this, QueueSyncZOrder);
            _foregroundWatcher.Start();

            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                foreach (var z in _zones.Values)
                    z.Reload();
                SyncZOrder();
            });
        };

        SizeChanged += (_, _) =>
        {
            LayoutZones();
            RefreshRegion();
        };

        // 分辨率变化才需要重摆；无变化时直接跳过，避免定时 SetWindowRgn 闪烁
        _repositionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _repositionTimer.Tick += (_, _) =>
        {
            if (_fencesShown && !_paused)
                FitPrimaryScreen();
        };
        _repositionTimer.Start();

        // 兜底纠正 Z 序；正常靠前台事件。间隔拉长，已在正确层则不动。
        _zOrderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _zOrderTimer.Tick += (_, _) => SyncZOrder();
        _zOrderTimer.Start();

        _syncDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _syncDebounce.Tick += (_, _) =>
        {
            _syncDebounce.Stop();
            _syncQueued = false;
            SyncZOrder();
        };
    }

    public void DisposeHooks()
    {
        _repositionTimer.Stop();
        _zOrderTimer.Stop();
        _syncDebounce.Stop();
        _mouseRouting?.Dispose();
        _mouseRouting = null;
        _foregroundWatcher?.Dispose();
        _foregroundWatcher = null;
        foreach (var z in _zones.Values)
            z.DisposeService();
    }

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
        _paused = paused;
        if (paused)
        {
            _fencesShown = false;
            Visibility = Visibility.Hidden;
            EnsureFenceStyle();
        }
        else
        {
            _fencesShown = true;
            Visibility = Visibility.Visible;
            FitPrimaryScreen(force: true);
            RefreshRegion(force: true);
            UpdateLayout();
            SyncZOrder(force: true);
            foreach (var z in _zones.Values)
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
        if (_paused)
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
        if (_paused)
            return;

        if (Visibility != Visibility.Visible)
            Visibility = Visibility.Visible;

        _fencesShown = true;
        EnsureFenceStyle();
        _ = force;
        ClickThrough.PlaceAboveDesktopIcons(this);
    }

    private void EnsureFenceStyle()
    {
        // ApplyFenceStyle 内部已做幂等；样式未变时不 SetWindowPos
        ClickThrough.ApplyFenceStyle(this);
    }

    private void FitPrimaryScreen(bool force = false)
    {
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
        foreach (var id in new[] { "project", "teaching", "affairs", "todo" })
        {
            if (!_zoneRects.TryGetValue(id, out var r))
                continue;
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

        string[] order = ["project", "teaching", "affairs", "todo"];
        foreach (var id in order)
        {
            var cfg = _config.FindZone(id);
            if (cfg is null)
                continue;

            var view = new ZoneView();
            view.Bind(cfg, _config.DropMode);
            view.HideRequested += (_, _) => SetPaused(true);
            RootGrid.Children.Add(view);
            _zones[id] = view;
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
        // 左/右/顶贴满屏幕外沿；底部避开任务栏；中间十字白槽留给桌面/Logo
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

        var topBottom = cy - gy;
        var lowTop = cy + gy;
        var lowBottom = h - bottomPad;

        Place("project", 0, 0, cx - gx, topBottom);
        Place("teaching", cx + gx, 0, w - (cx + gx), topBottom);
        Place("affairs", 0, lowTop, cx - gx, lowBottom - lowTop);
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
