using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace DesktopBox.Services.Shell;

internal static class ShellViewGuids
{
    public static readonly Guid IidIShellFolder = new("000214E6-0000-0000-C000-000000000046");
    public static readonly Guid IidIShellView = new("000214E3-0000-0000-C000-000000000046");
    public static readonly Guid IidIShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    public static readonly Guid IidIOleWindow = new("00000114-0000-0000-C000-000000000046");
    public static readonly Guid IidIColumnManager = new("d8ec27bb-3f3b-4042-b10a-4acfd924d453");
    public static readonly Guid IidICommDlgBrowser = new("000214F1-0000-0000-C000-000000000046");
    public static readonly Guid IidIFolderView = new("cde725b0-bcc9-4933-9539-313acf4f2df0");
    public static readonly Guid IidIServiceProvider = new("6d5140c1-7436-11ce-8034-00aa006009fa");
    public static readonly Guid SidSTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    public static readonly Guid SidSCommDlgBrowser = new("80d00800-bf40-11ce-90ba-00aa0060f876");
}

internal static class ShellAttr
{
    public const uint Folder = 0x20000000;
    public const uint Stream = 0x00400000;
    public const uint FileSystem = 0x40000000;
}

internal static class ShellPropertyKeys
{
    private static readonly Guid Storage = new("B725F130-47EF-101A-A5F1-02608C9EEBAC");

    public static PropertyKey ItemNameDisplay => new(Storage, 10);
    public static PropertyKey Size => new(Storage, 12);
    public static PropertyKey DateModified => new(Storage, 14);
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PropertyKey
{
    public Guid Fmtid;
    public uint Pid;

    public PropertyKey(Guid fmtid, uint pid)
    {
        Fmtid = fmtid;
        Pid = pid;
    }
}

internal static class ColumnManagerMasks
{
    public const uint Width = 0x00000001;
    public const uint State = 0x00000010;
    public const uint StateVisible = 0x00000001;
    public const uint StateFixedWidth = 0x00000002;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct CmColumnInfo
{
    public uint cbSize;
    public uint dwMask;
    public uint dwState;
    public uint uWidth;
    public uint uDefaultWidth;
    public uint uIdealWidth;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
    public string wszName;
}

[ComImport]
[Guid("d8ec27bb-3f3b-4042-b10a-4acfd924d453")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IColumnManager
{
    void SetColumnInfo(ref PropertyKey propkey, ref CmColumnInfo pcmci);
    void GetColumnInfo(ref PropertyKey propkey, ref CmColumnInfo pcmci);
    void GetColumnCount(uint dwFlags, out uint puCount);
    void GetColumns(uint dwFlags, IntPtr rgkey, uint cColumns);
    void SetColumns(IntPtr rgkey, uint cVisible);
}

internal static class ShellViewNative
{
    public const int SOk = 0;
    public const int SFalse = 1;
    public const int ENotImpl = unchecked((int)0x80004001);
    public const int ENoInterface = unchecked((int)0x80004002);
    public const int EFail = unchecked((int)0x80004005);

    public const uint SvuiaDeactivate = 0;
    public const uint SvuiaActivateNoFocus = 1;
    public const uint SvuiaActivateFocus = 2;
    public const uint SvuiaInPlaceActivate = 3;

    [DllImport("shell32.dll")]
    public static extern int SHGetDesktopFolder(out IntPtr ppshf);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHBindToObject(
        IntPtr psf,
        IntPtr pidl,
        IntPtr pbc,
        ref Guid riid,
        out IntPtr ppv);

    [DllImport("shell32.dll")]
    public static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll")]
    public static extern IntPtr ILClone(IntPtr pidl);

    [DllImport("shell32.dll")]
    public static extern IntPtr ILCloneFirst(IntPtr pidl);

    [DllImport("shell32.dll")]
    public static extern bool ILIsEmpty(IntPtr pidl);

    [DllImport("shell32.dll")]
    public static extern IntPtr ILGetNext(IntPtr pidl);

    [DllImport("ole32.dll")]
    public static extern void ReleaseStgMedium(ref STGMEDIUM pmedium);
}

[ComImport]
[Guid("000214E6-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellFolder
{
    void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
    void EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);
    void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
    void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
    void CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
    void GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);
    void GetUIObjectOf(IntPtr hwndOwner, uint cidl, IntPtr apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
    void GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
    void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
}

/// <summary>Win32 MSG，供 IShellView.TranslateAccelerator 使用。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Msg
{
    public IntPtr Hwnd;
    public int Message;
    public IntPtr WParam;
    public IntPtr LParam;
    public int Time;
    public int PtX;
    public int PtY;

    public static Msg FromInterop(System.Windows.Interop.MSG src) => new()
    {
        Hwnd = src.hwnd,
        Message = src.message,
        WParam = src.wParam,
        LParam = src.lParam,
        Time = src.time,
        PtX = src.pt_x,
        PtY = src.pt_y,
    };
}

[ComImport]
[Guid("000214E3-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellView
{
    // IOleWindow
    void GetWindow(out IntPtr phwnd);
    void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);

    // IShellView
    [PreserveSig]
    int TranslateAccelerator(ref Msg pmsg);
    void EnableModeless([MarshalAs(UnmanagedType.Bool)] bool fEnable);
    void UIActivate(uint uState);
    void Refresh();
    void CreateViewWindow(
        IShellView? psvPrevious,
        [In] ref FolderSettings pfs,
        [MarshalAs(UnmanagedType.Interface)] IShellBrowser psb,
        [In] ref ShellRect prcView,
        out IntPtr phWnd);
    void DestroyViewWindow();
    void GetCurrentInfo(out FolderSettings pfs);
    void AddPropertySheetPages(uint dwReserved, IntPtr pfn, IntPtr lparam);
    void SaveViewState();
    void SelectItem(IntPtr pidlItem, uint uFlags);
    void GetItemObject(uint uItem, ref Guid riid, out IntPtr ppv);
}

[ComImport]
[Guid("000214F1-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICommDlgBrowser
{
    [PreserveSig] int OnDefaultCommand([MarshalAs(UnmanagedType.Interface)] IShellView ppshv);
    [PreserveSig] int OnStateChange([MarshalAs(UnmanagedType.Interface)] IShellView ppshv, uint uChange);
    [PreserveSig] int IncludeObject([MarshalAs(UnmanagedType.Interface)] IShellView ppshv, IntPtr pidl);
}

[ComImport]
[Guid("6d5140c1-7436-11ce-8034-00aa006009fa")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IServiceProvider
{
    [PreserveSig]
    int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
}

[ComImport]
[Guid("cde725b0-bcc9-4933-9539-313acf4f2df0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFolderView
{
    void GetCurrentViewMode(out uint pViewMode);
    void SetCurrentViewMode(uint viewMode);
    void GetFolder(ref Guid riid, out IntPtr ppv);
    void Item(int iItemIndex, out IntPtr ppidl);
    void ItemCount(uint uFlags, out int pcItems);
    void Items(uint uFlags, ref Guid riid, out IntPtr ppv);
    void GetSelectionMarkedItem(out int piItem);
    void GetFocusedItem(out int piItem);
    void GetItemPosition(IntPtr pidl, out ShellPoint ppt);
    void GetSpacing(ref ShellPoint ppt);
    void GetDefaultSpacing(out ShellPoint ppt);
    [PreserveSig] int GetAutoArrange();
    void SelectItem(int iItem, uint dwFlags);
    void SelectAndPositionItems(uint cidl, IntPtr apidl, IntPtr apt, uint dwFlags);
}

[StructLayout(LayoutKind.Sequential)]
internal struct ShellPoint
{
    public int X;
    public int Y;
}

[ComImport]
[Guid("000214E2-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowser
{
    // IOleWindow
    void GetWindow(out IntPtr phwnd);
    void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);

    void InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
    void SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
    void RemoveMenusSB(IntPtr hmenuShared);
    void SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string pszStatusText);
    void EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool fEnable);
    void TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
    void BrowseObject(IntPtr pidl, uint wFlags);
    void GetViewStateStream(uint grfMode, out IStream ppStrm);
    void GetControlWindow(uint id, out IntPtr phwnd);
    [PreserveSig] int SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
    void QueryActiveShellView(out IShellView ppshv);
    void OnViewWindowActive(IShellView pshv);
    void SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags);
}
