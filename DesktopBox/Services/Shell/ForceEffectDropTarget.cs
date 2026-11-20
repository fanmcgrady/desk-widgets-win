using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace DesktopBox.Services.Shell;

/// <summary>
/// 自管 OLE 拖放：按配置把拖入文件移动或复制到当前分区目录。
/// 不依赖 SendInput / 源进程修饰键（跨盘时 Shell 默认常是复制）。
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class ForceEffectDropTarget : IOleDropTarget
{
    private const uint EffectNone = 0;
    private const uint EffectCopy = 1;
    private const uint EffectMove = 2;
    private const short CfHDrop = 15;

    private readonly Func<string> _getDestFolder;

    public ForceEffectDropTarget(Func<string> getDestFolder) => _getDestFolder = getDestFolder;

    public int DragEnter(ComIDataObject pDataObj, uint grfKeyState, PointL pt, ref uint pdwEffect)
    {
        ApplyPreferredEffect(ref pdwEffect);
        return 0;
    }

    public int DragOver(uint grfKeyState, PointL pt, ref uint pdwEffect)
    {
        ApplyPreferredEffect(ref pdwEffect);
        return 0;
    }

    public int DragLeave() => 0;

    public int Drop(ComIDataObject pDataObj, uint grfKeyState, PointL pt, ref uint pdwEffect)
    {
        var dest = _getDestFolder();
        if (string.IsNullOrWhiteSpace(dest))
        {
            pdwEffect = EffectNone;
            return unchecked((int)0x80004005);
        }

        var move = !ZoneDragMoveForce.PreferCopy;
        if (!TryTransfer(pDataObj, dest, move))
        {
            pdwEffect = EffectNone;
            return unchecked((int)0x80004005);
        }

        pdwEffect = move ? EffectMove : EffectCopy;
        return 0;
    }

    private static void ApplyPreferredEffect(ref uint pdwEffect)
    {
        var allowed = pdwEffect;
        if (allowed == EffectNone)
            return;

        if (ZoneDragMoveForce.PreferCopy)
        {
            if ((allowed & EffectCopy) != 0)
                pdwEffect = EffectCopy;
            else if ((allowed & EffectMove) != 0)
                pdwEffect = EffectMove;
            return;
        }

        if ((allowed & EffectMove) != 0)
            pdwEffect = EffectMove;
        else if ((allowed & EffectCopy) != 0)
            pdwEffect = EffectCopy;
    }

    private static bool TryTransfer(ComIDataObject data, string destFolder, bool move)
    {
        var paths = ExtractHDropPaths(data);
        if (paths.Count == 0)
            return false;

        Directory.CreateDirectory(destFolder);
        var any = false;
        foreach (var src in paths)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(src))
                    continue;

                var destRoot = Path.GetFullPath(destFolder).TrimEnd('\\') + '\\';
                var srcFull = Path.GetFullPath(src);
                if (srcFull.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(srcFull.TrimEnd('\\'), destRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = Path.GetFileName(srcFull.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(name))
                    continue;

                var dest = UniquePath(Path.Combine(destFolder, name));
                if (Directory.Exists(srcFull))
                {
                    if (move)
                        MoveDirectory(srcFull, dest);
                    else
                        CopyDirectory(srcFull, dest);
                    any = true;
                }
                else if (File.Exists(srcFull))
                {
                    if (move)
                        File.Move(srcFull, dest);
                    else
                        File.Copy(srcFull, dest);
                    any = true;
                }
            }
            catch
            {
                // 单个失败不阻断其余
            }
        }

        return any;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;

        var dir = Path.GetDirectoryName(path) ?? "";
        var baseName = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{baseName} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }

        return path;
    }

    private static void MoveDirectory(string src, string dest)
    {
        try
        {
            Directory.Move(src, dest);
        }
        catch (IOException)
        {
            CopyDirectory(src, dest);
            Directory.Delete(src, recursive: true);
        }
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: false);
        foreach (var sub in Directory.GetDirectories(src))
            CopyDirectory(sub, Path.Combine(dest, Path.GetFileName(sub)));
    }

    private static List<string> ExtractHDropPaths(ComIDataObject data)
    {
        var result = new List<string>();
        var fmt = new FORMATETC
        {
            cfFormat = CfHDrop,
            ptd = IntPtr.Zero,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL
        };

        STGMEDIUM medium;
        try
        {
            data.GetData(ref fmt, out medium);
        }
        catch
        {
            return result;
        }

        try
        {
            var hDrop = medium.unionmember;
            if (hDrop == IntPtr.Zero)
                return result;

            var count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
            var sb = new StringBuilder(520);
            for (uint i = 0; i < count; i++)
            {
                var chars = DragQueryFile(hDrop, i, null, 0);
                sb.EnsureCapacity((int)chars + 1);
                sb.Clear();
                DragQueryFile(hDrop, i, sb, (uint)sb.Capacity);
                if (sb.Length > 0)
                    result.Add(sb.ToString());
            }
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }

        return result;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM pmedium);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PointL
{
    public int X;
    public int Y;
}

[ComImport]
[Guid("00000122-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleDropTarget
{
    [PreserveSig]
    int DragEnter(
        [MarshalAs(UnmanagedType.Interface)] ComIDataObject pDataObj,
        uint grfKeyState,
        PointL pt,
        ref uint pdwEffect);

    [PreserveSig]
    int DragOver(uint grfKeyState, PointL pt, ref uint pdwEffect);

    [PreserveSig]
    int DragLeave();

    [PreserveSig]
    int Drop(
        [MarshalAs(UnmanagedType.Interface)] ComIDataObject pDataObj,
        uint grfKeyState,
        PointL pt,
        ref uint pdwEffect);
}

internal static class OleDropNative
{
    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int RegisterDragDrop(IntPtr hwnd, IOleDropTarget pDropTarget);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int RevokeDragDrop(IntPtr hwnd);
}
