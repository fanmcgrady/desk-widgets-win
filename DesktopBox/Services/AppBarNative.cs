using System.Runtime.InteropServices;

namespace DesktopBox.Services;

/// <summary>Windows AppBar（SHAppBarMessage），底边占位，避免盖住其它窗口。</summary>
internal static class AppBarNative
{
    public const int AbeLeft = 0;
    public const int AbeTop = 1;
    public const int AbeRight = 2;
    public const int AbeBottom = 3;

    public const int AbmNew = 0x00000000;
    public const int AbmRemove = 0x00000001;
    public const int AbmQueryPos = 0x00000002;
    public const int AbmSetPos = 0x00000003;
    public const int AbmActivate = 0x00000006;
    public const int AbmWindowPosChanged = 0x00000009;
    public const int AbmSetState = 0x0000000A;

    public const int AbsAlwaysOnTop = 0x00000002;

    public const int AbnStateChange = 0;
    public const int AbnPosChanged = 1;
    public const int AbnFullScreenApp = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AppBarData
    {
        public int CbSize;
        public IntPtr Hwnd;
        public uint UCallbackMessage;
        public uint UEdge;
        public Rect Rc;
        public IntPtr LParam;
    }

    [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern uint SHAppBarMessage(uint dwMessage, ref AppBarData pData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessage(string lpString);
}
