using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DeskWidgets.Services;

public sealed record StartupEntry(string Name, string ProcessName);

/// <summary>读取已启用的开机启动项，并按进程名判断是否已起来。</summary>
public static class StartupWatch
{
    private static readonly TimeSpan FreshBootWindow = TimeSpan.FromMinutes(3);
    private static readonly HashSet<string> HostProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "rundll32", "regsvr32", "wscript", "cscript", "explorer", "schtasks",
    };

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedStartup = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    public static bool IsFreshBoot() =>
        TimeSpan.FromMilliseconds(GetTickCount64()) < FreshBootWindow;

    public static IReadOnlyList<StartupEntry> LoadEnabledOthers()
    {
        var list = new List<StartupEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReadRun(Registry.CurrentUser, list, seen);
        ReadRun(Registry.LocalMachine, list, seen);
        ReadStartupFolder(Environment.SpecialFolder.Startup, Registry.CurrentUser, list, seen);
        ReadStartupFolder(Environment.SpecialFolder.CommonStartup, Registry.LocalMachine, list, seen);
        return list;
    }

    public static bool IsRunning(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void ReadRun(RegistryKey root, List<StartupEntry> list, HashSet<string> seen)
    {
        try
        {
            using var run = root.OpenSubKey(RunKey);
            if (run == null) return;
            using var approved = root.OpenSubKey(ApprovedRun);
            foreach (var name in run.GetValueNames())
            {
                if (!IsEnabled(approved, name)) continue;
                if (run.GetValue(name) is not string command || string.IsNullOrWhiteSpace(command)) continue;
                Add(list, seen, name.Trim(), command);
            }
        }
        catch { /* 读不到的项跳过 */ }
    }

    private static void ReadStartupFolder(
        Environment.SpecialFolder folder,
        RegistryKey root,
        List<StartupEntry> list,
        HashSet<string> seen)
    {
        string dir;
        try
        {
            dir = Environment.GetFolderPath(folder);
        }
        catch
        {
            return;
        }
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        try
        {
            using var approved = root.OpenSubKey(ApprovedStartup);
            foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk"))
            {
                var file = Path.GetFileName(lnk);
                if (!IsEnabled(approved, file)) continue;
                var target = ResolveShortcut(lnk);
                if (string.IsNullOrWhiteSpace(target)) continue;
                var display = Path.GetFileNameWithoutExtension(file);
                Add(list, seen, string.IsNullOrWhiteSpace(display) ? file : display, target);
            }
        }
        catch { /* ignore */ }
    }

    private static void Add(List<StartupEntry> list, HashSet<string> seen, string displayName, string command)
    {
        var process = PickProcessName(command);
        if (process == null || IsSelf(process)) return;
        if (!seen.Add(process)) return;
        var name = string.IsNullOrWhiteSpace(displayName) ? process : displayName.Trim();
        list.Add(new StartupEntry(name, process));
    }

    private static bool IsEnabled(RegistryKey? approved, string valueName)
    {
        if (approved == null) return true;
        try
        {
            if (approved.GetValue(valueName) is not byte[] data || data.Length == 0) return true;
            // 首字节 3：任务管理器里关掉的启动项
            return data[0] != 3;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsSelf(string processName)
    {
        var self = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        return processName.Equals(self, StringComparison.OrdinalIgnoreCase)
               || processName.Equals("DeskWidgets", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>从命令或快捷方式目标里取要核对的进程名。cmd / rundll32 这类外壳往后找真正的 exe。</summary>
    private static string? PickProcessName(string command)
    {
        var names = new List<string>();
        foreach (Match m in Regex.Matches(command, "\"([^\"]+?\\.exe)\"", RegexOptions.IgnoreCase))
            AddExe(names, m.Groups[1].Value);
        foreach (Match m in Regex.Matches(command, @"(?<!"")([A-Za-z]:\\[^""\s]+?\.exe)", RegexOptions.IgnoreCase))
            AddExe(names, m.Groups[1].Value);

        var trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            if (end > 1) AddExe(names, trimmed[1..end]);
        }
        else
        {
            var token = trimmed.Split(' ', 2)[0];
            AddExe(names, token);
        }

        string? host = null;
        foreach (var name in names)
        {
            if (HostProcesses.Contains(name))
            {
                host ??= name;
                continue;
            }
            return name;
        }
        return host;
    }

    private static void AddExe(List<string> names, string path)
    {
        var file = Path.GetFileNameWithoutExtension(path.Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(file)) return;
        if (names.Contains(file, StringComparer.OrdinalIgnoreCase)) return;
        names.Add(file);
    }

    private static string? ResolveShortcut(string linkPath)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            shell = Activator.CreateInstance(shellType);
            if (shell == null) return null;
            dynamic dyn = shell;
            shortcut = dyn.CreateShortcut(linkPath);
            if (shortcut == null) return null;
            dynamic sc = shortcut;
            string? target = sc.TargetPath as string;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (shortcut != null)
            {
                try { Marshal.FinalReleaseComObject(shortcut); } catch { /* ignore */ }
            }
            if (shell != null)
            {
                try { Marshal.FinalReleaseComObject(shell); } catch { /* ignore */ }
            }
        }
    }
}
