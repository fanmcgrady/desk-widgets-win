using System.Diagnostics;
using System.IO;

namespace DeskWidgets.Services;

public static class Autostart
{
    private static string ShortcutPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            "DeskWidgets.lnk");

    public static bool IsEnabled() => File.Exists(ShortcutPath);

    public static void SetEnabled(bool on)
    {
        if (on) Enable();
        else Disable();
    }

    public static void Enable()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);
        var link = ShortcutPath.Replace("'", "''");
        var target = exe.Replace("'", "''");
        var work = Path.GetDirectoryName(exe)!.Replace("'", "''");
        var ps =
            $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{link}');" +
            $"$s.TargetPath='{target}';$s.WorkingDirectory='{work}';" +
            "$s.Description='桌面侧栏';$s.Save()";
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = "-NoProfile -Command \"" + ps.Replace("\"", "\\\"") + "\"",
            CreateNoWindow = true,
            UseShellExecute = false,
        })?.WaitForExit(5000);
    }

    public static void Disable()
    {
        try
        {
            if (File.Exists(ShortcutPath))
                File.Delete(ShortcutPath);
        }
        catch { /* ignore */ }
    }
}
