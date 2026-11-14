using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace DesktopBox.Services.Shell;

/// <summary>
/// 宿主浏览器：实现 IShellBrowser + ICommDlgBrowser + IServiceProvider，
/// 用 OnDefaultCommand 拦截双击文件夹，在区内进入而不弹资源管理器。
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class MiniShellBrowser : IShellBrowser, ICommDlgBrowser, IServiceProvider
{
    private readonly IntPtr _hwndOwner;
    private IShellView? _activeView;

    public event Action<IntPtr, uint>? BrowseRequested;

    /// <summary>返回 true 表示已处理（文件夹区内导航）；false 交给系统默认（打开文件等）。</summary>
    public Func<IShellView, bool>? DefaultCommandHandler { get; set; }

    public MiniShellBrowser(IntPtr hwndOwner)
    {
        _hwndOwner = hwndOwner;
    }

    public void SetActiveView(IShellView? view) => _activeView = view;

    public void GetWindow(out IntPtr phwnd) => phwnd = _hwndOwner;

    public void ContextSensitiveHelp(bool fEnterMode) { }

    public void InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths) { }

    public void SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject) { }

    public void RemoveMenusSB(IntPtr hmenuShared) { }

    public void SetStatusTextSB(string pszStatusText) { }

    public void EnableModelessSB(bool fEnable) { }

    public void TranslateAcceleratorSB(IntPtr pmsg, ushort wID) { }

    public void BrowseObject(IntPtr pidl, uint wFlags)
    {
        BrowseRequested?.Invoke(pidl, wFlags);
    }

    public void GetViewStateStream(uint grfMode, out IStream ppStrm) =>
        throw new COMException(null, ShellViewNative.ENotImpl);

    public void GetControlWindow(uint id, out IntPtr phwnd) => phwnd = IntPtr.Zero;

    public int SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret)
    {
        pret = IntPtr.Zero;
        return ShellViewNative.ENotImpl;
    }

    public void QueryActiveShellView(out IShellView ppshv)
    {
        ppshv = _activeView ?? throw new COMException(null, ShellViewNative.EFail);
    }

    public void OnViewWindowActive(IShellView pshv) => _activeView = pshv;

    public void SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags) { }

    // --- ICommDlgBrowser ---

    public int OnDefaultCommand(IShellView ppshv)
    {
        try
        {
            if (DefaultCommandHandler?.Invoke(ppshv) == true)
                return ShellViewNative.SOk; // 已处理，禁止弹窗
        }
        catch
        {
            // fall through
        }

        return ShellViewNative.SFalse; // 文件等交给默认打开
    }

    public int OnStateChange(IShellView ppshv, uint uChange) => ShellViewNative.SOk;

    public int IncludeObject(IShellView ppshv, IntPtr pidl) => ShellViewNative.SOk;

    // --- IServiceProvider：让 DefView 能问到 ICommDlgBrowser ---

    public int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;
        try
        {
            if (riid == ShellViewGuids.IidICommDlgBrowser ||
                guidService == ShellViewGuids.SidSCommDlgBrowser ||
                (guidService == ShellViewGuids.SidSTopLevelBrowser && riid == ShellViewGuids.IidICommDlgBrowser))
            {
                ppvObject = Marshal.GetComInterfaceForObject(this, typeof(ICommDlgBrowser));
                return ShellViewNative.SOk;
            }

            if (riid == ShellViewGuids.IidIShellBrowser ||
                (guidService == ShellViewGuids.SidSTopLevelBrowser && riid == ShellViewGuids.IidIShellBrowser))
            {
                ppvObject = Marshal.GetComInterfaceForObject(this, typeof(IShellBrowser));
                return ShellViewNative.SOk;
            }

            if (riid == ShellViewGuids.IidIServiceProvider)
            {
                ppvObject = Marshal.GetComInterfaceForObject(this, typeof(IServiceProvider));
                return ShellViewNative.SOk;
            }
        }
        catch
        {
            ppvObject = IntPtr.Zero;
        }

        return ShellViewNative.ENoInterface;
    }
}
