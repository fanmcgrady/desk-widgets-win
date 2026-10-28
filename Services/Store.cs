using System.IO;
using Microsoft.Data.Sqlite;

namespace DeskWidgets.Services;

public sealed class TodoItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public bool Done { get; set; }
    public string? DueDate { get; set; }
    public long UpdatedAt { get; set; }
    public string? GraphId { get; set; }
    public long GraphModifiedMs { get; set; }
    public bool Deleted { get; set; }
}

public sealed class NoteItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public long UpdatedAt { get; set; }
}

public sealed class Store
{
    private readonly string _dbPath;

    public Store()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeskWidgets");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "desk.db");
        Init();
    }

    private SqliteConnection Open()
    {
        var con = new SqliteConnection($"Data Source={_dbPath}");
        con.Open();
        return con;
    }

    private void Init()
    {
        using var con = Open();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS todos (
                    id TEXT PRIMARY KEY,
                    title TEXT NOT NULL,
                    body TEXT NOT NULL DEFAULT '',
                    done INTEGER NOT NULL DEFAULT 0,
                    due_date TEXT,
                    sort_order INTEGER NOT NULL DEFAULT 0,
                    updated_at INTEGER NOT NULL,
                    deleted INTEGER NOT NULL DEFAULT 0,
                    graph_id TEXT,
                    graph_modified_ms INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS notes (
                    id TEXT PRIMARY KEY,
                    title TEXT NOT NULL DEFAULT '',
                    body TEXT NOT NULL DEFAULT '',
                    updated_at INTEGER NOT NULL,
                    deleted INTEGER NOT NULL DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS meta (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }

        EnsureColumn(con, "todos", "body", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(con, "todos", "graph_id", "TEXT");
        EnsureColumn(con, "todos", "graph_modified_ms", "INTEGER NOT NULL DEFAULT 0");
        MigrateNotesToTodos(con);
    }

    private static void EnsureColumn(SqliteConnection con, string table, string column, string decl)
    {
        using var q = con.CreateCommand();
        q.CommandText = $"PRAGMA table_info({table})";
        using var r = q.ExecuteReader();
        while (r.Read())
        {
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        r.Close();
        using var alter = con.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {decl}";
        alter.ExecuteNonQuery();
    }

    /// <summary>旧主题笔记并入当天代办（备注=正文），只迁移一次。</summary>
    private void MigrateNotesToTodos(SqliteConnection con)
    {
        if (GetMeta(con, "notes_migrated") == "1") return;

        var today = DateTime.Today.ToString("yyyy-MM-dd");
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT id, title, body, updated_at FROM notes
                WHERE deleted=0
                """;
            using var r = cmd.ExecuteReader();
            var rows = new List<(string Id, string Title, string Body, long Updated)>();
            while (r.Read())
            {
                rows.Add((
                    r.GetString(0),
                    r.GetString(1),
                    r.GetString(2),
                    r.GetInt64(3)));
            }
            r.Close();

            foreach (var row in rows)
            {
                using var ins = con.CreateCommand();
                ins.CommandText =
                    """
                    INSERT OR IGNORE INTO todos(id, title, body, done, due_date, sort_order, updated_at, deleted)
                    VALUES($id,$title,$body,0,$due,0,$ts,0)
                    """;
                var title = string.IsNullOrWhiteSpace(row.Title) ? "主题笔记" : row.Title.Trim();
                ins.Parameters.AddWithValue("$id", "note_" + row.Id);
                ins.Parameters.AddWithValue("$title", title);
                ins.Parameters.AddWithValue("$body", row.Body ?? "");
                ins.Parameters.AddWithValue("$due", today);
                ins.Parameters.AddWithValue("$ts", row.Updated);
                ins.ExecuteNonQuery();

                using var soft = con.CreateCommand();
                soft.CommandText = "UPDATE notes SET deleted=1, updated_at=$ts WHERE id=$id";
                soft.Parameters.AddWithValue("$id", row.Id);
                soft.Parameters.AddWithValue("$ts", NowMs());
                soft.ExecuteNonQuery();
            }
        }

        SetMeta(con, "notes_migrated", "1");
    }

    private static string? GetMeta(SqliteConnection con, string key)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key=$k LIMIT 1";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void SetMeta(SqliteConnection con, string key, string value)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO meta(key, value) VALUES($k,$v)
            ON CONFLICT(key) DO UPDATE SET value=$v
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public string? GetMeta(string key)
    {
        using var con = Open();
        return GetMeta(con, key);
    }

    public void SetMeta(string key, string value)
    {
        using var con = Open();
        SetMeta(con, key, value);
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static string NewId() => Guid.NewGuid().ToString("N");

    public IReadOnlyList<TodoItem> ListTodos(bool includeDone)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = includeDone
            ? """
              SELECT id, title, body, done, due_date, updated_at, graph_id, graph_modified_ms
              FROM todos WHERE deleted=0
              ORDER BY done ASC, sort_order ASC, updated_at DESC
              """
            : """
              SELECT id, title, body, done, due_date, updated_at, graph_id, graph_modified_ms
              FROM todos WHERE deleted=0 AND done=0
              ORDER BY sort_order ASC, updated_at DESC
              """;
        return ReadTodos(cmd);
    }

    /// <summary>含已删除，供同步推送远端删除。</summary>
    public IReadOnlyList<TodoItem> ListTodosForSync()
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, title, body, done, due_date, updated_at, graph_id, graph_modified_ms, deleted
            FROM todos
            ORDER BY updated_at ASC
            """;
        var list = new List<TodoItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new TodoItem
            {
                Id = r.GetString(0),
                Title = r.GetString(1),
                Body = r.IsDBNull(2) ? "" : r.GetString(2),
                Done = r.GetInt64(3) != 0,
                DueDate = r.IsDBNull(4) ? null : r.GetString(4),
                UpdatedAt = r.GetInt64(5),
                GraphId = r.IsDBNull(6) ? null : r.GetString(6),
                GraphModifiedMs = r.GetInt64(7),
                Deleted = r.GetInt64(8) != 0,
            });
        }
        return list;
    }

    private static List<TodoItem> ReadTodos(SqliteCommand cmd)
    {
        var list = new List<TodoItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new TodoItem
            {
                Id = r.GetString(0),
                Title = r.GetString(1),
                Body = r.IsDBNull(2) ? "" : r.GetString(2),
                Done = r.GetInt64(3) != 0,
                DueDate = r.IsDBNull(4) ? null : r.GetString(4),
                UpdatedAt = r.GetInt64(5),
                GraphId = r.IsDBNull(6) ? null : r.GetString(6),
                GraphModifiedMs = r.GetInt64(7),
            });
        }
        return list;
    }

    public TodoItem? GetTodo(string id)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, title, body, done, due_date, updated_at, graph_id, graph_modified_ms
            FROM todos WHERE id=$id AND deleted=0 LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$id", id);
        var list = ReadTodos(cmd);
        return list.Count == 0 ? null : list[0];
    }

    public TodoItem? FindByGraphId(string graphId)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, title, body, done, due_date, updated_at, graph_id, graph_modified_ms, deleted
            FROM todos WHERE graph_id=$gid LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$gid", graphId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new TodoItem
        {
            Id = r.GetString(0),
            Title = r.GetString(1),
            Body = r.IsDBNull(2) ? "" : r.GetString(2),
            Done = r.GetInt64(3) != 0,
            DueDate = r.IsDBNull(4) ? null : r.GetString(4),
            UpdatedAt = r.GetInt64(5),
            GraphId = r.IsDBNull(6) ? null : r.GetString(6),
            GraphModifiedMs = r.GetInt64(7),
            Deleted = r.GetInt64(8) != 0,
        };
    }

    public string AddTodo(string title, string? dueDate, string body = "")
    {
        title = title.Trim();
        if (title.Length == 0) return "";
        using var con = Open();
        long order;
        using (var q = con.CreateCommand())
        {
            q.CommandText = "SELECT COALESCE(MAX(sort_order),0)+1 FROM todos WHERE deleted=0";
            order = (long)(q.ExecuteScalar() ?? 1L);
        }
        var id = NewId();
        using var cmd = con.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO todos(id, title, body, done, due_date, sort_order, updated_at, deleted)
            VALUES($id,$title,$body,0,$due,$ord,$ts,0)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$body", body ?? "");
        cmd.Parameters.AddWithValue("$due", (object?)dueDate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ord", order);
        cmd.Parameters.AddWithValue("$ts", NowMs());
        cmd.ExecuteNonQuery();
        return id;
    }

    public void UpdateTodoContent(string id, string title, string body)
    {
        title = title.Trim();
        if (title.Length == 0) return;
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText =
            """
            UPDATE todos SET title=$title, body=$body, updated_at=$ts
            WHERE id=$id AND deleted=0
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$body", body ?? "");
        cmd.Parameters.AddWithValue("$ts", NowMs());
        cmd.ExecuteNonQuery();
    }

    public void ToggleTodo(string id)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText =
            """
            UPDATE todos SET done = CASE WHEN done=0 THEN 1 ELSE 0 END,
                             updated_at=$ts
            WHERE id=$id AND deleted=0
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$ts", NowMs());
        cmd.ExecuteNonQuery();
    }

    public void DeleteTodo(string id)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE todos SET deleted=1, updated_at=$ts WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$ts", NowMs());
        cmd.ExecuteNonQuery();
    }

    public void HardDeleteTodo(string id)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM todos WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void BindGraph(string id, string graphId, long graphModifiedMs)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText =
            """
            UPDATE todos SET graph_id=$gid, graph_modified_ms=$gm
            WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$gid", graphId);
        cmd.Parameters.AddWithValue("$gm", graphModifiedMs);
        cmd.ExecuteNonQuery();
    }

    public void ApplyFromGraph(string id, string title, string body, bool done, string? dueDate, long graphModifiedMs, string graphId)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText =
            """
            UPDATE todos SET title=$title, body=$body, done=$done, due_date=$due,
                             updated_at=$ts, graph_id=$gid, graph_modified_ms=$gm, deleted=0
            WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$body", body ?? "");
        cmd.Parameters.AddWithValue("$done", done ? 1 : 0);
        cmd.Parameters.AddWithValue("$due", (object?)dueDate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ts", graphModifiedMs);
        cmd.Parameters.AddWithValue("$gid", graphId);
        cmd.Parameters.AddWithValue("$gm", graphModifiedMs);
        cmd.ExecuteNonQuery();
    }

    public string InsertFromGraph(string title, string body, bool done, string? dueDate, string graphId, long graphModifiedMs)
    {
        using var con = Open();
        long order;
        using (var q = con.CreateCommand())
        {
            q.CommandText = "SELECT COALESCE(MAX(sort_order),0)+1 FROM todos WHERE deleted=0";
            order = (long)(q.ExecuteScalar() ?? 1L);
        }
        var id = NewId();
        using var cmd = con.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO todos(id, title, body, done, due_date, sort_order, updated_at, deleted, graph_id, graph_modified_ms)
            VALUES($id,$title,$body,$done,$due,$ord,$ts,0,$gid,$gm)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$body", body ?? "");
        cmd.Parameters.AddWithValue("$done", done ? 1 : 0);
        cmd.Parameters.AddWithValue("$due", (object?)dueDate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ord", order);
        cmd.Parameters.AddWithValue("$ts", graphModifiedMs);
        cmd.Parameters.AddWithValue("$gid", graphId);
        cmd.Parameters.AddWithValue("$gm", graphModifiedMs);
        cmd.ExecuteNonQuery();
        return id;
    }

    public Dictionary<string, int> DayTodoCounts(int year, int month)
    {
        var prefix = $"{year:0000}-{month:00}-";
        var map = new Dictionary<string, int>();
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText =
            """
            SELECT due_date, COUNT(*) FROM todos
            WHERE deleted=0 AND due_date LIKE $p
            GROUP BY due_date
            """;
        cmd.Parameters.AddWithValue("$p", prefix + "%");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(0)) continue;
            map[r.GetString(0)] = (int)r.GetInt64(1);
        }
        return map;
    }

    // ----- topic notes -----
    public IReadOnlyList<NoteItem> ListNotes()
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, title, body, updated_at FROM notes
            WHERE deleted=0
            ORDER BY updated_at DESC
            """;
        var list = new List<NoteItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new NoteItem
            {
                Id = r.GetString(0),
                Title = r.GetString(1),
                Body = r.GetString(2),
                UpdatedAt = r.GetInt64(3),
            });
        }
        return list;
    }

    public NoteItem? GetNote(string id)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, title, body, updated_at FROM notes
            WHERE id=$id AND deleted=0 LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new NoteItem
        {
            Id = r.GetString(0),
            Title = r.GetString(1),
            Body = r.GetString(2),
            UpdatedAt = r.GetInt64(3),
        };
    }

    public NoteItem CreateNote(string? title = null, string? body = null)
    {
        var note = new NoteItem
        {
            Id = NewId(),
            Title = string.IsNullOrWhiteSpace(title) ? "未命名笔记" : title.Trim(),
            Body = body ?? "",
            UpdatedAt = NowMs(),
        };
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO notes(id, title, body, updated_at, deleted)
            VALUES($id,$title,$body,$ts,0)
            """;
        cmd.Parameters.AddWithValue("$id", note.Id);
        cmd.Parameters.AddWithValue("$title", note.Title);
        cmd.Parameters.AddWithValue("$body", note.Body);
        cmd.Parameters.AddWithValue("$ts", note.UpdatedAt);
        cmd.ExecuteNonQuery();
        return note;
    }

    public void SaveNote(string id, string title, string body)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText =
            """
            UPDATE notes SET title=$title, body=$body, updated_at=$ts, deleted=0
            WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(title) ? "未命名笔记" : title.Trim());
        cmd.Parameters.AddWithValue("$body", body ?? "");
        cmd.Parameters.AddWithValue("$ts", NowMs());
        cmd.ExecuteNonQuery();
    }

    public void SoftDeleteNote(string id)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE notes SET deleted=1, updated_at=$ts WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$ts", NowMs());
        cmd.ExecuteNonQuery();
    }
}
