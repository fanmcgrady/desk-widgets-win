using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskWidgets.Services;

public static class Autostart
{
    private static string ShortcutPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            "DeskWidgets.lnk");

    private static string PrefPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeskWidgets",
            "autostart.pref");

    public static bool IsEnabled() => File.Exists(ShortcutPath);

    /// <summary>启动时调用：默认开启；若用户曾关掉则保持关闭，已开启则刷新快捷方式路径。</summary>
    public static void ApplyPreferred()
    {
        var pref = ReadPref();
        if (pref == "off")
        {
            Disable();
            return;
        }
        Enable();
        WritePref("on");
    }

    public static void SetEnabled(bool on)
    {
        if (on) Enable();
        else Disable();
        WritePref(on ? "on" : "off");
    }

    public static void Enable()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);
            CreateShortcut(ShortcutPath, exe, Path.GetDirectoryName(exe)!, "方老师的桌面工作台");
        }
        catch
        {
            // 回退：PowerShell 写快捷方式
            TryEnableViaPowerShell(exe);
        }
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

    private static void TryEnableViaPowerShell(string exe)
    {
        var link = ShortcutPath.Replace("'", "''");
        var target = exe.Replace("'", "''");
        var work = Path.GetDirectoryName(exe)!.Replace("'", "''");
        var script =
            $"$s = (New-Object -ComObject WScript.Shell).CreateShortcut('{link}');" +
            $"$s.TargetPath = '{target}'; $s.WorkingDirectory = '{work}';" +
            "$s.Description = '方老师的桌面工作台'; $s.Save()";
        var tmp = Path.Combine(Path.GetTempPath(), "deskwidgets-autostart.ps1");
        File.WriteAllText(tmp, script, Encoding.UTF8);
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + tmp + "\"",
            CreateNoWindow = true,
            UseShellExecute = false,
        })?.WaitForExit(8000);
    }

    private static void CreateShortcut(string linkPath, string targetPath, string workDir, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell unavailable");
        dynamic shell = Activator.CreateInstance(shellType)!;
        var shortcut = shell.CreateShortcut(linkPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = workDir;
        shortcut.Description = description;
        shortcut.Save();
        Marshal.FinalReleaseComObject(shortcut);
        Marshal.FinalReleaseComObject(shell);
    }

    private static string? ReadPref()
    {
        try
        {
            return File.Exists(PrefPath) ? File.ReadAllText(PrefPath).Trim() : null;
        }
        catch { return null; }
    }

    private static void WritePref(string value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PrefPath)!);
            File.WriteAllText(PrefPath, value);
        }
        catch { /* ignore */ }
    }
}
