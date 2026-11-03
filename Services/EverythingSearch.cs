using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DeskWidgets.Services;

public sealed record EverythingHit(string Name, string Directory, string FullPath, bool IsFolder);

/// <summary>通过 Everything 1.4 的窗口消息查本机文件。Everything 要在当前登录会话里运行。</summary>
public static class EverythingSearch
{
    private const int WmCopyData = 0x004A;
    private const int QueryUnicode = 2;
    private const int ReplyId = 0x4F51;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int MaxResults = 100;

    private static ReplyWindow? _reply;

    public static bool IsRunning() => FindIpcWindow() != IntPtr.Zero;

    public static string? InstalledExe()
    {
        var fixedPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Everything",
            "Everything.exe");
        if (File.Exists(fixedPath)) return fixedPath;
        return null;
    }

    /// <summary>拉到托盘，不弹出搜索窗。</summary>
    public static bool TryStart()
    {
        var exe = InstalledExe();
        if (exe == null) return false;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe,
            Arguments = "-startup",
            UseShellExecute = true,
        });
        return true;
    }

    public static IReadOnlyList<EverythingHit> Query(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return Array.Empty<EverythingHit>();

        var ipc = FindIpcWindow();
        if (ipc == IntPtr.Zero)
            throw new InvalidOperationException("Everything 没在运行");

        _reply ??= new ReplyWindow();
        _reply.Ensure();
        _reply.Hits = new List<EverythingHit>();
        _reply.GotReply = false;

        var chars = text.Length;
        var size = 20 + (chars + 1) * 2;
        var query = Marshal.AllocHGlobal(size);
        var cdsMem = Marshal.AllocHGlobal(Marshal.SizeOf<CopyData>());
        try
        {
            Marshal.WriteInt32(query, 0, unchecked((int)_reply.Handle.ToInt64()));
            Marshal.WriteInt32(query, 4, ReplyId);
            Marshal.WriteInt32(query, 8, 0);
            Marshal.WriteInt32(query, 12, 0);
            Marshal.WriteInt32(query, 16, MaxResults);
            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            Marshal.Copy(bytes, 0, query + 20, bytes.Length);

            var cds = new CopyData
            {
                dwData = (IntPtr)QueryUnicode,
                cbData = size,
                lpData = query,
            };
            Marshal.StructureToPtr(cds, cdsMem, false);
            var sent = SendMessageTimeout(
                ipc,
                WmCopyData,
                _reply.Handle,
                cdsMem,
                SmtoAbortIfHung,
                5000,
                out _);
            var until = Environment.TickCount64 + 2000;
            while (!_reply.GotReply && Environment.TickCount64 < until)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
            if (sent == IntPtr.Zero && !_reply.GotReply)
                throw new InvalidOperationException("查询没有返回");
        }
        finally
        {
            Marshal.FreeHGlobal(cdsMem);
            Marshal.FreeHGlobal(query);
        }

        return _reply.Hits;
    }

    private static IntPtr FindIpcWindow()
    {
        var hwnd = FindWindow("EVERYTHING_IPC_WNDCLASS", null);
        if (hwnd != IntPtr.Zero) return hwnd;
        return FindWindow("EVERYTHING_TASKBAR_NOTIFICATION", null);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    [StructLayout(LayoutKind.Sequential)]
    private struct CopyData
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    private sealed class ReplyWindow : NativeWindow
    {
        public List<EverythingHit> Hits { get; set; } = new();
        public bool GotReply { get; set; }

        public void Ensure()
        {
            if (Handle != IntPtr.Zero) return;
            var cp = new CreateParams
            {
                Caption = "DeskWidgetsEverything",
                Parent = new IntPtr(-3),
            };
            CreateHandle(cp);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmCopyData)
            {
                var cds = Marshal.PtrToStructure<CopyData>(m.LParam);
                if (cds.dwData.ToInt32() == ReplyId)
                {
                    GotReply = true;
                    Hits = cds.lpData != IntPtr.Zero && cds.cbData >= 28
                        ? Parse(cds.lpData, cds.cbData)
                        : new List<EverythingHit>();
                    m.Result = (IntPtr)1;
                    return;
                }
            }
            base.WndProc(ref m);
        }

        private static List<EverythingHit> Parse(IntPtr data, int cb)
        {
            var raw = new byte[cb];
            Marshal.Copy(data, raw, 0, cb);
            var count = BitConverter.ToInt32(raw, 20);
            var list = new List<EverythingHit>(Math.Max(0, count));
            for (var i = 0; i < count; i++)
            {
                var at = 28 + i * 12;
                if (at + 12 > cb) break;
                var flags = BitConverter.ToUInt32(raw, at);
                var nameOff = BitConverter.ToInt32(raw, at + 4);
                var pathOff = BitConverter.ToInt32(raw, at + 8);
                var name = ReadUtf16(raw, nameOff);
                var dir = ReadUtf16(raw, pathOff);
                if (string.IsNullOrEmpty(name)) continue;
                var full = string.IsNullOrEmpty(dir) ? name : Path.Combine(dir, name);
                list.Add(new EverythingHit(name, dir, full, (flags & 1) != 0));
            }
            return list;
        }

        private static string ReadUtf16(byte[] raw, int offset)
        {
            if (offset < 0 || offset + 1 >= raw.Length) return "";
            var end = offset;
            while (end + 1 < raw.Length && (raw[end] != 0 || raw[end + 1] != 0))
                end += 2;
            if (end == offset) return "";
            return Encoding.Unicode.GetString(raw, offset, end - offset);
        }
    }
}
