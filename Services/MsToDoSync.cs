using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace DeskWidgets.Services;

public sealed class MsToDoSync
{
    private static readonly string[] Scopes = ["Tasks.ReadWrite", "User.Read"];
    private static readonly HttpClient Http = new();

    private readonly Store _store;
    private MsSyncConfig _cfg;
    private IPublicClientApplication? _pca;
    private string? _accountLabel;

    public MsToDoSync(Store store)
    {
        _store = store;
        _cfg = MsSyncConfig.Load();
    }

    public bool HasClientId => !string.IsNullOrWhiteSpace(_cfg.ClientId);
    public bool IsSignedIn => _accountLabel != null;
    public string? AccountLabel => _accountLabel;
    public MsSyncConfig Config => _cfg;

    public void ReloadConfig() => _cfg = MsSyncConfig.Load();

    private async Task<IPublicClientApplication> GetAppAsync()
    {
        if (_pca != null) return _pca;
        if (!HasClientId)
            throw new InvalidOperationException("尚未配置 ClientId，请先填写 ms-sync.json。");

        _pca = PublicClientApplicationBuilder
            .Create(_cfg.ClientId.Trim())
            .WithAuthority(AzureCloudInstance.AzurePublic, "consumers")
            .WithRedirectUri("http://localhost")
            .Build();

        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeskWidgets",
            "msal");
        Directory.CreateDirectory(cacheDir);
        var storage = new StorageCreationPropertiesBuilder("msal.cache", cacheDir).Build();
        var cacheHelper = await MsalCacheHelper.CreateAsync(storage).ConfigureAwait(false);
        cacheHelper.RegisterCache(_pca.UserTokenCache);
        return _pca;
    }

    public async Task<string> SignInAsync()
    {
        var app = await GetAppAsync().ConfigureAwait(false);
        var accounts = await app.GetAccountsAsync().ConfigureAwait(false);
        AuthenticationResult result;
        try
        {
            result = await app.AcquireTokenSilent(Scopes, accounts.FirstOrDefault())
                .ExecuteAsync()
                .ConfigureAwait(false);
        }
        catch (MsalUiRequiredException)
        {
            result = await app.AcquireTokenInteractive(Scopes)
                .WithPrompt(Prompt.SelectAccount)
                .ExecuteAsync()
                .ConfigureAwait(false);
        }

        _accountLabel = result.Account?.Username ?? "已登录";
        return _accountLabel;
    }

    public async Task SignOutAsync()
    {
        if (_pca == null && HasClientId)
            await GetAppAsync().ConfigureAwait(false);
        if (_pca == null) return;
        foreach (var a in await _pca.GetAccountsAsync().ConfigureAwait(false))
            await _pca.RemoveAsync(a).ConfigureAwait(false);
        _accountLabel = null;
    }

    private async Task<string?> TryGetTokenSilentAsync()
    {
        var app = await GetAppAsync().ConfigureAwait(false);
        var accounts = await app.GetAccountsAsync().ConfigureAwait(false);
        var account = accounts.FirstOrDefault();
        if (account == null) return null;
        try
        {
            var result = await app.AcquireTokenSilent(Scopes, account)
                .ExecuteAsync()
                .ConfigureAwait(false);
            _accountLabel = result.Account?.Username ?? _accountLabel;
            return result.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            return null;
        }
    }

    private async Task<string> GetTokenAsync(bool allowInteractive)
    {
        var silent = await TryGetTokenSilentAsync().ConfigureAwait(false);
        if (silent != null) return silent;
        if (!allowInteractive)
            throw new InvalidOperationException("需要先登录微软账号。");

        var app = await GetAppAsync().ConfigureAwait(false);
        var result = await app.AcquireTokenInteractive(Scopes)
            .WithPrompt(Prompt.SelectAccount)
            .ExecuteAsync()
            .ConfigureAwait(false);
        _accountLabel = result.Account?.Username ?? _accountLabel;
        return result.AccessToken;
    }

    /// <summary>已登录时静默同步；未登录返回 false。</summary>
    public async Task<bool> TrySyncSilentAsync(CancellationToken ct = default)
    {
        if (!HasClientId) return false;
        var token = await TryGetTokenSilentAsync().ConfigureAwait(false);
        if (token == null) return false;
        await SyncWithTokenAsync(token, ct).ConfigureAwait(false);
        return true;
    }

    public async Task SyncAsync(CancellationToken ct = default)
    {
        var token = await GetTokenAsync(allowInteractive: true).ConfigureAwait(false);
        await SyncWithTokenAsync(token, ct).ConfigureAwait(false);
    }

    private async Task SyncWithTokenAsync(string token, CancellationToken ct)
    {
        var listId = await EnsureListAsync(token, ct).ConfigureAwait(false);
        var remote = await ListTasksAsync(token, listId, ct).ConfigureAwait(false);
        var remoteById = remote.ToDictionary(t => t.Id, StringComparer.Ordinal);

        // 远端 → 本地
        foreach (var rt in remote)
        {
            ct.ThrowIfCancellationRequested();
            var due = DueFromGraph(rt.DueDateTime);
            var body = StripHtml(rt.BodyContent);
            var done = string.Equals(rt.Status, "completed", StringComparison.OrdinalIgnoreCase);
            var gm = ToUnixMs(rt.LastModifiedDateTime);
            var local = _store.FindByGraphId(rt.Id);
            if (local == null)
            {
                _store.InsertFromGraph(rt.Title, body, done, due, rt.Id, gm);
                continue;
            }

            if (local.Deleted)
            {
                // 本地已删：再推一次删除
                continue;
            }

            if (gm >= local.UpdatedAt)
                _store.ApplyFromGraph(local.Id, rt.Title, body, done, due, gm, rt.Id);
        }

        // 本地 → 远端
        foreach (var local in _store.ListTodosForSync())
        {
            ct.ThrowIfCancellationRequested();
            if (local.Deleted)
            {
                if (!string.IsNullOrEmpty(local.GraphId))
                {
                    await DeleteTaskAsync(token, listId, local.GraphId!, ct).ConfigureAwait(false);
                    _store.HardDeleteTodo(local.Id);
                }
                else
                {
                    _store.HardDeleteTodo(local.Id);
                }
                continue;
            }

            if (string.IsNullOrEmpty(local.GraphId))
            {
                var created = await CreateTaskAsync(token, listId, local, ct).ConfigureAwait(false);
                _store.BindGraph(local.Id, created.Id, ToUnixMs(created.LastModifiedDateTime));
                continue;
            }

            if (!remoteById.TryGetValue(local.GraphId!, out var remoteTask))
            {
                // 远端没有了：本地也清掉（除非本地更新更晚——仍重建）
                if (local.UpdatedAt > local.GraphModifiedMs)
                {
                    var created = await CreateTaskAsync(token, listId, local, ct).ConfigureAwait(false);
                    _store.BindGraph(local.Id, created.Id, ToUnixMs(created.LastModifiedDateTime));
                }
                else
                {
                    _store.DeleteTodo(local.Id);
                    _store.HardDeleteTodo(local.Id);
                }
                continue;
            }

            if (local.UpdatedAt > ToUnixMs(remoteTask.LastModifiedDateTime))
            {
                var updated = await PatchTaskAsync(token, listId, local.GraphId!, local, ct).ConfigureAwait(false);
                _store.BindGraph(local.Id, local.GraphId!, ToUnixMs(updated.LastModifiedDateTime));
            }
        }

        _store.SetMeta("last_sync_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
    }

    private async Task<string> EnsureListAsync(string token, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_cfg.ListId))
            return _cfg.ListId;

        var lists = await GetJsonAsync(token, "https://graph.microsoft.com/v1.0/me/todo/lists", ct)
            .ConfigureAwait(false);
        string? found = null;
        if (lists.ValueKind == JsonValueKind.Object &&
            lists.TryGetProperty("value", out var arr))
        {
            foreach (var item in arr.EnumerateArray())
            {
                var name = item.GetProperty("displayName").GetString() ?? "";
                if (string.Equals(name, _cfg.ListName, StringComparison.OrdinalIgnoreCase))
                {
                    found = item.GetProperty("id").GetString();
                    break;
                }
            }
            // 若没有自定义列表名，用默认「任务」列表
            if (found == null)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var name = item.GetProperty("displayName").GetString() ?? "";
                    if (name is "Tasks" or "任务" or "To Do")
                    {
                        found = item.GetProperty("id").GetString();
                        break;
                    }
                }
            }
            if (found == null && arr.GetArrayLength() > 0)
                found = arr[0].GetProperty("id").GetString();
        }

        if (found == null)
        {
            var created = await PostJsonAsync(
                    token,
                    "https://graph.microsoft.com/v1.0/me/todo/lists",
                    $"{{\"displayName\":{JsonSerializer.Serialize(_cfg.ListName)}}}",
                    ct)
                .ConfigureAwait(false);
            found = created.GetProperty("id").GetString();
        }

        _cfg.ListId = found ?? throw new InvalidOperationException("无法获取 To Do 列表");
        _cfg.Save();
        return _cfg.ListId;
    }

    private async Task<List<GraphTask>> ListTasksAsync(string token, string listId, CancellationToken ct)
    {
        var list = new List<GraphTask>();
        string? url =
            $"https://graph.microsoft.com/v1.0/me/todo/lists/{listId}/tasks?$top=100";
        while (url != null)
        {
            var json = await GetJsonAsync(token, url, ct).ConfigureAwait(false);
            if (json.TryGetProperty("value", out var arr))
            {
                foreach (var item in arr.EnumerateArray())
                    list.Add(GraphTask.Parse(item));
            }
            url = json.TryGetProperty("@odata.nextLink", out var next)
                ? next.GetString()
                : null;
        }
        return list;
    }

    private async Task<GraphTask> CreateTaskAsync(string token, string listId, TodoItem local, CancellationToken ct)
    {
        var body = BuildTaskJson(local);
        var json = await PostJsonAsync(
                token,
                $"https://graph.microsoft.com/v1.0/me/todo/lists/{listId}/tasks",
                body,
                ct)
            .ConfigureAwait(false);
        return GraphTask.Parse(json);
    }

    private async Task<GraphTask> PatchTaskAsync(string token, string listId, string taskId, TodoItem local, CancellationToken ct)
    {
        var body = BuildTaskJson(local);
        var json = await PatchJsonAsync(
                token,
                $"https://graph.microsoft.com/v1.0/me/todo/lists/{listId}/tasks/{taskId}",
                body,
                ct)
            .ConfigureAwait(false);
        return GraphTask.Parse(json);
    }

    private static async Task DeleteTaskAsync(string token, string listId, string taskId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Delete,
            $"https://graph.microsoft.com/v1.0/me/todo/lists/{listId}/tasks/{taskId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if ((int)resp.StatusCode == 404) return;
        resp.EnsureSuccessStatusCode();
    }

    private static string BuildTaskJson(TodoItem local)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("title", local.Title);
            w.WriteString("status", local.Done ? "completed" : "notStarted");
            w.WritePropertyName("body");
            w.WriteStartObject();
            w.WriteString("content", ToHtml(local.Body));
            w.WriteString("contentType", "html");
            w.WriteEndObject();
            if (!string.IsNullOrEmpty(local.DueDate) &&
                DateTime.TryParse(local.DueDate, out var due))
            {
                w.WritePropertyName("dueDateTime");
                w.WriteStartObject();
                w.WriteString("dateTime", due.ToString("yyyy-MM-dd") + "T00:00:00.0000000");
                w.WriteString("timeZone", "China Standard Time");
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string ToHtml(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var enc = System.Net.WebUtility.HtmlEncode(text).Replace("\r\n", "\n").Replace("\n", "<br/>");
        return $"<p>{enc}</p>";
    }

    private static string StripHtml(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var t = html.Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("</p>", "\n", StringComparison.OrdinalIgnoreCase);
        t = Regex.Replace(t, "<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(t).Trim();
    }

    private static string? DueFromGraph(JsonElement? due)
    {
        if (due == null || due.Value.ValueKind != JsonValueKind.Object) return null;
        if (!due.Value.TryGetProperty("dateTime", out var dt)) return null;
        var s = dt.GetString();
        if (string.IsNullOrEmpty(s) || s.Length < 10) return null;
        return s[..10];
    }

    private static long ToUnixMs(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return 0;
        return DateTimeOffset.TryParse(iso, out var dto)
            ? dto.ToUnixTimeMilliseconds()
            : 0;
    }

    private static async Task<JsonElement> GetJsonAsync(string token, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Graph GET 失败 {(int)resp.StatusCode}: {text}");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> PostJsonAsync(string token, string url, string json, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Graph POST 失败 {(int)resp.StatusCode}: {text}");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> PatchJsonAsync(string token, string url, string json, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Graph PATCH 失败 {(int)resp.StatusCode}: {text}");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private sealed class GraphTask
    {
        public string Id { get; init; } = "";
        public string Title { get; init; } = "";
        public string Status { get; init; } = "";
        public string BodyContent { get; init; } = "";
        public string? LastModifiedDateTime { get; init; }
        public JsonElement? DueDateTime { get; init; }

        public static GraphTask Parse(JsonElement item)
        {
            string body = "";
            if (item.TryGetProperty("body", out var b) &&
                b.TryGetProperty("content", out var c))
                body = c.GetString() ?? "";

            JsonElement? due = null;
            if (item.TryGetProperty("dueDateTime", out var d) && d.ValueKind == JsonValueKind.Object)
                due = d.Clone();

            return new GraphTask
            {
                Id = item.GetProperty("id").GetString() ?? "",
                Title = item.TryGetProperty("title", out var t) ? (t.GetString() ?? "") : "",
                Status = item.TryGetProperty("status", out var s) ? (s.GetString() ?? "") : "",
                BodyContent = body,
                LastModifiedDateTime = item.TryGetProperty("lastModifiedDateTime", out var lm)
                    ? lm.GetString()
                    : null,
                DueDateTime = due,
            };
        }
    }
}
