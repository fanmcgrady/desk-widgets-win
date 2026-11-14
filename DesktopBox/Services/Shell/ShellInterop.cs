using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace DesktopBox.Services.Shell;

internal static class ShellGuids
{
    public static readonly Guid ClsidExplorerBrowser = new("71F96385-DDD6-48D3-A0C1-AE06E8B055FB");
    public static readonly Guid IidExplorerBrowser = new("DFD3B6B5-C10C-4BE9-85F6-A66969F370A3");
}

internal static class ShellNative
{
    public const int SOk = 0;
    public const uint SbspAbsolute = 0x0000;
    public const uint SbspSameBrowser = 0x0001;
    public const uint SbspNewBrowser = 0x0002;
    public const uint SbspRelative = 0x1000;
    public const uint SbspParent = 0x2000;
    public const uint SbspNavigateBack = 0x4000;
    public const uint SbspNavigateForward = 0x8000;
    public const int SigdnFileSysPath = unchecked((int)0x80058000);

    public const int WsChild = 0x40000000;
    public const int WsVisible = 0x10000000;
    public const int WsClipSiblings = 0x04000000;
    public const int WsClipChildren = 0x02000000;

    public const uint SwpNomove = 0x0002;
    public const uint SwpNozorder = 0x0004;
    public const uint SwpNoactivate = 0x0010;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHParseDisplayName(
        string pszName,
        IBindCtx? pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("shell32.dll")]
    public static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll")]
    public static extern IntPtr ILCombine(IntPtr pidl1, IntPtr pidl2);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHGetNameFromIDList(IntPtr pidl, int sigdnName, out IntPtr ppszName);

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out ShellRect rc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public const uint LvmSetColumnWidth = 0x101E;

    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    public const uint ClsctxInprocServer = 0x1;
    public const uint ClsctxLocalServer = 0x4;
    public const uint ClsctxAll = 0x17;

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        out IntPtr ppv);
}

[StructLayout(LayoutKind.Sequential)]
internal struct ShellRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public ShellRect(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct FolderSettings
{
    public uint ViewMode;
    public uint FFlags;
}

internal enum FolderViewMode : uint
{
    Auto = 0xFFFFFFFF,
    First = 1,
    Icon = 1,
    SmallIcon = 2,
    List = 3,
    Details = 4,
    Thumbnail = 5,
    Tile = 6,
    ThumbStrip = 7,
    Content = 8
}

[Flags]
internal enum FolderFlags : uint
{
    None = 0,
    AutoArrange = 0x00000001,
    SnapToGrid = 0x00000004,
    BestFitWindow = 0x00000010,
    NoClientEdge = 0x00000200,
    ShowSelAlways = 0x00002000,
    NoHeaderInAllViews = 0x02000000,
    UseSearchFolder = 0x40000000
}

[Flags]
internal enum ExplorerBrowserOptions : uint
{
    None = 0,
    NavigateOnce = 0x00000001,
    ShowFrames = 0x00000002,
    AlwaysNavigate = 0x00000004,
    NoTravelLog = 0x00000008,
    NoWrapperWindow = 0x00000010,
    NoBorder = 0x00000040,
    NoPersistViewState = 0x00000080
}

[ComImport]
[Guid("DFD3B6B5-C10C-4BE9-85F6-A66969F370A3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IExplorerBrowser
{
    void Initialize(IntPtr hwndParent, [In] ref ShellRect prc, [In] ref FolderSettings pfs);
    void Destroy();
    void SetRect(IntPtr phdwp, ShellRect rcBrowser);
    void SetPropertyBag([MarshalAs(UnmanagedType.IUnknown)] object punk);
    void SetEmptyText([MarshalAs(UnmanagedType.LPWStr)] string pszEmptyText);
    void SetFolderSettings(ref FolderSettings pfs);
    void Advise(IExplorerBrowserEvents psbe, out uint pdwCookie);
    void Unadvise(uint dwCookie);
    void SetOptions(ExplorerBrowserOptions dwFlag);
    void GetOptions(out ExplorerBrowserOptions pdwFlag);
    void BrowseToIDList(IntPtr pidl, uint uFlags);
    void BrowseToObject([MarshalAs(UnmanagedType.IUnknown)] object punk, uint uFlags);
    void FillFromObject([MarshalAs(UnmanagedType.IUnknown)] object punk, uint dwFlags);
    void RemoveAll();
    void GetCurrentView(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
}

[ComImport]
[Guid("361BBDC7-E6EE-4E13-BE58-58E2240C810F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IExplorerBrowserEvents
{
    [PreserveSig]
    int OnNavigationPending(IntPtr pidlFolder);

    [PreserveSig]
    int OnViewCreated([MarshalAs(UnmanagedType.IUnknown)] object psv);

    [PreserveSig]
    int OnNavigationComplete(IntPtr pidlFolder);

    [PreserveSig]
    int OnNavigationFailed(IntPtr pidlFolder);
}
