using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using DesktopBox.Services;
using DesktopBox.Services.Shell;

namespace DesktopBox;

public partial class ZoneView : System.Windows.Controls.UserControl
{
    private static readonly SolidColorBrush TitleEnglishBrush = CreateFrozenBrush(0x99, 0x99, 0x99);
    private static readonly SolidColorBrush TitlePathBrush = CreateFrozenBrush(0x88, 0x88, 0x88);

    private ZoneConfig? _zone;
    private ExplorerBrowserHost? _shellHost;
    private SystemStatsService? _stats;

    public event EventHandler? HideRequested;

    public string ZoneId => _zone?.Id ?? "";

    /// <summary>当前 Shell 视图 HWND（供主窗转发滚轮/侧键）。</summary>
    public IntPtr ShellHostHwnd => _shellHost?.ViewHwnd ?? IntPtr.Zero;

    public ZoneView()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
        Unloaded += (_, _) => StopStatsMonitor();
    }

    internal bool TryGetShellHost(out ExplorerBrowserHost? host)
    {
        host = _shellHost;
        return host is not null;
    }

    internal void ForwardMouseWheel(int msg, IntPtr wParam, IntPtr lParam) =>
        _shellHost?.ForwardMouseWheel(msg, wParam, lParam);

    internal bool TryGoBack()
    {
        if (_shellHost is null || !_shellHost.CanGoBack)
            return false;
        _shellHost.GoBack();
        return true;
    }

    internal bool TryGoForward()
    {
        if (_shellHost is null || !_shellHost.CanGoForward)
            return false;
        _shellHost.GoForward();
        return true;
    }

    public void Bind(ZoneConfig zone, string dropMode)
    {
        _ = dropMode; // Shell 拖放由系统处理
        DisposeService();

        _zone = zone;
        ApplyTitle(zone.Name, zone.English, relative: null);
        Directory.CreateDirectory(zone.Folder);

        _shellHost = new ExplorerBrowserHost(zone.Folder);
        _shellHost.Navigated += OnShellNavigated;
        ShellHostContainer.Children.Clear();
        ShellHostContainer.Children.Add(_shellHost);

        if (string.Equals(zone.Id, "todo", StringComparison.OrdinalIgnoreCase))
            StartStatsMonitor();
        else
            StopStatsMonitor();
    }

    public void Reload() => _shellHost?.RefreshView();

    public void DisposeService()
    {
        StopStatsMonitor();
        if (_shellHost is not null)
        {
            _shellHost.Navigated -= OnShellNavigated;
            ShellHostContainer.Children.Remove(_shellHost);
            _shellHost.Dispose();
            _shellHost = null;
        }
    }

    private void StartStatsMonitor()
    {
        StopStatsMonitor();
        StatsPanel.Visibility = Visibility.Visible;
        StatsRow.Height = new GridLength(64);
        _stats = new SystemStatsService();
        _stats.Sampled += OnStatsSampled;
        _stats.Start();
    }

    private void StopStatsMonitor()
    {
        if (_stats is not null)
        {
            _stats.Sampled -= OnStatsSampled;
            _stats.Dispose();
            _stats = null;
        }

        StatsPanel.Visibility = Visibility.Collapsed;
        StatsRow.Height = new GridLength(0);
        StatsCpu.Text = "—";
        StatsCpuBar.Width = 0;
        StatsMem.Text = "—";
        StatsMemDetail.Text = "";
        StatsNetDown.Text = "↓ —";
        StatsNetUp.Text = "↑ —";
        StatsWifi.Text = "—";
        StatsWifiSsid.Text = "";
        StatsWifiBar.Width = 0;
        StatsWifiBar.Background = WifiBarGray;
    }

    private static readonly SolidColorBrush WifiBarGreen = CreateFrozenBrush(0x10, 0x7C, 0x10);
    private static readonly SolidColorBrush WifiBarOrange = CreateFrozenBrush(0xF7, 0x63, 0x0C);
    private static readonly SolidColorBrush WifiBarRed = CreateFrozenBrush(0xE8, 0x11, 0x23);
    private static readonly SolidColorBrush WifiBarGray = CreateFrozenBrush(0xA0, 0xA0, 0xA0);
    private static readonly SolidColorBrush WifiTextMuted = CreateFrozenBrush(0x99, 0x99, 0x99);

    private void OnStatsSampled(object? sender, SystemStatsSample sample)
    {
        if (sample.CpuPercent < 0)
            StatsCpu.Text = "—";
        else
        {
            StatsCpu.Text = $"{sample.CpuPercent:0}%";
            var track = Math.Max(0, (StatsPanel.ActualWidth / 4) - 24);
            StatsCpuBar.Width = track * Math.Clamp(sample.CpuPercent / 100.0, 0, 1);
        }

        if (sample.Memory.IsValid)
        {
            StatsMem.Text = $"{sample.Memory.LoadPercent}%";
            StatsMemDetail.Text =
                $"{SystemStatsService.FormatBytes(sample.Memory.UsedBytes)} / {SystemStatsService.FormatBytes(sample.Memory.TotalBytes)}";
        }
        else
        {
            StatsMem.Text = "—";
            StatsMemDetail.Text = "";
        }

        StatsNetDown.Text = "↓ " + SystemStatsService.FormatRate(sample.DownBytesPerSec);
        StatsNetUp.Text = "↑ " + SystemStatsService.FormatRate(sample.UpBytesPerSec);

        ApplyWifiUi(sample.Wifi);
    }

    private void ApplyWifiUi(WifiInfo wifi)
    {
        var track = Math.Max(0, StatsWifiTrack.ActualWidth);
        if (track < 1)
            track = Math.Max(0, (StatsPanel.ActualWidth / 4) - 24);

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

    private void OnShellNavigated(object? sender, string path)
    {
        if (_zone is null || _shellHost is null)
            return;

        string? relative = null;
        if (_shellHost.CanGoUp)
        {
            var root = Path.GetFullPath(_zone.Folder).TrimEnd('\\') + '\\';
            var full = Path.GetFullPath(path);
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                relative = full[root.Length..].TrimEnd('\\');
        }

        ApplyTitle(_zone.Name, _zone.English, relative);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            _shellHost?.RefreshView();
            _stats?.Start();
        }
        else
        {
            _stats?.Stop();
        }
    }

    private void ApplyTitle(string name, string english, string? relative)
    {
        TitleText.Inlines.Clear();
        TitleText.Inlines.Add(new Run(name)
        {
            FontSize = 13,
            Foreground = TitleText.Foreground
        });

        if (!string.IsNullOrWhiteSpace(english))
        {
            TitleText.Inlines.Add(new Run("  " + english)
            {
                FontSize = 11,
                Foreground = TitleEnglishBrush
            });
        }

        if (!string.IsNullOrWhiteSpace(relative))
        {
            TitleText.Inlines.Add(new Run("  ·  " + relative)
            {
                FontSize = 11,
                Foreground = TitlePathBrush
            });
        }
    }

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
            return;

        if (_shellHost?.CanGoUp == true)
        {
            _shellHost.GoUp();
            e.Handled = true;
            return;
        }

        HideRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
            return;

        if (ReferenceEquals(e.OriginalSource, Frame) ||
            ReferenceEquals(e.OriginalSource, TitleBar) ||
            ReferenceEquals(e.OriginalSource, TitleText) ||
            e.OriginalSource is Run)
        {
            if (_shellHost?.CanGoUp == true)
            {
                _shellHost.GoUp();
                e.Handled = true;
                return;
            }

            HideRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }
}
