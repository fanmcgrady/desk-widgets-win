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
/// 优先 WSL ~/.config/cursor/auth.json（agent 登录）；否则尝试解密 IDE state.vscdb。
/// </summary>
internal static class CursorUsageService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly object Gate = new();
    private static CursorUsageInfo _cached = CursorUsageInfo.Unknown;
    private static DateTime _nextFetchUtc = DateTime.MinValue;
    private static int _busy;

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
                        _nextFetchUtc = DateTime.UtcNow + RefreshInterval;
                    }
                }
                catch
                {
                    lock (Gate)
                    {
                        if (_cached.State == CursorUsageState.Unknown)
                            _cached = CursorUsageInfo.Unavailable;
                        _nextFetchUtc = DateTime.UtcNow + TimeSpan.FromMinutes(2);
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _busy, 0);
                }
            });
        }

        lock (Gate) return _cached;
    }

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

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
            if (plan.TryGetProperty("totalPercentUsed", out var tp) && tp.TryGetDouble(out var v))
                pct = v;
            else if (plan.TryGetProperty("apiPercentUsed", out var ap) && ap.TryGetDouble(out var v2))
                pct = v2;
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
        // 1) WSL agent 登录（明文 JWT）
        var wsl = await TryReadWslAuthTokenAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(wsl))
            return wsl;

        // 2) IDE state.vscdb（可能是 safe-storage 密文）
        return TryReadIdeAccessToken();
    }

    private static async Task<string?> TryReadWslAuthTokenAsync()
    {
        try
        {
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
            var stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync().ConfigureAwait(false);
            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                return null;

            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.TryGetProperty("accessToken", out var t))
            {
                var token = t.GetString();
                if (!string.IsNullOrWhiteSpace(token) && token.Contains('.'))
                    return token;
            }
        }
        catch { /* ignore */ }

        return null;
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

            // 只读打开；Microsoft.Data.Sqlite 未引用时用 sqlite3 进程不稳，改 ADO 可选。
            // 这里用轻量：System.Data.SQLite 没有，直接读文件不现实。用 Process sqlite3 或手动解析。
            // DesktopBox 无额外依赖：调 powershell/python 太重。改用 Microsoft.Data.Sqlite 包。
            return ReadTokenFromVscdb(db);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadTokenFromVscdb(string dbPath)
    {
        // 无 SQLite 包时：用 Windows 自带 python 或跳过。为稳健加 Microsoft.Data.Sqlite。
        // 先尝试：若明文/密文都能解则返回。
        try
        {
            using var con = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT value FROM ItemTable WHERE key=$k LIMIT 1";
            cmd.Parameters.AddWithValue("$k", "cursorAuth/accessToken");
            var raw = cmd.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            if (raw.Contains('.'))
                return raw; // 旧明文 JWT
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
        try
        {
            var localState = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Cursor", "Local State");
            if (!File.Exists(localState))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(localState));
            if (!doc.RootElement.TryGetProperty("os_crypt", out var oc)
                || !oc.TryGetProperty("encrypted_key", out var ek))
                return null;
            var raw = Convert.FromBase64String(ek.GetString() ?? "");
            // prefix "DPAPI"
            if (raw.Length < 6 || Encoding.ASCII.GetString(raw, 0, 5) != "DPAPI")
                return null;
            return ProtectedData.Unprotect(raw.AsSpan(5).ToArray(), null, DataProtectionScope.CurrentUser);
        }
        catch
        {
            return null;
        }
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
