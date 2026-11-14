using System.Collections.Concurrent;
using System.IO;

namespace DesktopBox.Services;

/// <summary>
/// 监视用户桌面：新文件/文件夹防抖后移动到内存区（X:\）。
/// </summary>
internal sealed class DesktopAutoSortService : IDisposable
{
    private readonly AppConfig _config;
    private readonly string _desktop;
    private readonly string _fenceRoot;
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounceTimer;
    private bool _runtimeEnabled = true;
    private bool _disposed;

    public DesktopAutoSortService(AppConfig config)
    {
        _config = config;
        _desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        _fenceRoot = Path.GetFullPath(AppConfig.DefaultRoot);
    }

    public bool IsEnabled => _runtimeEnabled && (_config.AutoSort?.Enabled ?? true);

    public void Start()
    {
        if (_watcher is not null)
            return;

        Directory.CreateDirectory(_desktop);
        _watcher = new FileSystemWatcher(_desktop)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };
        _watcher.Created += OnCreated;
        _watcher.Renamed += OnRenamed;
        _watcher.Changed += OnChanged;

        _debounceTimer = new System.Threading.Timer(_ => FlushPending(), null, Timeout.Infinite, Timeout.Infinite);
        Log("auto-sort started");
    }

    public void SetRuntimeEnabled(bool enabled)
    {
        _runtimeEnabled = enabled;
        if (_config.AutoSort is null)
            _config.AutoSort = new AutoSortConfig();
        _config.AutoSort.Enabled = enabled;
        _config.Save();
        Log(enabled ? "auto-sort enabled" : "auto-sort paused");
    }

    private void OnCreated(object sender, FileSystemEventArgs e) => Queue(e.FullPath);

    private void OnChanged(object sender, FileSystemEventArgs e) => Queue(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e) => Queue(e.FullPath);

    private void Queue(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsEnabled)
            return;

        _pending[path] = 0;
        _debounceTimer?.Change(TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
    }

    private void FlushPending()
    {
        if (!IsEnabled || _disposed)
            return;

        var paths = _pending.Keys.ToArray();
        _pending.Clear();

        foreach (var path in paths)
            TryMoveWithRetry(path);
    }

    private void TryMoveWithRetry(string path)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                if (TryMoveOnce(path))
                    return;
            }
            catch (IOException)
            {
                // 下载未完成等
            }
            catch (UnauthorizedAccessException)
            {
                // ignore retry
            }
            catch (Exception ex)
            {
                Log($"move fail {path}: {ex.Message}");
                return;
            }

            Thread.Sleep(400 + attempt * 200);
        }

        Log($"give up (locked?): {path}");
    }

    private bool TryMoveOnce(string path)
    {
        if (!IsEnabled)
            return true;

        if (!File.Exists(path) && !Directory.Exists(path))
            return true; // 已消失

        var full = Path.GetFullPath(path);
        if (ShouldSkip(full))
            return true;

        if (File.Exists(full) && !IsFileReady(full))
            return false;

        var targetRoot = ResolveTargetFolder();
        Directory.CreateDirectory(targetRoot);

        var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name))
            return true;

        var dest = UniquePath(Path.Combine(targetRoot, name));
        if (Directory.Exists(full))
            Directory.Move(full, dest);
        else
            File.Move(full, dest);

        Log($"moved -> {dest}");
        return true;
    }

    private string ResolveTargetFolder()
    {
        var id = _config.AutoSort?.TargetZoneId ?? "todo";
        var zone = _config.FindZone(id) ?? _config.FindZone("todo");
        var folder = zone?.Folder;
        if (string.IsNullOrWhiteSpace(folder))
            folder = AppConfig.DefaultMemoryRoot;
        return Path.GetFullPath(folder);
    }

    private bool ShouldSkip(string full)
    {
        var name = Path.GetFileName(full);
        if (string.IsNullOrEmpty(name))
            return true;

        if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("~$", StringComparison.Ordinal))
            return true;

        // 围栏根及其子项（E:\桌面分区）
        if (PathsEqual(full, _fenceRoot) || IsUnder(full, _fenceRoot))
            return true;

        // 内存区（X:）及其子项
        var memoryRoot = Path.GetFullPath(AppConfig.DefaultMemoryRoot);
        if (PathsEqual(full, memoryRoot) || IsUnder(full, memoryRoot))
            return true;

        var target = ResolveTargetFolder();
        if (PathsEqual(full, target) || IsUnder(full, target))
            return true;

        // 只处理桌面根下的直接子项
        var parent = Path.GetDirectoryName(full);
        if (parent is null || !PathsEqual(parent, _desktop))
            return true;

        return false;
    }

    private static bool IsFileReady(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;

        var dir = Path.GetDirectoryName(path) ?? "";
        var fileName = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{fileName} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }

        return Path.Combine(dir, $"{fileName}-{Guid.NewGuid():N}{ext}");
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string root)
    {
        var p = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var r = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.ConfigDirectory);
            File.AppendAllText(
                Path.Combine(AppConfig.ConfigDirectory, "debug.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _debounceTimer?.Dispose();
        _debounceTimer = null;
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }
}
