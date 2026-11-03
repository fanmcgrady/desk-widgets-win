using System.Runtime.InteropServices;

namespace DeskWidgets;

public static class Program
{
    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [STAThread]
    public static void Main()
    {
        // -4 = PER_MONITOR_AWARE_V2。主屏 200%、扩展屏 125% 时必须按各屏真实像素摆放。
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
