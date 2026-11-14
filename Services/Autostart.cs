using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace DeskWidgets.Services;

public static class Autostart
{
    private const string TaskName = "DeskWidgets";

    // Run 和「启动」文件夹由资源管理器推迟几秒再拉起，先后不保证。
    // 登录计划任务在这次推迟之前就会跑，所以排在那些启动项前面。
    private const int TaskTriggerLogon = 9;
    private const int TaskActionExec = 0;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskCreateOrUpdate = 6;
    private const int TaskInstancesIgnoreNew = 2;
    private const int TaskRunLevelLeast = 0;

    private static string ShortcutPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            "DeskWidgets.lnk");

    private static string PrefPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeskWidgets",
            "autostart.pref");

    public static bool IsEnabled()
    {
        if (TryReadTaskEnabled(out var enabled)) return enabled;
        return File.Exists(ShortcutPath);
    }

    /// <summary>启动时调用：默认开启；若用户曾关掉则保持关闭，已开启则刷新任务里的程序路径。</summary>
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
        var work = Path.GetDirectoryName(exe)!;
        // 计划任务已指向同一 exe 则跳过 COM 重注册
        if (TryGetLogonTaskAction(out var taskExe, out var taskWork, out var enabled)
            && enabled
            && PathsEqual(taskExe, exe)
            && PathsEqual(taskWork, work))
        {
            DeleteShortcut();
            return;
        }
        if (TryRegisterLogonTask(exe))
        {
            DeleteShortcut();
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);
            if (File.Exists(ShortcutPath)
                && TryReadShortcutTarget(ShortcutPath, out var linkExe, out var linkWork)
                && PathsEqual(linkExe, exe)
                && PathsEqual(linkWork, work))
                return;
            CreateShortcut(ShortcutPath, exe, work, "工作区");
        }
        catch
        {
            TryEnableViaPowerShell(exe);
        }
    }

    private static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(a.Trim()),
                Path.GetFullPath(b.Trim()),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool TryGetLogonTaskAction(out string exe, out string workDir, out bool enabled)
    {
        exe = "";
        workDir = "";
        enabled = false;
        object? service = null;
        object? folder = null;
        object? task = null;
        try
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service");
            if (serviceType == null) return false;
            service = Activator.CreateInstance(serviceType);
            if (service == null) return false;
            dynamic svc = service;
            svc.Connect();
            folder = svc.GetFolder("\\");
            dynamic fld = folder;
            task = fld.GetTask(TaskName);
            dynamic registered = task;
            enabled = (bool)registered.Enabled;
            dynamic def = registered.Definition;
            dynamic actions = def.Actions;
            if (actions.Count < 1) return false;
            dynamic action = actions[1]; // ITaskActionCollection is 1-based
            exe = (string)(action.Path ?? "");
            workDir = (string)(action.WorkingDirectory ?? "");
            return !string.IsNullOrWhiteSpace(exe);
        }
        catch
        {
            return false;
        }
        finally
        {
            Release(task);
            Release(folder);
            Release(service);
        }
    }

    private static bool TryReadShortcutTarget(string linkPath, out string exe, out string workDir)
    {
        exe = "";
        workDir = "";
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return false;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic s = shell.CreateShortcut(linkPath);
            exe = (string)(s.TargetPath ?? "");
            workDir = (string)(s.WorkingDirectory ?? "");
            try { Marshal.FinalReleaseComObject(s); } catch { /* ignore */ }
            try { Marshal.FinalReleaseComObject(shell); } catch { /* ignore */ }
            return !string.IsNullOrWhiteSpace(exe);
        }
        catch
        {
            return false;
        }
    }

    public static void Disable()
    {
        DeleteTask();
        DeleteShortcut();
    }

    private static bool TryRegisterLogonTask(string exe)
    {
        object? service = null;
        object? folder = null;
        object? definition = null;
        try
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service");
            if (serviceType == null) return false;
            service = Activator.CreateInstance(serviceType);
            if (service == null) return false;
            dynamic svc = service;
            svc.Connect();
            folder = svc.GetFolder("\\");
            definition = svc.NewTask(0);
            dynamic task = definition;
            task.RegistrationInfo.Description = "工作区";
            var user = WindowsIdentity.GetCurrent().Name;
            task.Principal.UserId = user;
            task.Principal.LogonType = TaskLogonInteractiveToken;
            task.Principal.RunLevel = TaskRunLevelLeast;
            task.Settings.Enabled = true;
            task.Settings.DisallowStartIfOnBatteries = false;
            task.Settings.StopIfGoingOnBatteries = false;
            task.Settings.AllowDemandStart = true;
            task.Settings.StartWhenAvailable = true;
            task.Settings.MultipleInstances = TaskInstancesIgnoreNew;
            task.Settings.ExecutionTimeLimit = "PT0S";
            dynamic trigger = task.Triggers.Create(TaskTriggerLogon);
            trigger.UserId = user;
            trigger.Enabled = true;
            dynamic action = task.Actions.Create(TaskActionExec);
            action.Path = exe;
            action.WorkingDirectory = Path.GetDirectoryName(exe)!;
            dynamic fld = folder;
            fld.RegisterTaskDefinition(TaskName, task, TaskCreateOrUpdate, null, null, TaskLogonInteractiveToken, null);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Release(definition);
            Release(folder);
            Release(service);
        }
    }

    private static bool TryReadTaskEnabled(out bool enabled)
    {
        enabled = false;
        object? service = null;
        object? folder = null;
        object? task = null;
        try
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service");
            if (serviceType == null) return false;
            service = Activator.CreateInstance(serviceType);
            if (service == null) return false;
            dynamic svc = service;
            svc.Connect();
            folder = svc.GetFolder("\\");
            dynamic fld = folder;
            task = fld.GetTask(TaskName);
            dynamic registered = task;
            enabled = (bool)registered.Enabled;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Release(task);
            Release(folder);
            Release(service);
        }
    }

    private static void DeleteTask()
    {
        object? service = null;
        object? folder = null;
        try
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service");
            if (serviceType == null) return;
            service = Activator.CreateInstance(serviceType);
            if (service == null) return;
            dynamic svc = service;
            svc.Connect();
            folder = svc.GetFolder("\\");
            dynamic fld = folder;
            fld.DeleteTask(TaskName, 0);
        }
        catch { /* 没有任务时忽略 */ }
        finally
        {
            Release(folder);
            Release(service);
        }
    }

    private static void DeleteShortcut()
    {
        try
        {
            if (File.Exists(ShortcutPath))
                File.Delete(ShortcutPath);
        }
        catch { /* ignore */ }
    }

    private static void Release(object? com)
    {
        if (com == null) return;
        try { Marshal.FinalReleaseComObject(com); } catch { /* ignore */ }
    }

    private static void TryEnableViaPowerShell(string exe)
    {
        var link = ShortcutPath.Replace("'", "''");
        var target = exe.Replace("'", "''");
        var work = Path.GetDirectoryName(exe)!.Replace("'", "''");
        var script =
            $"$s = (New-Object -ComObject WScript.Shell).CreateShortcut('{link}');" +
            $"$s.TargetPath = '{target}'; $s.WorkingDirectory = '{work}';" +
            "$s.Description = '工作区'; $s.Save()";
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
