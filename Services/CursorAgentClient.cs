using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskWidgets.Services;

public enum AgentRuntimeKind
{
    None,
    Windows,
    Wsl,
}

/// <summary>与 cursor-agent CLI 一致：Ask 只读，Plan 先方案，Agent 默认可改文件。</summary>
public enum AgentChatMode
{
    Ask,
    Plan,
    Agent,
}

/// <summary>stream-json 里的一次工具调用（started / completed）。</summary>
public sealed class AgentToolCallInfo
{
    public required string CallId { get; init; }
    public required bool Completed { get; init; }
    /// <summary>如 read / write / shell / edit / grep / glob / delete / todo。</summary>
    public required string Kind { get; init; }
    public string Detail { get; init; } = "";
    public string? ResultSummary { get; init; }
    public bool Failed { get; init; }
}

/// <summary>优先本机 Windows agent，找不到再经 WSL 调 cursor-agent。</summary>
public sealed class CursorAgentClient
{
    private AgentRuntimeKind _kind = AgentRuntimeKind.None;
    private string? _windowsAgentPath;
    private string? _label;
    private bool _resolved;

    /// <summary>Agent 工作区：优先桌面「智能体空间」，便于在侧栏直接改项目。</summary>
    public static string WorkspaceDir
    {
        get
        {
            var deskSpace = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "智能体空间");
            if (Directory.Exists(deskSpace)) return deskSpace;

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DeskWidgets",
                "agent-workspace");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public AgentRuntimeKind RuntimeKind
    {
        get
        {
            EnsureResolved();
            return _kind;
        }
    }

    /// <summary>显示用标签，如 win:agent.exe / wsl:cursor-agent；没有则 null。</summary>
    public string? ResolveAgentPath()
    {
        EnsureResolved();
        return _label;
    }

    private void EnsureResolved()
    {
        if (_resolved) return;
        _resolved = true;

        var win = FindWindowsAgent();
        if (win != null)
        {
            _kind = AgentRuntimeKind.Windows;
            _windowsAgentPath = win;
            _label = "win:" + Path.GetFileName(win);
            return;
        }

        if (ProbeWslAgent(out var wslName))
        {
            _kind = AgentRuntimeKind.Wsl;
            _label = "wsl:" + wslName;
            return;
        }

        _kind = AgentRuntimeKind.None;
        _label = null;
    }

    /// <summary>强制重新探测（装完 CLI 后点「新对话」可用）。</summary>
    public void Refresh()
    {
        _resolved = false;
        _kind = AgentRuntimeKind.None;
        _windowsAgentPath = null;
        _label = null;
    }

    private static string? FindWindowsAgent()
    {
        string[] names = ["agent.exe", "agent.cmd", "cursor-agent.exe", "cursor-agent.cmd", "agent", "cursor-agent"];
        foreach (var dir in WindowsCandidateDirs())
        {
            foreach (var name in names)
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path)) return path;
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var path = Path.Combine(dir.Trim().Trim('"'), name);
                    if (File.Exists(path)) return path;
                }
                catch { /* ignore */ }
            }
        }

        return null;
    }

    private static IEnumerable<string> WindowsCandidateDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(home, ".local", "bin");
        yield return Path.Combine(local, "cursor-agent");
        yield return Path.Combine(home, "cursor-agent");
        yield return Path.Combine(local, "Programs", "cursor", "resources", "app", "bin");
    }

    private static bool ProbeWslAgent(out string fileName)
    {
        fileName = "cursor-agent";
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
            psi.ArgumentList.Add("export PATH=\"$HOME/.local/bin:$PATH\"; command -v cursor-agent || command -v agent");

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            var stdout = proc.StandardOutput.ReadToEnd().Trim();
            if (!proc.WaitForExit(12000))
            {
                try { proc.Kill(true); } catch { /* ignore */ }
                return false;
            }

            if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout) && stdout.Contains('/'))
            {
                fileName = Path.GetFileName(stdout.Replace('\\', '/'));
                return true;
            }
        }
        catch
        {
            /* WSL 不可用 */
        }

        return false;
    }

    public async Task<string> ChatAsync(
        string prompt,
        bool continueSession,
        AgentChatMode mode,
        Action<string>? onDelta,
        Action<string>? onLog,
        Action<AgentToolCallInfo>? onTool,
        CancellationToken ct)
    {
        EnsureResolved();
        return _kind switch
        {
            AgentRuntimeKind.Windows => await ChatWindowsAsync(prompt, continueSession, mode, onDelta, onLog, onTool, ct).ConfigureAwait(false),
            AgentRuntimeKind.Wsl => await ChatWslAsync(prompt, continueSession, mode, onDelta, onLog, onTool, ct).ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                "未找到 agent。\n优先装 Windows 版（更快）：\nirm 'https://cursor.com/install?win32=true' | iex\n或在 WSL：\ncurl https://cursor.com/install -fsS | bash\n然后 agent login"),
        };
    }

    private static void AppendModeArgs(List<string> args, AgentChatMode mode)
    {
        // CLI：不传 --mode 即为 Agent；Ask/Plan 需显式指定
        if (mode == AgentChatMode.Ask) args.Add("--mode=ask");
        else if (mode == AgentChatMode.Plan) args.Add("--mode=plan");
        // Agent 非交互改文件需要 --force，否则只提案不落地
        if (mode == AgentChatMode.Agent) args.Add("--force");
    }

    private static string ModeCliFlags(AgentChatMode mode)
    {
        return mode switch
        {
            AgentChatMode.Ask => " --mode=ask",
            AgentChatMode.Plan => " --mode=plan",
            AgentChatMode.Agent => " --force",
            _ => " --mode=ask",
        };
    }

    private async Task<string> ChatWindowsAsync(
        string prompt,
        bool continueSession,
        AgentChatMode mode,
        Action<string>? onDelta,
        Action<string>? onLog,
        Action<AgentToolCallInfo>? onTool,
        CancellationToken ct)
    {
        var agent = _windowsAgentPath
                    ?? throw new InvalidOperationException("Windows agent 路径丢失，请点「新对话」重试。");

        var workspace = WorkspaceDir;
        var args = new List<string>
        {
            "-p",
            "--trust",
            "--workspace", workspace,
            "--output-format", "stream-json",
            "--stream-partial-output",
        };
        AppendModeArgs(args, mode);
        if (continueSession) args.Add("--continue");
        args.Add("--");
        args.Add(prompt);

        var isCmd = agent.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                    || agent.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workspace,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (isCmd)
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(agent);
        }
        else
        {
            psi.FileName = agent;
        }
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        var key = Environment.GetEnvironmentVariable("CURSOR_API_KEY");
        if (!string.IsNullOrWhiteSpace(key))
            psi.Environment["CURSOR_API_KEY"] = key;

        onLog?.Invoke(FormatCliCommand(psi, prompt));
        return await RunAndCollectAsync(psi, onDelta, onLog, onTool, ct).ConfigureAwait(false);
    }

    private async Task<string> ChatWslAsync(
        string prompt,
        bool continueSession,
        AgentChatMode mode,
        Action<string>? onDelta,
        Action<string>? onLog,
        Action<AgentToolCallInfo>? onTool,
        CancellationToken ct)
    {
        var winWorkspace = WorkspaceDir;
        var wslWorkspace = ToWslPath(winWorkspace);

        var promptFile = Path.Combine(winWorkspace, ".last-prompt.txt");
        await File.WriteAllTextAsync(promptFile, prompt, new UTF8Encoding(false), ct).ConfigureAwait(false);

        var scriptFile = Path.Combine(winWorkspace, ".run-agent.sh");
        var script = BuildWslRunScript(wslWorkspace, ToWslPath(promptFile), continueSession, mode);
        await File.WriteAllTextAsync(scriptFile, script, new UTF8Encoding(false), ct).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add("bash");
        psi.ArgumentList.Add(ToWslPath(scriptFile));

        var key = Environment.GetEnvironmentVariable("CURSOR_API_KEY");
        if (!string.IsNullOrWhiteSpace(key))
            psi.Environment["CURSOR_API_KEY"] = key;

        onLog?.Invoke($"$ wsl bash {ToWslPath(scriptFile)}");
        onLog?.Invoke($"# prompt: {Truncate(prompt, 120)}");
        return await RunAndCollectAsync(psi, onDelta, onLog, onTool, ct).ConfigureAwait(false);
    }

    private static string FormatCliCommand(ProcessStartInfo psi, string prompt)
    {
        var parts = new List<string> { psi.FileName };
        foreach (var a in psi.ArgumentList)
        {
            if (a == prompt) { parts.Add(QuoteShell(Truncate(prompt, 80))); continue; }
            parts.Add(a.Contains(' ') ? QuoteShell(a) : a);
        }
        return "$ " + string.Join(" ", parts);
    }

    private static string QuoteShell(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
    private static string Truncate(string s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");

    private static async Task<string> RunAndCollectAsync(
        ProcessStartInfo psi,
        Action<string>? onDelta,
        Action<string>? onLog,
        Action<AgentToolCallInfo>? onTool,
        CancellationToken ct)
    {
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!proc.Start())
            throw new InvalidOperationException("无法启动 agent 进程。");

        await using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* ignore */ }
        });

        var assistant = new StringBuilder();
        var stderr = new StringBuilder();

        var errTask = Task.Run(async () =>
        {
            while (await proc.StandardError.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                stderr.AppendLine(line);
                if (!string.IsNullOrWhiteSpace(line))
                    onLog?.Invoke("[stderr] " + line);
            }
        }, ct);

        while (await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            TryConsumeStreamLine(line, assistant, onDelta, onLog, onTool);
        }

        await errTask.ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        onLog?.Invoke($"# exit {proc.ExitCode}");

        var text = assistant.ToString().Trim();
        if (proc.ExitCode != 0 && string.IsNullOrEmpty(text))
        {
            var err = stderr.ToString().Trim();
            if (string.IsNullOrEmpty(err)) err = $"agent 退出码 {proc.ExitCode}";
            throw new InvalidOperationException(err);
        }

        if (string.IsNullOrEmpty(text))
        {
            var fallback = stderr.ToString().Trim();
            if (!string.IsNullOrEmpty(fallback) && !fallback.Contains("error", StringComparison.OrdinalIgnoreCase))
                return fallback;
            return "（无回复）";
        }

        return text;
    }

    private static string BuildWslRunScript(string wslWorkspace, string wslPromptFile, bool continueSession, AgentChatMode mode)
    {
        var cont = continueSession ? " --continue" : "";
        var modeFlags = ModeCliFlags(mode);
        var sb = new StringBuilder();
        sb.Append("#!/usr/bin/env bash\n");
        sb.Append("set -euo pipefail\n");
        sb.Append("export PATH=\"$HOME/.local/bin:$PATH\"\n");
        sb.Append("AGENT_BIN=\"$(command -v cursor-agent || command -v agent || true)\"\n");
        sb.Append("if [ -z \"$AGENT_BIN\" ]; then echo 'cursor-agent not found' >&2; exit 127; fi\n");
        sb.Append("PROMPT=\"$(cat '").Append(wslPromptFile).Append("')\"\n");
        sb.Append("exec \"$AGENT_BIN\" -p --trust");
        sb.Append(modeFlags);
        sb.Append(" --workspace '").Append(wslWorkspace).Append("'");
        sb.Append(" --output-format stream-json --stream-partial-output");
        sb.Append(cont);
        sb.Append(" -- \"$PROMPT\"\n");
        return sb.ToString();
    }

    public static string ToWslPath(string winPath)
    {
        var full = Path.GetFullPath(winPath);
        if (full.Length >= 2 && full[1] == ':')
        {
            var drive = char.ToLowerInvariant(full[0]);
            var rest = full[2..].Replace('\\', '/');
            if (!rest.StartsWith('/')) rest = "/" + rest;
            return "/mnt/" + drive + rest;
        }
        return full.Replace('\\', '/');
    }

    private static void TryConsumeStreamLine(
        string line,
        StringBuilder assistant,
        Action<string>? onDelta,
        Action<string>? onLog,
        Action<AgentToolCallInfo>? onTool)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) return;
            var type = typeEl.GetString();
            var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() : null;

            if (type == "system")
            {
                var model = root.TryGetProperty("model", out var m) ? m.GetString() : null;
                onLog?.Invoke(string.IsNullOrEmpty(model)
                    ? $"[system] {subtype ?? ""}".Trim()
                    : $"[system] {subtype} model={model}");
                return;
            }

            if (type == "tool_call")
            {
                var info = ParseToolCall(root, subtype);
                if (info != null)
                {
                    onLog?.Invoke(FormatToolLog(info));
                    onTool?.Invoke(info);
                }
                return;
            }

            if (type == "assistant")
            {
                if (!root.TryGetProperty("message", out var msg)) return;
                if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                    return;
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text"
                        && block.TryGetProperty("text", out var textEl))
                    {
                        var piece = textEl.GetString() ?? "";
                        if (piece.Length == 0) continue;
                        var hasTs = root.TryGetProperty("timestamp_ms", out _);
                        var hasModelCall = root.TryGetProperty("model_call_id", out _);
                        if (hasTs && !hasModelCall)
                        {
                            if (assistant.Length == 0) onLog?.Invoke("[assistant]");
                            assistant.Append(piece);
                            onDelta?.Invoke(piece);
                        }
                        else if (!hasTs && assistant.Length == 0)
                        {
                            onLog?.Invoke("[assistant]");
                            assistant.Append(piece);
                            onDelta?.Invoke(piece);
                        }
                    }
                }
                return;
            }

            if (type == "result")
            {
                var ms = root.TryGetProperty("duration_ms", out var d) ? d.ToString() : "?";
                onLog?.Invoke($"[result] {ms}ms");
                if (root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String)
                {
                    var final = r.GetString() ?? "";
                    if (!string.IsNullOrEmpty(final) && assistant.Length == 0)
                    {
                        onLog?.Invoke("[assistant]");
                        assistant.Append(final);
                        onDelta?.Invoke(final);
                    }
                }
            }
        }
        catch (JsonException)
        {
            if (line.StartsWith('{')) return;
            if (Regex.IsMatch(line, @"^\s*$")) return;
            assistant.AppendLine(line);
            onDelta?.Invoke(line + "\n");
            onLog?.Invoke(line);
        }
    }

    private static string FormatToolLog(AgentToolCallInfo info)
    {
        var phase = info.Completed ? "done" : "start";
        var detail = string.IsNullOrEmpty(info.Detail) ? "" : " " + info.Detail;
        var summary = string.IsNullOrEmpty(info.ResultSummary) ? "" : " · " + info.ResultSummary;
        return $"[tool:{info.Kind}] {phase}{detail}{summary}";
    }

    private static AgentToolCallInfo? ParseToolCall(JsonElement root, string? subtype)
    {
        var callId = root.TryGetProperty("call_id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(callId)) callId = Guid.NewGuid().ToString("N");
        var completed = string.Equals(subtype, "completed", StringComparison.OrdinalIgnoreCase);

        if (!root.TryGetProperty("tool_call", out var tc) || tc.ValueKind != JsonValueKind.Object)
        {
            return new AgentToolCallInfo
            {
                CallId = callId,
                Completed = completed,
                Kind = "tool",
            };
        }

        foreach (var prop in tc.EnumerateObject())
        {
            var kind = NormalizeToolKind(prop.Name, prop.Value);
            var detail = ExtractToolDetail(prop.Name, prop.Value);
            var (failed, summary) = ExtractToolResult(prop.Value);
            return new AgentToolCallInfo
            {
                CallId = callId,
                Completed = completed,
                Kind = kind,
                Detail = detail,
                ResultSummary = completed ? summary : null,
                Failed = completed && failed,
            };
        }

        return new AgentToolCallInfo
        {
            CallId = callId,
            Completed = completed,
            Kind = "tool",
        };
    }

    private static string NormalizeToolKind(string propName, JsonElement value)
    {
        if (propName.Equals("function", StringComparison.OrdinalIgnoreCase)
            && value.TryGetProperty("name", out var n))
        {
            var name = n.GetString() ?? "function";
            return name.EndsWith("ToolCall", StringComparison.Ordinal)
                ? name[..^"ToolCall".Length].ToLowerInvariant()
                : name.ToLowerInvariant();
        }

        if (propName.EndsWith("ToolCall", StringComparison.Ordinal))
            return propName[..^"ToolCall".Length].ToLowerInvariant();
        return propName.ToLowerInvariant();
    }

    private static string ExtractToolDetail(string propName, JsonElement value)
    {
        if (propName.Equals("function", StringComparison.OrdinalIgnoreCase))
        {
            if (value.TryGetProperty("arguments", out var argsEl))
            {
                if (argsEl.ValueKind == JsonValueKind.String)
                    return Truncate(argsEl.GetString() ?? "", 100);
                if (argsEl.ValueKind == JsonValueKind.Object)
                    return DetailFromArgs(argsEl);
            }
            return "";
        }

        if (value.TryGetProperty("args", out var args))
            return DetailFromArgs(args);
        return "";
    }

    private static string DetailFromArgs(JsonElement args)
    {
        if (args.TryGetProperty("path", out var p))
            return ShortPath(p.GetString() ?? "");
        if (args.TryGetProperty("command", out var c))
            return Truncate(c.GetString() ?? "", 100);
        if (args.TryGetProperty("pattern", out var pat))
            return Truncate(pat.GetString() ?? "", 80);
        if (args.TryGetProperty("query", out var q))
            return Truncate(q.GetString() ?? "", 80);
        if (args.TryGetProperty("glob", out var g))
            return Truncate(g.GetString() ?? "", 80);
        if (args.TryGetProperty("targetDirectory", out var td))
            return ShortPath(td.GetString() ?? "");
        return "";
    }

    private static (bool failed, string? summary) ExtractToolResult(JsonElement value)
    {
        if (!value.TryGetProperty("result", out var result))
            return (false, null);

        if (result.TryGetProperty("success", out var success))
        {
            if (success.TryGetProperty("totalLines", out var lines) && lines.TryGetInt32(out var n))
                return (false, n + " 行");
            if (success.TryGetProperty("linesCreated", out var created) && created.TryGetInt32(out var lc))
                return (false, "写入 " + lc + " 行");
            if (success.TryGetProperty("fileSize", out var size) && size.TryGetInt32(out var fs))
                return (false, fs + " B");
            if (success.TryGetProperty("exitCode", out var exit))
            {
                var code = exit.ValueKind == JsonValueKind.Number ? exit.GetInt32() : 0;
                return (code != 0, "exit " + code);
            }
            return (false, "完成");
        }

        if (result.TryGetProperty("error", out var err))
        {
            var msg = err.ValueKind == JsonValueKind.String
                ? err.GetString()
                : (err.TryGetProperty("message", out var m) ? m.GetString() : "失败");
            return (true, Truncate(msg ?? "失败", 60));
        }

        return (false, "完成");
    }

    private static string ShortPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        path = path.Replace('\\', '/');
        var ws = WorkspaceDir.Replace('\\', '/');
        if (path.StartsWith(ws, StringComparison.OrdinalIgnoreCase))
        {
            var rel = path[ws.Length..].TrimStart('/');
            if (rel.Length > 0) return Truncate(rel, 100);
        }
        var name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? Truncate(path, 100) : name;
    }
}
