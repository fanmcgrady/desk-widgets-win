using System.Diagnostics;
using System.IO;
using DesktopBox.Services;
using DesktopBox.Services.Shell;

namespace DesktopBox;

/// <summary>
/// 在 DeskWidgets 进程内启动/停止桌面围栏（无独立 exe、无独立托盘）。
/// </summary>
public static class DesktopBoxRuntime
{
    private static Mutex? _mutex;
    private static MainWindow? _main;
    private static DesktopAutoSortService? _autoSort;
    private static bool _started;

    public static bool IsRunning => _started;
    public static bool IsPaused { get; private set; }

    public static bool TryStart()
    {
        if (_started)
            return true;

        // 关掉独立 DesktopBox.exe，避免双实例抢互斥体
        foreach (var p in Process.GetProcessesByName("DesktopBox"))
        {
            try
            {
                if (p.Id == Environment.ProcessId)
                    continue;
                p.CloseMainWindow();
                if (!p.WaitForExit(800))
                    p.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }
            finally
            {
                p.Dispose();
            }
        }

        _mutex = new Mutex(true, @"Local\DesktopBox.SingleInstance", out var created);
        if (!created)
        {
            try { _mutex.Dispose(); } catch { /* ignore */ }
            _mutex = null;
            return false;
        }

        try
        {
            Directory.CreateDirectory(AppConfig.ConfigDirectory);
            File.AppendAllText(
                Path.Combine(AppConfig.ConfigDirectory, "debug.log"),
                $"{DateTime.Now:HH:mm:ss.fff} host start pid={Environment.ProcessId}\n");
        }
        catch
        {
            // ignore
        }

        var config = AppConfig.LoadOrCreate();
        config.EnsureFolders();

        _main = new MainWindow(config);
        _main.Show();

        _autoSort = new DesktopAutoSortService(config);
        _autoSort.Start();

        ShellWheelHook.Install();
        _started = true;
        IsPaused = false;
        return true;
    }

    public static void Stop()
    {
        if (!_started && _main is null)
            return;

        ShellWheelHook.Uninstall();
        ZoneDragMoveForce.Shutdown();
        _autoSort?.Dispose();
        _autoSort = null;
        _main?.DisposeHooks();
        if (_main is not null)
        {
            try { _main.Close(); } catch { /* ignore */ }
            _main = null;
        }

        try
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch
        {
            // ignore
        }
        _mutex = null;
        _started = false;
        IsPaused = false;
    }

    public static AppConfig LoadConfig() => AppConfig.LoadOrCreate();

    public static void SaveAndReload(AppConfig config)
    {
        config.Normalize();
        config.EnsureFolders();
        config.Save();

        _autoSort?.Dispose();
        _autoSort = new DesktopAutoSortService(config);
        _autoSort.Start();

        if (_main is null)
        {
            if (!_started)
                TryStart();
            return;
        }

        _main.ReloadConfig();
        if (IsPaused)
            _main.SetPaused(true);
    }

    public static void SetPaused(bool paused)
    {
        IsPaused = paused;
        _main?.SetPaused(paused);
    }

    public static void SetAutoSortEnabled(bool enabled)
    {
        _autoSort?.SetRuntimeEnabled(enabled);
    }

    public static bool IsAutoSortEnabled => _autoSort?.IsEnabled ?? (LoadConfig().AutoSort?.Enabled ?? true);

    public static void PreviewZones() => _main?.PreviewZones(TimeSpan.Zero);
}
