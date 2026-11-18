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
    private static bool _stopping;

    public static bool IsRunning => _started && _main is not null && _main.IsLoaded;
    public static bool IsPaused { get; private set; }

    public static event Action? PauseChanged;

    public static bool TryStart()
    {
        // 窗口已被关掉但标志未清时，允许重新拉起
        if (_started && _main is not null && _main.IsLoaded)
            return true;
        if (_started && (_main is null || !_main.IsLoaded))
            CleanupAfterClose();

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
        _main.Closed += OnMainClosed;
        _main.Show();

        _autoSort = new DesktopAutoSortService(config);
        _autoSort.Start();

        // 不装全局 WH_MOUSE_LL：滚轮改由 ZoneDragMoveForce / ShellMouseRouting 处理，减轻全系统鼠标开销
        _started = true;
        IsPaused = false;
        return true;
    }

    private static void OnMainClosed(object? sender, EventArgs e)
    {
        if (_stopping)
            return;
        try
        {
            File.AppendAllText(
                Path.Combine(AppConfig.ConfigDirectory, "debug.log"),
                $"{DateTime.Now:HH:mm:ss.fff} fence window closed unexpectedly; will allow restart\n");
        }
        catch { /* ignore */ }
        CleanupAfterClose();
    }

    private static void CleanupAfterClose()
    {
        ShellWheelHook.Uninstall();
        ZoneDragMoveForce.Shutdown();
        _autoSort?.Dispose();
        _autoSort = null;
        if (_main is not null)
        {
            _main.Closed -= OnMainClosed;
            try { _main.DisposeHooks(); } catch { /* ignore */ }
            _main = null;
        }

        try
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch { /* ignore */ }
        _mutex = null;
        _started = false;
    }

    public static void Stop()
    {
        if (!_started && _main is null)
            return;

        _stopping = true;
        try
        {
            _main?.DisposeHooks();
            if (_main is not null)
            {
                try { _main.Close(); } catch { /* ignore */ }
            }
            CleanupAfterClose();
        }
        finally
        {
            _stopping = false;
            IsPaused = false;
        }
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
        var changed = IsPaused != paused;
        IsPaused = paused;
        _main?.SetPaused(paused);
        if (changed)
        {
            try { PauseChanged?.Invoke(); } catch { /* ignore */ }
        }
    }

    public static void SetAutoSortEnabled(bool enabled)
    {
        _autoSort?.SetRuntimeEnabled(enabled);
    }

    public static bool IsAutoSortEnabled => _autoSort?.IsEnabled ?? (LoadConfig().AutoSort?.Enabled ?? true);

    public static void PreviewZones() => _main?.PreviewZones(TimeSpan.Zero);
}
