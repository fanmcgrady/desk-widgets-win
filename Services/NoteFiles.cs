using System.Diagnostics;
using System.IO;
using System.Text;

namespace DeskWidgets.Services;

/// <summary>主题笔记本地文本，可用记事本打开并回写。</summary>
public sealed class NoteFiles
{
    private readonly string _dir;

    public NoteFiles()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeskWidgets",
            "notes");
        Directory.CreateDirectory(_dir);
    }

    public string PathFor(string noteId) => Path.Combine(_dir, noteId + ".txt");

    public string Read(string noteId)
    {
        var path = PathFor(noteId);
        if (!File.Exists(path)) return "";
        try { return File.ReadAllText(path, Encoding.UTF8); }
        catch { return ""; }
    }

    public void Write(string noteId, string body)
    {
        File.WriteAllText(PathFor(noteId), body ?? "", new UTF8Encoding(false));
    }

    public void Delete(string noteId)
    {
        try
        {
            var path = PathFor(noteId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* ignore */ }
    }

    public void OpenInNotepad(string noteId, string? seedBody = null)
    {
        if (seedBody != null || !File.Exists(PathFor(noteId)))
            Write(noteId, seedBody ?? "");
        Process.Start(new ProcessStartInfo
        {
            FileName = "notepad.exe",
            Arguments = "\"" + PathFor(noteId) + "\"",
            UseShellExecute = true,
        });
    }

    public FileSystemWatcher Watch(string noteId, Action onChanged)
    {
        var watcher = new FileSystemWatcher(_dir)
        {
            Filter = Path.GetFileName(PathFor(noteId)),
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        void Handler(object _, FileSystemEventArgs __)
        {
            try { onChanged(); } catch { /* ignore */ }
        }
        watcher.Changed += Handler;
        watcher.Created += Handler;
        watcher.Renamed += (_, __) => Handler(_, null!);
        return watcher;
    }

    public static string TitleFromBody(string body)
    {
        foreach (var raw in (body ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            return line.Length > 24 ? line[..24] + "…" : line;
        }
        return "未命名笔记";
    }

    /// <summary>第一行=标题，其余=备注。</summary>
    public static (string Title, string Body) SplitTitleBody(string text)
    {
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        string? title = null;
        var rest = new List<string>();
        foreach (var line in lines)
        {
            if (title == null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                title = line.Trim();
            }
            else rest.Add(line);
        }
        while (rest.Count > 0 && string.IsNullOrWhiteSpace(rest[0]))
            rest.RemoveAt(0);
        while (rest.Count > 0 && string.IsNullOrWhiteSpace(rest[^1]))
            rest.RemoveAt(rest.Count - 1);
        return (title ?? "未命名", string.Join("\n", rest));
    }

    public static string JoinTitleBody(string title, string? body)
    {
        title = (title ?? "").Trim();
        body = body ?? "";
        if (string.IsNullOrWhiteSpace(body)) return title;
        return title + "\n" + body.TrimStart('\r', '\n');
    }
}
