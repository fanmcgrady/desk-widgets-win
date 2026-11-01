using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DeskWidgets.Services;

namespace DeskWidgets;

public partial class StartupWatchWindow : Window
{
    private static readonly SolidColorBrush PendingBrush = Freeze(System.Windows.Media.Color.FromRgb(0x8A, 0x88, 0x86));
    private static readonly SolidColorBrush ReadyBrush = Freeze(System.Windows.Media.Color.FromRgb(0x10, 0x7C, 0x10));
    private static readonly TimeSpan ItemTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CloseAfterAllGreen = TimeSpan.FromMilliseconds(500);

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
            var items = StartupWatch.LoadEnabledOthers();
            if (items.Count == 0) return;
            var window = new StartupWatchWindow(items);
            if (preview)
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Loaded += (_, _) => PlaceBesideSidebar(window);
            }
            window.Show();
        }
        catch { /* 读启动项失败时不挡侧栏 */ }
    }

    private static void PlaceBesideSidebar(Window window)
    {
        if (System.Windows.Application.Current?.MainWindow is not Window main || main == window) return;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        var width = window.ActualWidth > 1 ? window.ActualWidth : 320;
        var area = SystemParameters.WorkArea;
        window.Left = Math.Max(area.Left + 12, main.Left - width - 16);
        window.Top = main.Top + 48;
    }

    public StartupWatchWindow(IReadOnlyList<StartupEntry> items)
    {
        InitializeComponent();
        _rows = new Row[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var row = MakeRow(items[i]);
            _rows[i] = row;
            Rows.Children.Add(row.Panel);
        }
        UpdateHint();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            Refresh();
            _timer.Start();
        };
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

            if (StartupWatch.IsRunning(row.Entry.ProcessName))
            {
                row.Ready = true;
                row.Dot.Fill = ReadyBrush;
                row.Name.Foreground = ReadyBrush;
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
        _closeQueued = true;
        if (green == _rows.Length)
        {
            var close = new DispatcherTimer { Interval = CloseAfterAllGreen };
            close.Tick += (_, _) =>
            {
                close.Stop();
                Close();
            };
            close.Start();
            return;
        }

        Close();
    }

    private void UpdateHint()
    {
        var green = 0;
        foreach (var row in _rows)
        {
            if (row.Ready) green++;
        }
        Hint.Text = $"{green}/{_rows.Length} 已启动";
    }

    private static Row MakeRow(StartupEntry entry)
    {
        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = PendingBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var name = new TextBlock
        {
            Text = entry.Name,
            Foreground = PendingBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var panel = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        DockPanel.SetDock(dot, Dock.Left);
        panel.Children.Add(dot);
        panel.Children.Add(name);
        return new Row(entry, panel, dot, name);
    }

    private static SolidColorBrush Freeze(System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed class Row
    {
        public Row(StartupEntry entry, DockPanel panel, Ellipse dot, TextBlock name)
        {
            Entry = entry;
            Panel = panel;
            Dot = dot;
            Name = name;
        }

        public StartupEntry Entry { get; }
        public DockPanel Panel { get; }
        public Ellipse Dot { get; }
        public TextBlock Name { get; }
        public bool Ready { get; set; }
        public bool TimedOut { get; set; }
    }
}
