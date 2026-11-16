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

    public event EventHandler? HideRequested;

    public string ZoneId => _zone?.Id ?? "";

    /// <summary>当前 Shell 视图 HWND（供主窗转发滚轮/侧键）。</summary>
    public IntPtr ShellHostHwnd => _shellHost?.ViewHwnd ?? IntPtr.Zero;

    public ZoneView()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
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
    }

    public void Reload() => _shellHost?.RefreshView();

    public void DisposeService()
    {
        if (_shellHost is not null)
        {
            _shellHost.Navigated -= OnShellNavigated;
            ShellHostContainer.Children.Remove(_shellHost);
            _shellHost.Dispose();
            _shellHost = null;
        }
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
            _shellHost?.RefreshView();
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

        // 根目录双击标题 → 隐藏围栏（桌面双击再显示）
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
