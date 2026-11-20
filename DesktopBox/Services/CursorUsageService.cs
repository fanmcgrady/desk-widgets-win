using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopBox.Services;

/// <summary>
/// 读取本机 Cursor 登录态，向 Dashboard 拉当前周期用量。
/// 优先 WSL ~/.config/cursor/auth.json（限时）；否则解密 IDE state.vscdb。
/// </summary>
internal static class CursorUsageService
{
    private static readonly HttpClient Http = CreateHttp();
    private static readonly object Gate = new();
    private static CursorUsageInfo _cached = CursorUsageInfo.Unknown;
    private static DateTime _nextFetchUtc = DateTime.MinValue;
    private static int _busy;

    /// <summary>后台拉取结束后触发（任意线程）。</summary>
    public static event Action? Updated;

    public static CursorUsageInfo Cached
    {
        get { lock (Gate) return _cached; }
    }

    /// <summary>按节流触发后台刷新；立刻返回缓存。</summary>
    public static CursorUsageInfo PeekAndRefresh()
    {
        var now = DateTime.UtcNow;
        bool due;
        lock (Gate)
            due = now >= _nextFetchUtc;

        if (due && Interlocked.CompareExchange(ref _busy, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var info = await FetchAsync().ConfigureAwait(false);
                    lock (Gate)
                    {
                        _cached = info;
                        _nextFetchUtc = DateTime.UtcNow + (
                            info.State == CursorUsageState.Ok
                                ? RefreshIntervalOk
                                : RefreshIntervalFail);
                    }
                }
                catch
                {
                    lock (Gate)
                    {
                        if (_cached.State == CursorUsageState.Unknown)
                            _cached = CursorUsageInfo.Unavailable;
                        _nextFetchUtc = DateTime.UtcNow + RefreshIntervalFail;
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _busy, 0);
                    try { Updated?.Invoke(); } catch { /* ignore */ }
                }
            });
        }

        lock (Gate) return _cached;
    }

    private static readonly TimeSpan RefreshIntervalOk = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RefreshIntervalFail = TimeSpan.FromMinutes(1);

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "DeskWidgets/1.0");
        return c;
    }

    private static async Task<CursorUsageInfo> FetchAsync()
    {
        var token = await ResolveAccessTokenAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            return CursorUsageInfo.Unavailable;

        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var resp = await Http.SendAsync(req).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return CursorUsageInfo.Unavailable;

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var plan = root.TryGetProperty("planUsage", out var p) ? p : default;
        var msg = root.TryGetProperty("displayMessage", out var m) ? m.GetString() ?? "" : "";
        double pct = -1;
        if (plan.ValueKind == JsonValueKind.Object)
        {
            if (TryReadDouble(plan, "totalPercentUsed", out var v))
                pct = v;
            else if (TryReadDouble(plan, "autoPercentUsed", out var v2))
                pct = v2;
            else if (TryReadDouble(plan, "apiPercentUsed", out var v3))
                pct = v3;
        }

        // 无百分比时从 displayMessage 里抠 "23%"
        if (pct < 0 && !string.IsNullOrWhiteSpace(msg))
        {
            var i = msg.IndexOf('%');
            if (i > 0)
            {
                var s = i - 1;
                while (s >= 0 && (char.IsDigit(msg[s]) || msg[s] == '.'))
                    s--;
                if (double.TryParse(msg[(s + 1)..i], out var fromMsg))
                    pct = fromMsg;
            }
        }

        string? resetHint = null;
        if (root.TryGetProperty("billingCycleEnd", out var endEl))
        {
            var endMs = ParseEpochMs(endEl);
            if (endMs is not null)
            {
                var local = DateTimeOffset.FromUnixTimeMilliseconds(endMs.Value).ToLocalTime();
                resetHint = $"重置 {local:MM-dd}";
            }
        }

        if (pct < 0 && string.IsNullOrWhiteSpace(msg))
            return CursorUsageInfo.Unavailable;

        if (pct < 0) pct = 0;
        return new CursorUsageInfo(CursorUsageState.Ok, pct, msg, resetHint);
    }

    private static bool TryReadDouble(JsonElement obj, string name, out double value)
    {
        value = 0;
        if (!obj.TryGetProperty(name, out var el))
            return false;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out value))
            return true;
        if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), out value))
            return true;
        return false;
    }

    private static long? ParseEpochMs(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n))
            return n;
        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out var s))
            return s;
        return null;
    }

    private static async Task<string?> ResolveAccessTokenAsync()
    {
        var wsl = await TryReadWslAuthTokenAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(wsl))
            return wsl;

        return TryReadIdeAccessToken();
    }

    private static async Task<string?> TryReadWslAuthTokenAsync()
    {
        try
        {
            // 先走 UNC，避免 wsl.exe 冷启动卡住
            var unc = TryFindWslAuthJsonUnc();
            if (unc is not null)
            {
                var text = await File.ReadAllTextAsync(unc).ConfigureAwait(false);
                var token = ParseAccessToken(text);
                if (!string.IsNullOrWhiteSpace(token))
                    return token;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("bash");
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add("cat \"$HOME/.config/cursor/auth.json\" 2>/dev/null");

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var exited = await WaitForExitAsync(proc, TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            if (!exited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return null;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                return null;

            return ParseAccessToken(stdout);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryFindWslAuthJsonUnc()
    {
        foreach (var root in new[] { @"\\wsl.localhost", @"\\wsl$" })
        {
            try
            {
                if (!Directory.Exists(root))
                    continue;
                foreach (var distro in Directory.EnumerateDirectories(root))
                {
                    try
                    {
                        var homeRoot = Path.Combine(distro, "home");
                        if (!Directory.Exists(homeRoot))
                            continue;
                        foreach (var userDir in Directory.EnumerateDirectories(homeRoot))
                        {
                            var auth = Path.Combine(userDir, ".config", "cursor", "auth.json");
                            if (File.Exists(auth))
                                return auth;
                        }
                    }
                    catch { /* 单个发行版不可读则跳过 */ }
                }
            }
            catch { /* ignore */ }
        }

        return null;
    }

    private static string? ParseAccessToken(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("accessToken", out var t))
                return null;
            var token = t.GetString();
            if (!string.IsNullOrWhiteSpace(token) && token.Contains('.'))
                return token;
        }
        catch { /* ignore */ }

        return null;
    }

    private static async Task<bool> WaitForExitAsync(Process proc, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static string? TryReadIdeAccessToken()
    {
        try
        {
            var db = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Cursor", "User", "globalStorage", "state.vscdb");
            if (!File.Exists(db))
                return null;
            return ReadTokenFromVscdb(db);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadTokenFromVscdb(string dbPath)
    {
        try
        {
            using var con = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={dbPath};Mode=ReadOnly;Cache=Shared");
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT value FROM ItemTable WHERE key=$k LIMIT 1";
            cmd.Parameters.AddWithValue("$k", "cursorAuth/accessToken");
            var raw = cmd.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            if (raw.Contains('.'))
                return raw;
            if (raw.StartsWith("cursor-safe-storage:v1:", StringComparison.Ordinal))
                return DecryptSafeStorage(raw);
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string? DecryptSafeStorage(string stored)
    {
        try
        {
            var b64 = stored["cursor-safe-storage:v1:".Length..];
            var blob = Convert.FromBase64String(b64);
            if (blob.Length < 3 + 12 + 16)
                return null;
            if (blob[0] != (byte)'v' || blob[1] != (byte)'1' || blob[2] != (byte)'0')
                return null;

            var key = LoadOsCryptKey();
            if (key is null || key.Length != 32)
                return null;

            var nonce = blob.AsSpan(3, 12);
            var tag = blob.AsSpan(blob.Length - 16, 16);
            var cipher = blob.AsSpan(15, blob.Length - 3 - 12 - 16);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? LoadOsCryptKey()
    {
        foreach (var localState in CandidateLocalStatePaths())
        {
            try
            {
                if (!File.Exists(localState))
                    continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(localState));
                if (!doc.RootElement.TryGetProperty("os_crypt", out var oc)
                    || !oc.TryGetProperty("encrypted_key", out var ek))
                    continue;
                var raw = Convert.FromBase64String(ek.GetString() ?? "");
                if (raw.Length < 6 || Encoding.ASCII.GetString(raw, 0, 5) != "DPAPI")
                    continue;
                return ProtectedData.Unprotect(raw.AsSpan(5).ToArray(), null, DataProtectionScope.CurrentUser);
            }
            catch
            {
                // 试下一个路径
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateLocalStatePaths()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Cursor", "Local State");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cursor", "Local State");
    }
}

internal enum CursorUsageState
{
    Unknown,
    Unavailable,
    Ok,
}

internal readonly record struct CursorUsageInfo(
    CursorUsageState State,
    double PercentUsed,
    string Message,
    string? ResetHint)
{
    public static CursorUsageInfo Unknown => new(CursorUsageState.Unknown, -1, "", null);
    public static CursorUsageInfo Unavailable => new(CursorUsageState.Unavailable, -1, "", null);

    public double PercentRemaining =>
        PercentUsed < 0 ? -1 : Math.Clamp(100 - PercentUsed, 0, 100);
}
