using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DeskWidgets.Services;

namespace DeskWidgets;

public partial class StartupWatchWindow : Window
{
    private static readonly SolidColorBrush PendingBrush = Freeze(System.Windows.Media.Color.FromRgb(0x8A, 0x8A, 0x8A));
    private static readonly SolidColorBrush ReadyBrush = Freeze(System.Windows.Media.Color.FromRgb(0x00, 0xFF, 0x66));
    private static readonly SolidColorBrush DimBrush = Freeze(System.Windows.Media.Color.FromRgb(0x1F, 0x8F, 0x45));
    private static readonly TimeSpan ItemTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CloseAfterAllGreen = TimeSpan.FromMilliseconds(800);

    private readonly Row[] _rows;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly DispatcherTimer _timer;
    private bool _closeQueued;

    public static void ShowIfFreshBoot()
    {
        try
        {
            var preview = Environment.GetCommandLineArgs().Any(a =>
                string.Equals(a, "--preview-startup", StringComparison.OrdinalIgnoreCase));
            if (!preview && !StartupWatch.IsFreshBoot()) return;
            // 枚举启动项（注册表 / 任务计划）放到后台，避免卡住侧栏 OnLoaded
            _ = Task.Run(() =>
            {
                try
                {
                    var items = StartupWatch.LoadEnabledOthers();
                    if (items.Count == 0) return;
                    var app = System.Windows.Application.Current;
                    if (app == null) return;
                    app.Dispatcher.BeginInvoke(() =>
                    {
                        try { new StartupWatchWindow(items).Show(); }
                        catch { /* ignore */ }
                    });
                }
                catch { /* ignore */ }
            });
        }
        catch { /* 读启动项失败时不挡侧栏 */ }
    }

    public StartupWatchWindow(IReadOnlyList<StartupEntry> items)
    {
        InitializeComponent();
        _rows = new Row[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var row = MakeRow(i + 1, items[i]);
            _rows[i] = row;
            Rows.Children.Add(row.Panel);
        }
        UpdateHint();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (_, _) => Refresh();
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) =>
        {
            PlaceOnPrimaryScreen();
            Refresh();
            _timer.Start();
            Activate();
            Focus();
        };
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        BeginClose();
    }

    private void PlaceOnPrimaryScreen()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null) return;
        var bounds = screen.Bounds;
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = bounds.Left / dpi.DpiScaleX;
        Top = bounds.Top / dpi.DpiScaleY;
        Width = bounds.Width / dpi.DpiScaleX;
        Height = bounds.Height / dpi.DpiScaleY;
    }

    private void Refresh()
    {
        var now = DateTime.UtcNow;
        var green = 0;
        var pending = 0;
        foreach (var row in _rows)
        {
            if (row.Ready)
            {
                green++;
                continue;
            }
            if (row.TimedOut)
                continue;

            if (StartupWatch.IsRunning(row.Entry))
            {
                row.Ready = true;
                row.Index.Foreground = ReadyBrush;
                row.Name.Foreground = ReadyBrush;
                row.Status.Foreground = ReadyBrush;
                row.Status.Text = "ONLINE";
                green++;
                continue;
            }

            if (now - _startedUtc >= ItemTimeout)
            {
                row.TimedOut = true;
                continue;
            }

            pending++;
        }

        UpdateHint();
        if (pending > 0 || _closeQueued) return;

        _timer.Stop();
        if (green == _rows.Length)
        {
            var close = new DispatcherTimer { Interval = CloseAfterAllGreen };
            close.Tick += (_, _) =>
            {
                close.Stop();
                BeginClose();
            };
            close.Start();
            return;
        }

        BeginClose();
    }

    private void BeginClose()
    {
        if (_closeQueued) return;
        _closeQueued = true;
        _timer.Stop();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280))
        {
            FillBehavior = FillBehavior.HoldEnd,
        };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    private void UpdateHint()
    {
        var green = 0;
        foreach (var row in _rows)
        {
            if (row.Ready) green++;
        }
        Clock.Text = DateTime.Now.ToString("HH:mm:ss");
        Hint.Text = $"{green}/{_rows.Length}";
        HintSub.Text = green == _rows.Length ? "ONLINE" : "WAITING";
        HintSub.Foreground = green == _rows.Length ? ReadyBrush : PendingBrush;
        var track = ProgressTrack.ActualWidth;
        ProgressFill.Width = track > 0 && _rows.Length > 0 ? track * green / _rows.Length : 0;
    }

    private static Row MakeRow(int index, StartupEntry entry)
    {
        var idx = new TextBlock
        {
            Text = index.ToString("00"),
            Width = 36,
            Foreground = DimBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var name = new TextBlock
        {
            Text = entry.Name,
            Foreground = PendingBrush,
            FontSize = 20,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var status = new TextBlock
        {
            Text = "WAIT",
            Width = 88,
            Foreground = PendingBrush,
            FontSize = 14,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var panel = new Grid { Margin = new Thickness(0, 8, 48, 8) };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(name, 1);
        Grid.SetColumn(status, 2);
        panel.Children.Add(idx);
        panel.Children.Add(name);
        panel.Children.Add(status);
        return new Row(entry, panel, idx, name, status);
    }

    private static SolidColorBrush Freeze(System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed class Row
    {
        public Row(StartupEntry entry, Grid panel, TextBlock index, TextBlock name, TextBlock status)
        {
            Entry = entry;
            Panel = panel;
            Index = index;
            Name = name;
            Status = status;
        }

        public StartupEntry Entry { get; }
        public Grid Panel { get; }
        public TextBlock Index { get; }
        public TextBlock Name { get; }
        public TextBlock Status { get; }
        public bool Ready { get; set; }
        public bool TimedOut { get; set; }
    }
}
