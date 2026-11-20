using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopBox.Services;
using DesktopBox.Services.Shell;

namespace DesktopBox;

public partial class ZoneView : System.Windows.Controls.UserControl
{
    private static readonly SolidColorBrush TitleEnglishBrush = CreateFrozenBrush(0xA0, 0x7A, 0x58);
    private static readonly SolidColorBrush TitlePathBrush = CreateFrozenBrush(0x8C, 0x6E, 0x52);

    private ZoneConfig? _zone;
    private string _dropMode = "move";
    private ExplorerBrowserHost? _shellHost;
    private bool _shellEnsureQueued;

    public string ZoneId => _zone?.Id ?? "";

    /// <summary>当前 Shell 视图 HWND（供主窗转发滚轮/侧键）。</summary>
    public IntPtr ShellHostHwnd => _shellHost?.ViewHwnd ?? IntPtr.Zero;

    public ZoneView()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
        SizeChanged += (_, _) => QueueEnsureShellHost();
        MouseEnter += (_, _) => EnsureShellHost();
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
        DisposeService();

        _zone = zone;
        _dropMode = dropMode;
        _shellEnsureQueued = false;
        ApplyTitle(zone.Name, zone.English, relative: null);
        Directory.CreateDirectory(zone.Folder);
        ShellHostContainer.Children.Clear();

        // 有尺寸且可见时再挂 IShellView，避免三区同时创建拖垮内存
        if (IsVisible && ActualWidth > 40 && ActualHeight > 40)
            QueueEnsureShellHost();
    }

    public void Reload()
    {
        if (_shellHost is null)
            EnsureShellHost();
        else
            _shellHost.RefreshView();
    }

    public void DisposeService()
    {
        _shellEnsureQueued = false;
        if (_shellHost is not null)
        {
            _shellHost.Navigated -= OnShellNavigated;
            ShellHostContainer.Children.Remove(_shellHost);
            _shellHost.Dispose();
            _shellHost = null;
        }
    }

    private void QueueEnsureShellHost()
    {
        if (_shellHost is not null || _zone is null || _shellEnsureQueued)
            return;
        _shellEnsureQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, EnsureShellHost);
    }

    private void EnsureShellHost()
    {
        _shellEnsureQueued = false;
        if (_shellHost is not null || _zone is null)
            return;
        if (!IsVisible || ActualWidth < 40 || ActualHeight < 40)
            return;

        Directory.CreateDirectory(_zone.Folder);
        ZoneDragMoveForce.SetDropMode(_dropMode);
        _shellHost = new ExplorerBrowserHost(_zone.Folder);
        _shellHost.Navigated += OnShellNavigated;
        ShellHostContainer.Children.Clear();
        ShellHostContainer.Children.Add(_shellHost);
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

    private void ApplyTitle(string name, string english, string? relative)
    {
        TitleText.Inlines.Clear();
        TitleText.Inlines.Add(new Run(name));
        if (!string.IsNullOrWhiteSpace(english))
        {
            TitleText.Inlines.Add(new Run("  ") { Foreground = TitleEnglishBrush });
            TitleText.Inlines.Add(new Run(english.ToUpperInvariant())
            {
                Foreground = TitleEnglishBrush,
                FontSize = 11
            });
        }

        if (!string.IsNullOrWhiteSpace(relative))
        {
            TitleText.Inlines.Add(new Run("  ·  ") { Foreground = TitlePathBrush });
            TitleText.Inlines.Add(new Run(relative) { Foreground = TitlePathBrush, FontSize = 11 });
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
            QueueEnsureShellHost();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
            return;

        // 仅返回上级；隐藏改走托盘「显示桌面围栏」，避免双击后找不到恢复入口
        if (_shellHost?.CanGoUp == true)
        {
            _shellHost.GoUp();
            e.Handled = true;
        }
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        EnsureShellHost();
    }

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
