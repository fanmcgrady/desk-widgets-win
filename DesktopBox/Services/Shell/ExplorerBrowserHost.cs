using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopBox.Services;

namespace DesktopBox.Services.Shell;

/// <summary>
/// WPF HwndHost：通过 IShellFolder/IShellView 嵌入原生文件夹图标视图。
/// CreateViewWindow 必须在 BuildWindowCore 返回后异步调用，避免 Dispatcher 重入。
/// </summary>
internal sealed class ExplorerBrowserHost : HwndHost
{
    private readonly string _rootPath;
    private IntPtr _hwndHost = IntPtr.Zero;
    private MiniShellBrowser? _browser;
    private IShellView? _shellView;
    private IntPtr _hwndView = IntPtr.Zero;
    private IShellFolder? _folder;
    private bool _hostReady;
    private string _currentPath;
    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private int _lastViewW = -1;
    private int _lastViewH = -1;
    private int _lastColumnsW = -1;

    /// <summary>当前路径变化（区内导航）。</summary>
    public event EventHandler<string>? Navigated;

    public string RootPath => _rootPath;
    public string CurrentPath => _currentPath;
    public IntPtr ViewHwnd => _hwndView;
    public bool CanGoUp => !IsSamePath(_currentPath, _rootPath);
    public bool CanGoBack => _historyIndex > 0;
    public bool CanGoForward => _historyIndex >= 0 && _historyIndex < _history.Count - 1;

    public void GoBack() => NavigateHistory(-1);
    public void GoForward() => NavigateHistory(1);

    public void ForwardMouseWheel(int msg, IntPtr wParam, IntPtr lParam)
    {
        var delta = (short)(((int)wParam >> 16) & 0xFFFF);
        if (msg == 0x020E)
            ScrollHorizontal(delta);
        else
            ScrollVertical(delta);
    }

    public IntPtr GetListViewHwndPublic() => GetListViewHwnd();

    public void ScrollVertical(int wheelDelta)
    {
        var listView = GetListViewHwnd();
        if (listView == IntPtr.Zero)
        {
            Log("ScrollVertical: no SysListView32");
            return;
        }

        ShellWheelHook.ScrollListView(listView, wheelDelta, horizontal: false);
    }

    public void ScrollHorizontal(int wheelDelta)
    {
        var listView = GetListViewHwnd();
        if (listView == IntPtr.Zero)
            return;
        ShellWheelHook.ScrollListView(listView, wheelDelta, horizontal: true);
    }

    /// <summary>最近一次点击获焦的区；主窗键盘钩子优先转给它。</summary>
    public static ExplorerBrowserHost? LastActive { get; private set; }

    /// <summary>点击区内后让列表获焦，Delete/方向键才有效。</summary>
    public void ActivateForInput()
    {
        try
        {
            LastActive = this;

            // ShowSelAlways 会让未获焦时也显示选中，必须真正 SetFocus 键盘才有反应
            var top = _hwndHost != IntPtr.Zero
                ? GetAncestor(_hwndHost, GaRoot)
                : IntPtr.Zero;
            if (top != IntPtr.Zero)
            {
                if (GetForegroundWindow() != top)
                    TryForceForeground(top);
            }

            _shellView?.UIActivate(ShellViewNative.SvuiaActivateFocus);

            // 先让 HwndHost 进入 WPF 键盘路由，再把 Win32 焦点落到 SysListView32
            Focus();
            FocusListView();

            // WPF 可能在同一次点击后抢回焦点：再补一次
            var hwndView = _hwndView;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (_hwndView != hwndView || _hwndView == IntPtr.Zero)
                    return;
                try
                {
                    LastActive = this;
                    _shellView?.UIActivate(ShellViewNative.SvuiaActivateFocus);
                    FocusListView();
                }
                catch { /* ignore */ }
            });
        }
        catch (Exception ex)
        {
            Log($"ActivateForInput: {ex.Message}");
        }
    }

    private void FocusListView()
    {
        var target = GetListViewHwnd();
        if (target == IntPtr.Zero)
            target = _hwndView;
        if (target != IntPtr.Zero)
            SetFocus(target);
    }

    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkBack = 0x08;

    /// <summary>
    /// 把键盘消息交给 IShellView（Delete/F2/Ctrl+A 等）。
    /// 嵌入视图里 SysListView32 本身往往不处理这些键。
    /// Backspace：资源管理器里是历史后退；嵌入视图不会自动走，这里自己处理。
    /// </summary>
    public bool TryTranslateAccelerator(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam)
    {
        if ((message is WmKeyDown or WmSysKeyDown)
            && ((int)wParam & 0xFFFF) == VkBack)
        {
            if (CanGoBack)
            {
                GoBack();
                return true;
            }

            if (CanGoUp)
            {
                GoUp();
                return true;
            }
        }

        if (_shellView is null)
            return false;
        try
        {
            var shellMsg = new Msg
            {
                Hwnd = hwnd,
                Message = message,
                WParam = wParam,
                LParam = lParam,
                Time = 0,
                PtX = 0,
                PtY = 0,
            };
            return _shellView.TranslateAccelerator(ref shellMsg) == ShellNative.SOk;
        }
        catch (Exception ex)
        {
            Log($"TryTranslateAccelerator: {ex.Message}");
            return false;
        }
    }

    /// <summary>WPF 持有键盘路由时，把加速键交给 Shell 视图（Delete/F2/方向键）。</summary>
    protected override bool TranslateAcceleratorCore(ref MSG msg, ModifierKeys modifiers)
    {
        if (TryTranslateAccelerator(msg.hwnd, msg.message, msg.wParam, msg.lParam))
            return true;
        return base.TranslateAcceleratorCore(ref msg, modifiers);
    }

    protected override bool TabIntoCore(TraversalRequest request)
    {
        ActivateForInput();
        return true;
    }

    private IntPtr GetListViewHwnd()
    {
        if (_hwndView == IntPtr.Zero)
            return IntPtr.Zero;
        return FindDescendantByClass(_hwndView, "SysListView32");
    }

    private static void TryForceForeground(IntPtr hwnd)
    {
        var fg = GetForegroundWindow();
        if (fg == hwnd)
            return;
        var fgThread = GetWindowThreadProcessId(fg, out _);
        var ourThread = GetWindowThreadProcessId(hwnd, out _);
        var attached = false;
        try
        {
            if (fgThread != 0 && ourThread != 0 && fgThread != ourThread)
                attached = AttachThreadInput(fgThread, ourThread, true);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
                AttachThreadInput(fgThread, ourThread, false);
        }
    }

    private const uint GaRoot = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    public ExplorerBrowserHost(string folderPath)
    {
        _rootPath = Path.GetFullPath(folderPath);
        _currentPath = _rootPath;
        Focusable = true;
    }

    public void Navigate(string folderPath, bool addHistory = true)
    {
        var full = Path.GetFullPath(folderPath);
        if (!IsSameOrUnder(_rootPath, full))
        {
            Log($"navigate blocked outside root: {full}");
            return;
        }

        if (!Directory.Exists(full))
            Directory.CreateDirectory(full);

        if (addHistory)
            PushHistory(full);
        else
            _currentPath = full;

        if (_hostReady)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => RecreateView(full));
        else
            _currentPath = full;
    }

    public void GoUp()
    {
        if (!CanGoUp)
            return;
        var parent = Directory.GetParent(_currentPath)?.FullName;
        if (parent is null || !IsSameOrUnder(_rootPath, parent))
            Navigate(_rootPath);
        else
            Navigate(parent);
    }

    public void RefreshView()
    {
        if (!_hostReady || string.IsNullOrWhiteSpace(_currentPath))
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            try
            {
                _shellView?.Refresh();
            }
            catch
            {
                RecreateView(_currentPath);
            }
        });
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        Log($"BuildWindowCore parent={hwndParent.Handle} folder={_rootPath}");
        Directory.CreateDirectory(_rootPath);

        var style = ShellNative.WsChild | ShellNative.WsVisible | ShellNative.WsClipSiblings | ShellNative.WsClipChildren;
        _hwndHost = ShellNative.CreateWindowEx(
            0,
            "Static",
            "",
            style,
            0, 0, 200, 200,
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_hwndHost == IntPtr.Zero)
            throw new InvalidOperationException($"无法创建宿主窗口 err={Marshal.GetLastWin32Error()}");

        _browser = new MiniShellBrowser(_hwndHost);
        _browser.BrowseRequested += OnBrowseRequested;
        _browser.DefaultCommandHandler = TryHandleDefaultCommand;
        _hostReady = true;

        // 必须延后：CreateViewWindow 会泵消息，不能在 BuildWindowCore 里跑
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            try
            {
                RecreateView(_currentPath);
            }
            catch (Exception ex)
            {
                Log($"CreateView exception: {ex}");
            }
        });

        return new HandleRef(this, _hwndHost);
    }

    private void OnBrowseRequested(IntPtr pidl, uint wFlags)
    {
        Log($"BrowseObject flags=0x{wFlags:X8}");
        // CreateViewWindow/Browse 可能在 Shell 回调里，延后到 Dispatcher
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            try
            {
                HandleBrowseObject(pidl, wFlags);
            }
            catch (Exception ex)
            {
                Log($"BrowseObject failed: {ex.GetType().Name} {ex.Message}");
            }
        });
    }

    /// <summary>
    /// ICommDlgBrowser.OnDefaultCommand：双击/回车时若是文件夹则区内导航。
    /// 必须同步返回 true，否则 DefView 仍会弹出资源管理器。
    /// </summary>
    private bool TryHandleDefaultCommand(IShellView view)
    {
        IntPtr pfv = IntPtr.Zero;
        IntPtr pidlChild = IntPtr.Zero;
        try
        {
            var unk = Marshal.GetIUnknownForObject(view);
            try
            {
                var iid = ShellViewGuids.IidIFolderView;
                var hr = Marshal.QueryInterface(unk, ref iid, out pfv);
                if (hr != ShellNative.SOk || pfv == IntPtr.Zero)
                {
                    Log("OnDefaultCommand: no IFolderView");
                    return false;
                }
            }
            finally
            {
                Marshal.Release(unk);
            }

            var folderView = (IFolderView)Marshal.GetTypedObjectForIUnknown(pfv, typeof(IFolderView));
            folderView.ItemCount(1 /* SVGIO_SELECTION */, out var selected);
            if (selected <= 0)
            {
                Log("OnDefaultCommand: empty selection");
                return false;
            }

            int index;
            try
            {
                folderView.GetFocusedItem(out index);
            }
            catch
            {
                folderView.GetSelectionMarkedItem(out index);
            }

            folderView.Item(index, out pidlChild);
            if (pidlChild == IntPtr.Zero)
                return false;

            var path = ResolveChildItemPath(pidlChild);
            if (string.IsNullOrWhiteSpace(path))
            {
                Log("OnDefaultCommand: path resolve failed");
                return false;
            }

            if (!Directory.Exists(path))
            {
                Log($"OnDefaultCommand: not folder, default open ({path})");
                return false;
            }

            if (!IsSameOrUnder(_rootPath, path))
            {
                Log($"OnDefaultCommand: outside root blocked ({path})");
                return false;
            }

            Log($"OnDefaultCommand: in-place -> {path}");
            Navigate(path);
            return true;
        }
        catch (Exception ex)
        {
            Log($"OnDefaultCommand error: {ex.GetType().Name} {ex.Message}");
            return false;
        }
        finally
        {
            if (pidlChild != IntPtr.Zero)
                ShellNative.ILFree(pidlChild);
            if (pfv != IntPtr.Zero)
                Marshal.Release(pfv);
        }
    }

    private string? ResolveChildItemPath(IntPtr pidlChild)
    {
        var hr = ShellNative.SHParseDisplayName(_currentPath, null, out var currentPidl, 0, out _);
        if (hr != ShellNative.SOk || currentPidl == IntPtr.Zero)
            return null;

        IntPtr absolute = IntPtr.Zero;
        try
        {
            absolute = ShellNative.ILCombine(currentPidl, pidlChild);
            if (absolute == IntPtr.Zero)
                return null;
            return GetPathFromIdList(absolute);
        }
        finally
        {
            ShellNative.ILFree(currentPidl);
            if (absolute != IntPtr.Zero)
                ShellNative.ILFree(absolute);
        }
    }

    private void HandleBrowseObject(IntPtr pidl, uint wFlags)
    {
        if ((wFlags & ShellNative.SbspNavigateBack) != 0)
        {
            NavigateHistory(-1);
            return;
        }

        if ((wFlags & ShellNative.SbspNavigateForward) != 0)
        {
            NavigateHistory(1);
            return;
        }

        if ((wFlags & ShellNative.SbspParent) != 0)
        {
            GoUp();
            return;
        }

        var path = ResolveBrowsePath(pidl, wFlags);
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (Directory.Exists(path))
            Navigate(path);
        else
            Log($"BrowseObject non-folder ignored: {path}");
    }

    private string? ResolveBrowsePath(IntPtr pidl, uint wFlags)
    {
        if (pidl == IntPtr.Zero && (wFlags & ShellNative.SbspRelative) == 0)
            return null;

        IntPtr absolute = IntPtr.Zero;
        try
        {
            if ((wFlags & ShellNative.SbspRelative) != 0 && pidl != IntPtr.Zero)
            {
                var hr = ShellNative.SHParseDisplayName(_currentPath, null, out var currentPidl, 0, out _);
                if (hr != ShellNative.SOk || currentPidl == IntPtr.Zero)
                    return null;
                try
                {
                    absolute = ShellNative.ILCombine(currentPidl, pidl);
                }
                finally
                {
                    ShellNative.ILFree(currentPidl);
                }
            }
            else if (pidl != IntPtr.Zero)
            {
                absolute = ShellViewNative.ILClone(pidl);
            }

            if (absolute == IntPtr.Zero)
                return null;

            return GetPathFromIdList(absolute);
        }
        finally
        {
            if (absolute != IntPtr.Zero)
                ShellNative.ILFree(absolute);
        }
    }

    private static string? GetPathFromIdList(IntPtr pidl)
    {
        var hr = ShellNative.SHGetNameFromIDList(pidl, ShellNative.SigdnFileSysPath, out var pName);
        if (hr != ShellNative.SOk || pName == IntPtr.Zero)
            return null;
        try
        {
            return Marshal.PtrToStringUni(pName);
        }
        finally
        {
            ShellNative.CoTaskMemFree(pName);
        }
    }

    private void PushHistory(string full)
    {
        if (_historyIndex >= 0 &&
            _historyIndex < _history.Count &&
            IsSamePath(_history[_historyIndex], full))
        {
            _currentPath = full;
            return;
        }

        if (_historyIndex < _history.Count - 1)
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);

        _history.Add(full);
        _historyIndex = _history.Count - 1;
        _currentPath = full;
    }

    private void NavigateHistory(int delta)
    {
        var next = _historyIndex + delta;
        if (next < 0 || next >= _history.Count)
            return;
        _historyIndex = next;
        var path = _history[_historyIndex];
        _currentPath = path;
        if (_hostReady)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => RecreateView(path));
    }

    private static bool IsSamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrUnder(string root, string path)
    {
        root = Path.GetFullPath(root);
        path = Path.GetFullPath(path);
        if (IsSamePath(root, path))
            return true;
        var prefix = root.EndsWith('\\') ? root : root + '\\';
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _hostReady = false;
        if (_browser is not null)
        {
            _browser.BrowseRequested -= OnBrowseRequested;
            _browser = null;
        }
        DestroyView();
        if (_hwndHost != IntPtr.Zero)
        {
            ShellNative.DestroyWindow(_hwndHost);
            _hwndHost = IntPtr.Zero;
        }
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        // 先让 WPF 把宿主 Static 摆到正确的设备像素大小
        base.OnWindowPositionChanged(rcBoundingBox);
        if (_hwndHost == IntPtr.Zero)
            return;

        // DefView/ListView 必须等于宿主客户区。若再乘 DpiScale 会大一倍：
        // 列表自认一页装得下 → 无滚动条，但画面被裁切。
        ShellNative.GetClientRect(_hwndHost, out var client);
        var w = Math.Max(1, client.Right - client.Left);
        var h = Math.Max(1, client.Bottom - client.Top);
        if (w < 8 || h < 8)
            return;

        var sizeChanged = w != _lastViewW || h != _lastViewH;
        if (sizeChanged && _hwndView != IntPtr.Zero)
        {
            ShellNative.SetWindowPos(
                _hwndView,
                IntPtr.Zero,
                0, 0, w, h,
                ShellNative.SwpNozorder | ShellNative.SwpNoactivate);

            var listView = FindDescendantByClass(_hwndView, "SysListView32");
            if (listView != IntPtr.Zero)
            {
                ShellNative.SetWindowPos(
                    listView,
                    IntPtr.Zero,
                    0, 0, w, h,
                    ShellNative.SwpNozorder | ShellNative.SwpNoactivate);
            }

            _lastViewW = w;
            _lastViewH = h;
        }

        // 列宽只在横向尺寸变化时重算，避免定时重摆把列表刷闪
        if (_shellView is not null && w != _lastColumnsW)
        {
            ApplyDetailsColumns(_shellView, w);
            ApplyColumnLayout(_hwndView, w);
            _lastColumnsW = w;
        }
    }

    private int ReadHostClientWidth()
    {
        if (_hwndHost == IntPtr.Zero)
            return 0;
        ShellNative.GetClientRect(_hwndHost, out var rc);
        return Math.Max(0, rc.Right - rc.Left);
    }

    private void ApplyColumnLayout(IntPtr hwndView, int viewWidthPx)
    {
        ApplyEvenListViewColumns(hwndView, viewWidthPx);
    }

    private void RecreateView(string folder)
    {
        if (_hwndHost == IntPtr.Zero || _browser is null)
            return;

        DestroyView();
        Directory.CreateDirectory(folder);

        var hr = ShellNative.SHParseDisplayName(folder, null, out var pidl, 0, out _);
        if (hr != ShellNative.SOk || pidl == IntPtr.Zero)
            throw new COMException($"SHParseDisplayName 失败: {folder}", hr);

        IntPtr pFolder = IntPtr.Zero;
        try
        {
            var iidFolder = ShellViewGuids.IidIShellFolder;
            hr = ShellViewNative.SHBindToObject(IntPtr.Zero, pidl, IntPtr.Zero, ref iidFolder, out pFolder);
            if (hr != ShellNative.SOk || pFolder == IntPtr.Zero)
                throw new COMException($"SHBindToObject 失败 hr=0x{hr:X8}", hr);

            _folder = (IShellFolder)Marshal.GetTypedObjectForIUnknown(pFolder, typeof(IShellFolder));

            var iidView = ShellViewGuids.IidIShellView;
            _folder.CreateViewObject(_hwndHost, ref iidView, out var pView);
            if (pView == IntPtr.Zero)
                throw new COMException("CreateViewObject 返回空");

            try
            {
                _shellView = (IShellView)Marshal.GetTypedObjectForIUnknown(pView, typeof(IShellView));
            }
            finally
            {
                Marshal.Release(pView);
            }

            ShellNative.GetClientRect(_hwndHost, out var client);
            if (client.Right - client.Left < 8 || client.Bottom - client.Top < 8)
                client = new ShellRect(0, 0, 200, 200);

            var settings = new FolderSettings
            {
                ViewMode = (uint)FolderViewMode.Details,
                FFlags = (uint)(FolderFlags.AutoArrange | FolderFlags.NoClientEdge | FolderFlags.ShowSelAlways)
            };

            _browser.SetActiveView(_shellView);
            _shellView.CreateViewWindow(null, ref settings, _browser, ref client, out _hwndView);
            _shellView.UIActivate(ShellViewNative.SvuiaActivateNoFocus);
            ZoneDragMoveForce.RegisterView(_hwndView, this);
            if (_hwndHost != IntPtr.Zero)
                ZoneDragMoveForce.RegisterAlias(_hwndHost, this);
            var listView = FindDescendantByClass(_hwndView, "SysListView32");
            if (listView != IntPtr.Zero)
                ZoneDragMoveForce.RegisterAlias(listView, this);

            var viewW = client.Right - client.Left;
            ApplyDetailsColumns(_shellView, viewW);
            ApplyColumnLayout(_hwndView, viewW);
            var hwndView = _hwndView;
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                if (_hwndView == hwndView && hwndView != IntPtr.Zero)
                {
                    ApplyColumnLayout(hwndView, ReadHostClientWidth());
                    var lv = FindDescendantByClass(hwndView, "SysListView32");
                    if (lv != IntPtr.Zero)
                        ZoneDragMoveForce.RegisterAlias(lv, this);
                }
            });
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (_hwndView == hwndView && hwndView != IntPtr.Zero)
                    ApplyColumnLayout(hwndView, ReadHostClientWidth());
            });
            _currentPath = Path.GetFullPath(folder);
            if (_history.Count == 0)
            {
                _history.Add(_currentPath);
                _historyIndex = 0;
            }
            Navigated?.Invoke(this, _currentPath);
            Log($"view ready folder={folder} hwndView={_hwndView} listView={listView}");
        }
        finally
        {
            ShellNative.ILFree(pidl);
            if (pFolder != IntPtr.Zero)
                Marshal.Release(pFolder);
        }
    }

    private static void ApplyDetailsColumns(IShellView view, int viewWidthPx)
    {
        try
        {
            var iid = ShellViewGuids.IidIColumnManager;
            IntPtr pcm = IntPtr.Zero;

            // 优先从 DefView 背景对象取；失败再直接 QI IShellView
            try
            {
                view.GetItemObject(0 /* SVGIO_BACKGROUND */, ref iid, out pcm);
            }
            catch
            {
                pcm = IntPtr.Zero;
            }

            if (pcm == IntPtr.Zero)
            {
                var unk = Marshal.GetIUnknownForObject(view);
                try
                {
                    var hr = Marshal.QueryInterface(unk, ref iid, out pcm);
                    if (hr != ShellNative.SOk || pcm == IntPtr.Zero)
                    {
                        Log($"ApplyDetailsColumns QI hr=0x{hr:X8}");
                        return;
                    }
                }
                finally
                {
                    Marshal.Release(unk);
                }
            }

            try
            {
                var columns = (IColumnManager)Marshal.GetTypedObjectForIUnknown(pcm, typeof(IColumnManager));
                var keys = new[]
                {
                    ShellPropertyKeys.ItemNameDisplay,
                    ShellPropertyKeys.DateModified,
                    ShellPropertyKeys.Size
                };
                var pin = GCHandle.Alloc(keys, GCHandleType.Pinned);
                try
                {
                    columns.SetColumns(pin.AddrOfPinnedObject(), (uint)keys.Length);
                }
                finally
                {
                    pin.Free();
                }

                // 名称 50% / 日期 25% / 大小 25%，铺满可视宽度；不锁宽，可拖
                var totalChars = (uint)Math.Max(40, viewWidthPx / 6);
                var nameChars = Math.Max(16u, totalChars / 2);
                var dateChars = Math.Max(10u, totalChars / 4);
                var sizeChars = Math.Max(10u, totalChars - nameChars - dateChars);
                SetColumnWidth(columns, ShellPropertyKeys.ItemNameDisplay, nameChars);
                SetColumnWidth(columns, ShellPropertyKeys.DateModified, dateChars);
                SetColumnWidth(columns, ShellPropertyKeys.Size, sizeChars);
            }
            finally
            {
                Marshal.Release(pcm);
            }
        }
        catch (Exception ex)
        {
            Log($"ApplyDetailsColumns skipped: {ex.GetType().Name} {ex.Message}");
        }
    }

    private static void SetColumnWidth(IColumnManager columns, PropertyKey key, uint widthChars)
    {
        var info = new CmColumnInfo
        {
            cbSize = (uint)Marshal.SizeOf<CmColumnInfo>(),
            dwMask = ColumnManagerMasks.Width | ColumnManagerMasks.State,
            dwState = ColumnManagerMasks.StateVisible,
            uWidth = widthChars,
            uDefaultWidth = widthChars,
            uIdealWidth = widthChars,
            wszName = string.Empty
        };
        columns.SetColumnInfo(ref key, ref info);
    }

    /// <summary>按像素把 SysListView32 三列设为 50%/25%/25%，铺满可视宽度；仍可拖表头改宽。</summary>
    private static void ApplyEvenListViewColumns(IntPtr hwndView, int viewWidthPx)
    {
        if (hwndView == IntPtr.Zero || viewWidthPx < 80)
            return;

        try
        {
            var listView = FindDescendantByClass(hwndView, "SysListView32");
            if (listView == IntPtr.Zero)
                return;

            // 去掉纵向滚动条约占位，三列合计铺满
            var usable = Math.Max(120, viewWidthPx - GetVerticalScrollBarWidth(listView));
            var w0 = usable / 2;          // 名称 50%
            var w1 = usable / 4;          // 修改日期 25%
            var w2 = usable - w0 - w1;    // 大小 25%（吃掉余数）
            ShellNative.SendMessage(listView, ShellNative.LvmSetColumnWidth, (IntPtr)0, (IntPtr)w0);
            ShellNative.SendMessage(listView, ShellNative.LvmSetColumnWidth, (IntPtr)1, (IntPtr)w1);
            ShellNative.SendMessage(listView, ShellNative.LvmSetColumnWidth, (IntPtr)2, (IntPtr)w2);
        }
        catch (Exception ex)
        {
            Log($"ApplyEvenListViewColumns skipped: {ex.GetType().Name} {ex.Message}");
        }
    }

    private static int GetVerticalScrollBarWidth(IntPtr listView)
    {
        const int gwlStyle = -16;
        const int wsVScroll = 0x00200000;
        var style = GetWindowLongPtr(listView, gwlStyle);
        if ((style.ToInt64() & wsVScroll) == 0)
            return 8; // 预留一点边
        return GetSystemMetrics(15 /* SM_CXVSCROLL */) + 4;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : (IntPtr)GetWindowLong32(hWnd, nIndex);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private static IntPtr FindDescendantByClass(IntPtr root, string className)
    {
        var queue = new Queue<IntPtr>();
        queue.Enqueue(root);
        var sb = new System.Text.StringBuilder(256);

        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            var child = IntPtr.Zero;
            while (true)
            {
                child = ShellNative.FindWindowEx(parent, child, null, null);
                if (child == IntPtr.Zero)
                    break;

                sb.Clear();
                ShellNative.GetClassName(child, sb, sb.Capacity);
                if (string.Equals(sb.ToString(), className, StringComparison.OrdinalIgnoreCase))
                    return child;

                queue.Enqueue(child);
            }
        }

        return IntPtr.Zero;
    }

    private void DestroyView()
    {
        if (_hwndView != IntPtr.Zero)
        {
            ZoneDragMoveForce.UnregisterView(_hwndView);
            _hwndView = IntPtr.Zero;
        }

        if (_shellView is not null)
        {
            try { _shellView.UIActivate(ShellViewNative.SvuiaDeactivate); } catch { /* ignore */ }
            try { _shellView.DestroyViewWindow(); } catch { /* ignore */ }
            try { Marshal.FinalReleaseComObject(_shellView); } catch { /* ignore */ }
            _shellView = null;
        }

        _browser?.SetActiveView(null);

        if (_folder is not null)
        {
            try { Marshal.FinalReleaseComObject(_folder); } catch { /* ignore */ }
            _folder = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hostReady = false;
            DestroyView();
        }

        base.Dispose(disposing);
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.ConfigDirectory);
            File.AppendAllText(
                Path.Combine(AppConfig.ConfigDirectory, "debug.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
        }
        catch
        {
            // ignore
        }
    }
}
