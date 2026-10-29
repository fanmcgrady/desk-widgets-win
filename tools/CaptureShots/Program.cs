using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using IOPath = System.IO.Path;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: CaptureShots <outDir>");
    return 1;
}

var outDir = IOPath.GetFullPath(args[0]);
Directory.CreateDirectory(outDir);

var procs = Process.GetProcessesByName("DeskWidgets");
if (procs.Length == 0)
{
    var exe = IOPath.GetFullPath(IOPath.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "bin", "Release", "net8.0-windows", "DeskWidgets.exe"));
    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    Thread.Sleep(2500);
    procs = Process.GetProcessesByName("DeskWidgets");
}

var procId = (uint)procs[0].Id;
foreach (var s in Screen.AllScreens)
{
    Native.SetCursorPos(s.Bounds.Right - 2, s.Bounds.Top + s.Bounds.Height / 3);
    Thread.Sleep(800);
}

var hwnd = FindSidebar(procId);
if (hwnd == IntPtr.Zero)
{
    Console.Error.WriteLine("window not found");
    return 3;
}

Native.GetWindowRect(hwnd, out var rect);
Console.WriteLine($"hwnd={hwnd} rect={rect.Left},{rect.Top}-{rect.Right},{rect.Bottom}");

// Focus title, pin (top-right icon area)
FocusWindow(hwnd, rect);
ClickAt(rect.Right - 24, rect.Top + 22);
Thread.Sleep(400);
FocusWindow(hwnd, rect);

Shoot(hwnd, rect, IOPath.Combine(outDir, "01-notes.png"), 0x31, "notes");
Shoot(hwnd, rect, IOPath.Combine(outDir, "02-todos.png"), 0x32, "todos");
Shoot(hwnd, rect, IOPath.Combine(outDir, "03-mail.png"), 0x33, "mail");
Shoot(hwnd, rect, IOPath.Combine(outDir, "04-agent.png"), 0x34, "agent");

// Compare sizes / hashes to detect identical frames
var files = new[] { "01-notes.png", "02-todos.png", "03-mail.png", "04-agent.png" };
foreach (var f in files)
{
    var p = IOPath.Combine(outDir, f);
    var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(p)))[..10];
    Console.WriteLine($"{f} sha1={hash} len={new FileInfo(p).Length}");
}
return 0;

static void Shoot(IntPtr hwnd, Native.RECT rect, string path, byte digitVk, string label)
{
    FocusWindow(hwnd, rect);
    Thread.Sleep(150);
    // Alt + digit
    Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);
    Thread.Sleep(60);
    Native.keybd_event(digitVk, 0, 0, UIntPtr.Zero);
    Thread.Sleep(60);
    Native.keybd_event(digitVk, 0, 2, UIntPtr.Zero);
    Native.keybd_event(0x12, 0, 2, UIntPtr.Zero);
    Thread.Sleep(1600);

    using var bmp = Capture(hwnd);
    bmp.Save(path, ImageFormat.Png);
    Console.WriteLine($"saved {label} {bmp.Width}x{bmp.Height}");
}

static void FocusWindow(IntPtr hwnd, Native.RECT rect)
{
    Native.SetForegroundWindow(hwnd);
    Thread.Sleep(80);
    // click title text area
    ClickAt(rect.Left + 100, rect.Top + 20);
    Thread.Sleep(80);
}

static IntPtr FindSidebar(uint procId)
{
    var hwnd = IntPtr.Zero;
    Native.EnumWindows((h, _) =>
    {
        Native.GetWindowThreadProcessId(h, out var pid);
        if (pid != procId || !Native.IsWindowVisible(h)) return true;
        var sb = new StringBuilder(64);
        Native.GetWindowText(h, sb, 64);
        if (sb.ToString() == "桌面侧栏") hwnd = h;
        return true;
    }, IntPtr.Zero);
    return hwnd;
}

static void ClickAt(int x, int y)
{
    Native.SetCursorPos(x, y);
    Thread.Sleep(40);
    Native.mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
    Thread.Sleep(30);
    Native.mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
}

static Bitmap Capture(IntPtr hwnd)
{
    Native.GetWindowRect(hwnd, out var r);
    var w = r.Right - r.Left;
    var h = r.Bottom - r.Top;
    var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bmp);
    var hdc = g.GetHdc();
    var ok = Native.PrintWindow(hwnd, hdc, 2);
    g.ReleaseHdc(hdc);
    if (!ok)
        g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
    return bmp;
}

static class Native
{
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
