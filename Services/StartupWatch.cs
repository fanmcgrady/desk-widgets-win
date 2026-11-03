using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DeskWidgets.Services;

public sealed record StartupEntry(string Name, string ProcessName, string? PathHint = null, string[]? AlsoProcesses = null);

/// <summary>读取任务管理器里已启用的开机启动项，并判断对应进程是否已起来。</summary>
public static class StartupWatch
{
    private static readonly TimeSpan FreshBootWindow = TimeSpan.FromMinutes(3);
    private static readonly HashSet<string> HostProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "rundll32", "regsvr32", "wscript", "cscript", "explorer", "schtasks",
    };
    private static readonly HashSet<string> GenericDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "windows", "system32", "syswow64", "program files", "program files (x86)",
        "windowsapps", "local", "roaming", "appdata", "users", "microsoft",
    };
    private static readonly HashSet<string> SkipPackageKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "HAM", "PSR", "Schemas", "SplashScreen",
    };

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunKeyWow = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedRun32 = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private const string ApprovedStartup = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    private const int TaskTriggerLogon = 9;

    private static HashSet<string>? _processNames;
    private static List<string>? _processPaths;
    private static DateTime _processSnapshotUtc = DateTime.MinValue;

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr reserved);

    public static bool IsFreshBoot() =>
        TimeSpan.FromMilliseconds(GetTickCount64()) < FreshBootWindow;

    public static IReadOnlyList<StartupEntry> LoadEnabledOthers()
    {
        var list = new List<StartupEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReadRun(Registry.CurrentUser, RunKey, ApprovedRun, list, seen);
        ReadRun(Registry.LocalMachine, RunKey, ApprovedRun, list, seen);
        ReadRun(Registry.LocalMachine, RunKeyWow, ApprovedRun32, list, seen);
        ReadStartupFolder(Environment.SpecialFolder.Startup, Registry.CurrentUser, list, seen);
        ReadStartupFolder(Environment.SpecialFolder.CommonStartup, Registry.LocalMachine, list, seen);
        ReadPackagedStartupTasks(list, seen);
        ReadRootLogonTasks(list, seen);
        return list;
    }

    public static bool IsRunning(StartupEntry entry)
    {
        SnapshotProcesses();
        if (NameRunning(entry.ProcessName)) return true;
        if (entry.AlsoProcesses != null)
        {
            foreach (var name in entry.AlsoProcesses)
                if (NameRunning(name)) return true;
        }
        if (string.IsNullOrWhiteSpace(entry.PathHint) || _processPaths == null) return false;
        foreach (var path in _processPaths)
        {
            if (path.Contains(entry.PathHint, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool NameRunning(string? processName) =>
        !string.IsNullOrWhiteSpace(processName)
        && _processNames != null
        && _processNames.Contains(processName);

    private static void SnapshotProcesses()
    {
        if (_processNames != null && DateTime.UtcNow - _processSnapshotUtc < TimeSpan.FromMilliseconds(300))
            return;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(process.ProcessName))
                    names.Add(process.ProcessName);
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path))
                    paths.Add(path);
            }
            catch { /* 无权读取的进程只保留名字 */ }
            finally
            {
                process.Dispose();
            }
        }
        _processNames = names;
        _processPaths = paths;
        _processSnapshotUtc = DateTime.UtcNow;
    }

    private static void ReadRun(
        RegistryKey root,
        string runPath,
        string approvedPath,
        List<StartupEntry> list,
        HashSet<string> seen)
    {
        try
        {
            using var run = root.OpenSubKey(runPath);
            if (run == null) return;
            using var approved = root.OpenSubKey(approvedPath);
            foreach (var name in run.GetValueNames())
            {
                if (!IsEnabled(approved, name)) continue;
                if (run.GetValue(name) is not string command || string.IsNullOrWhiteSpace(command)) continue;
                AddCommand(list, seen, command, null);
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
                AddCommand(list, seen, target, null);
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>应用商店 / 打包应用在任务管理器「启动应用」里的项。State 2 或 4 为已启用。没打开过的应用，任务管理器仍显示已禁用。</summary>
    private static void ReadPackagedStartupTasks(List<StartupEntry> list, HashSet<string> seen)
    {
        const string root = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(root);
            if (packages == null) return;
            foreach (var packageKeyName in packages.GetSubKeyNames())
            {
                using var package = packages.OpenSubKey(packageKeyName);
                if (package == null) continue;
                var launched = package.GetValue("WasEverActivated") is int activated && activated != 0;
                foreach (var taskId in package.GetSubKeyNames())
                {
                    if (SkipPackageKeys.Contains(taskId)) continue;
                    using var task = package.OpenSubKey(taskId);
                    if (task?.GetValue("State") is not int state) continue;
                    if (state is not (2 or 4)) continue;
                    if (state == 2 && !launched) continue;
                    var resolved = ResolvePackagedTask(packageKeyName, taskId);
                    if (resolved == null) continue;
                    AddExe(list, seen, resolved.Value.Name, resolved.Value.ExePath, null);
                }
            }
        }
        catch { /* ignore */ }
    }

    private static (string Name, string ExePath)? ResolvePackagedTask(string packageKey, string taskId)
    {
        var split = packageKey.LastIndexOf('_');
        if (split <= 0 || split >= packageKey.Length - 1) return null;
        var packageName = packageKey[..split];
        var publisher = packageKey[(split + 1)..];
        var install = FindPackageRoot(packageName, publisher);
        if (install == null) return null;
        var manifestPath = Path.Combine(install, "AppxManifest.xml");
        if (!File.Exists(manifestPath)) return null;

        string xml;
        try
        {
            xml = File.ReadAllText(manifestPath);
        }
        catch
        {
            return null;
        }

        foreach (Match task in Regex.Matches(xml, @"<[^>]*StartupTask\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var tag = task.Value;
            var id = Attr(tag, "TaskId");
            if (!string.Equals(id, taskId, StringComparison.OrdinalIgnoreCase)) continue;
            var executable = Attr(tag, "Executable");
            if (string.IsNullOrWhiteSpace(executable))
            {
                var start = Math.Max(0, task.Index - 2500);
                var before = xml.Substring(start, task.Index - start);
                var found = Regex.Matches(before, @"Executable\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
                for (var i = found.Count - 1; i >= 0; i--)
                {
                    var rel = found[i].Groups[1].Value.Replace('/', '\\');
                    var candidate = Path.GetFullPath(Path.Combine(install, rel));
                    if (!File.Exists(candidate)) continue;
                    executable = rel;
                    break;
                }
            }
            if (string.IsNullOrWhiteSpace(executable)) return null;
            var exePath = Path.GetFullPath(Path.Combine(install, executable.Replace('/', '\\')));
            if (!File.Exists(exePath)) return null;
            var display = Attr(tag, "DisplayName");
            var name = FriendlyName(display, exePath, Path.GetFileName(install), packageName);
            return (name, exePath);
        }
        return null;
    }

    private static void ReadRootLogonTasks(List<StartupEntry> list, HashSet<string> seen)
    {
        object? scheduler = null;
        object? folder = null;
        object? tasks = null;
        try
        {
            var schedulerType = Type.GetTypeFromProgID("Schedule.Service");
            if (schedulerType == null) return;
            scheduler = Activator.CreateInstance(schedulerType);
            if (scheduler == null) return;
            dynamic svc = scheduler;
            svc.Connect();
            folder = svc.GetFolder("\\");
            if (folder == null) return;
            dynamic fld = folder;
            tasks = fld.GetTasks(1);
            if (tasks == null) return;
            dynamic col = tasks;
            var count = (int)col.Count;
            for (var i = 1; i <= count; i++)
            {
                dynamic? task = null;
                try
                {
                    task = col.Item(i);
                    if (task is null) continue;
                    dynamic current = task;
                    if (!(bool)current.Enabled) continue;
                    string name = current.Name;
                    if (name.Equals("DeskWidgets", StringComparison.OrdinalIgnoreCase)) continue;
                    var command = FirstExeAction(current);
                    if (string.IsNullOrWhiteSpace(command) || !IsUserStartupTask(command, HasLogonTrigger(current)))
                        continue;
                    AddCommand(list, seen, command, CleanTaskName(name));
                }
                catch { /* 单条任务读失败就跳过 */ }
                finally
                {
                    Release(task);
                }
            }
        }
        catch { /* 计划任务读不到时，注册表和启动文件夹仍然有效 */ }
        finally
        {
            Release(tasks);
            Release(folder);
            Release(scheduler);
        }
    }

    /// <summary>登录时启动，或装在用户目录里的启用任务。系统目录里的登录任务不算。</summary>
    private static bool IsUserStartupTask(string command, bool logon)
    {
        var exe = PickExePath(command);
        if (string.IsNullOrWhiteSpace(exe)) return false;
        var full = exe;
        try
        {
            if (File.Exists(exe)) full = Path.GetFullPath(exe);
        }
        catch { /* 别名解析失败时按原路径判断 */ }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows) &&
            full.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
            return false;
        if (full.Contains(@"\DriverStore\", StringComparison.OrdinalIgnoreCase)) return false;
        if (full.Contains(@"\Microsoft\Edge\", StringComparison.OrdinalIgnoreCase)) return false;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var inUserProfile =
            (!string.IsNullOrEmpty(local) && full.StartsWith(local, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrEmpty(roaming) && full.StartsWith(roaming, StringComparison.OrdinalIgnoreCase));
        return logon || inUserProfile;
    }

    private static string? FindPackageRoot(string packageName, string publisher)
    {
        const string repo = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        try
        {
            using var packages = Registry.CurrentUser.OpenSubKey(repo);
            if (packages == null) return null;
            string? bestName = null;
            string? bestRoot = null;
            foreach (var fullName in packages.GetSubKeyNames())
            {
                if (!fullName.StartsWith(packageName + "_", StringComparison.OrdinalIgnoreCase)) continue;
                if (!fullName.EndsWith("__" + publisher, StringComparison.OrdinalIgnoreCase)) continue;
                using var key = packages.OpenSubKey(fullName);
                var root = key?.GetValue("PackageRootFolder") as string;
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
                if (bestName == null || string.Compare(fullName, bestName, StringComparison.OrdinalIgnoreCase) > 0)
                {
                    bestName = fullName;
                    bestRoot = root;
                }
            }
            return bestRoot;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasLogonTrigger(dynamic task)
    {
        dynamic? triggers = null;
        try
        {
            triggers = task.Definition.Triggers;
            var count = (int)triggers.Count;
            for (var i = 1; i <= count; i++)
            {
                dynamic? trigger = null;
                try
                {
                    trigger = triggers.Item(i);
                    if ((int)trigger.Type == TaskTriggerLogon) return true;
                }
                finally
                {
                    Release(trigger);
                }
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            Release(triggers);
        }
        return false;
    }

    private static string? FirstExeAction(dynamic task)
    {
        dynamic? actions = null;
        try
        {
            actions = task.Definition.Actions;
            var count = (int)actions.Count;
            for (var i = 1; i <= count; i++)
            {
                dynamic? action = null;
                try
                {
                    action = actions.Item(i);
                    string? path = action.Path as string;
                    if (!string.IsNullOrWhiteSpace(path) && path.Contains(".exe", StringComparison.OrdinalIgnoreCase))
                        return path;
                }
                catch { /* 不是可执行操作 */ }
                finally
                {
                    Release(action);
                }
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Release(actions);
        }
        return null;
    }

    private static string CleanTaskName(string name)
    {
        var cut = name.IndexOf(" (", StringComparison.Ordinal);
        if (cut > 0) name = name[..cut];
        return name.Trim();
    }

    private static void AddCommand(List<StartupEntry> list, HashSet<string> seen, string command, string? displayOverride)
    {
        var exe = PickExePath(command);
        if (string.IsNullOrWhiteSpace(exe)) return;
        var display = string.IsNullOrWhiteSpace(displayOverride) ? Path.GetFileName(exe) : displayOverride;
        AddExe(list, seen, display, exe, null);
    }

    private static void AddExe(
        List<StartupEntry> list,
        HashSet<string> seen,
        string displayName,
        string exePath,
        string[]? alsoProcesses)
    {
        var process = Path.GetFileNameWithoutExtension(exePath.Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(process) || IsSelf(process)) return;
        if (!seen.Add(process)) return;
        var name = string.IsNullOrWhiteSpace(displayName) ? process : displayName.Trim();
        list.Add(new StartupEntry(name, process, ProductHint(exePath), alsoProcesses));
    }

    private static string? ProductHint(string exePath)
    {
        var dir = Path.GetDirectoryName(exePath.Trim().Trim('"'));
        for (var i = 0; i < 4 && !string.IsNullOrEmpty(dir); i++)
        {
            var name = Path.GetFileName(dir);
            if (!string.IsNullOrEmpty(name) && name.Length >= 4 && !GenericDirs.Contains(name))
                return name;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
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

    /// <summary>从命令里取出真正的 exe。路径中间可以有空格。</summary>
    private static string? PickExePath(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim().Trim('"'));
        var names = new List<string>();
        foreach (Match m in Regex.Matches(command, "\"([^\"]+\\.exe)\"", RegexOptions.IgnoreCase))
            ConsiderExe(names, m.Groups[1].Value);
        foreach (Match m in Regex.Matches(command, @"(?:[A-Za-z]:\\|\\\\)[^""]*?\.exe", RegexOptions.IgnoreCase))
            ConsiderExe(names, m.Value);

        string? host = null;
        foreach (var path in names)
        {
            var file = Path.GetFileNameWithoutExtension(path);
            if (HostProcesses.Contains(file))
            {
                host ??= path;
                continue;
            }
            return path;
        }
        return host;
    }

    private static void ConsiderExe(List<string> paths, string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
        if (paths.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        paths.Add(path);
    }

    private static string FriendlyName(string? displayName, string exePath, string packageFolder, string packageName)
    {
        if (!string.IsNullOrWhiteSpace(displayName) &&
            !displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            return displayName;

        var resourceId = string.IsNullOrWhiteSpace(displayName) ? "AppName" : displayName["ms-resource:".Length..];
        var localized = LoadPackageString(packageFolder, packageName, resourceId);
        if (!string.IsNullOrWhiteSpace(localized)) return localized;

        try
        {
            var desc = FileVersionInfo.GetVersionInfo(exePath).FileDescription;
            if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim();
        }
        catch { /* ignore */ }
        return Path.GetFileNameWithoutExtension(exePath);
    }

    private static string? LoadPackageString(string packageFolder, string packageName, string resourceId)
    {
        var sources = new[]
        {
            $@"@{{{packageFolder}?ms-resource://{packageName}/Resources/{resourceId}}}",
            $@"@{{{packageFolder}?ms-resource://{packageName}/resources/{resourceId}}}",
        };
        foreach (var source in sources)
        {
            var sb = new StringBuilder(512);
            if (SHLoadIndirectString(source, sb, sb.Capacity, IntPtr.Zero) != 0) continue;
            var text = sb.ToString().Trim();
            if (text.Length == 0 || text.StartsWith('@') || text.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase))
                continue;
            return text;
        }
        return null;
    }

    private static string? Attr(string tag, string name)
    {
        var m = Regex.Match(tag, name + @"\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
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
            Release(shortcut);
            Release(shell);
        }
    }

    private static void Release(object? com)
    {
        if (com == null) return;
        try { Marshal.FinalReleaseComObject(com); } catch { /* ignore */ }
    }
}
