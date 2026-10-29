using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DeskWidgets.Services;

public sealed class AgentSkillInfo
{
    public required string Id { get; init; }
    public required string Summary { get; init; }
    public required string Path { get; init; }
}

/// <summary>扫描 %USERPROFILE%\.cursor\skills 下的 SKILL.md。</summary>
public static class AgentSkillCatalog
{
    public static string SkillsRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cursor",
            "skills");

    public static IReadOnlyList<AgentSkillInfo> List()
    {
        var root = SkillsRoot;
        if (!Directory.Exists(root)) return Array.Empty<AgentSkillInfo>();

        var list = new List<AgentSkillInfo>();
        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var skillFile = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(skillFile)) continue;
            try
            {
                var text = File.ReadAllText(skillFile);
                var id = ExtractName(text) ?? Path.GetFileName(dir);
                var summary = ShortSummary(ExtractDescription(text), id);
                list.Add(new AgentSkillInfo
                {
                    Id = id,
                    Summary = summary,
                    Path = skillFile,
                });
            }
            catch
            {
                list.Add(new AgentSkillInfo
                {
                    Id = Path.GetFileName(dir),
                    Summary = Path.GetFileName(dir),
                    Path = skillFile,
                });
            }
        }
        return list;
    }

    public static string FormatWelcomeBlock()
    {
        var skills = List();
        if (skills.Count == 0)
            return "可用 Skills：暂无（目录 " + SkillsRoot + "）";

        var sb = new StringBuilder();
        sb.AppendLine("可用 Skills（说触发词即可，规则在 ~/.cursor/skills）：");
        foreach (var s in skills)
            sb.AppendLine("· " + s.Id + " — " + s.Summary);
        return sb.ToString().TrimEnd();
    }

    private static string? ExtractName(string text)
    {
        var m = Regex.Match(text, @"^name:\s*(.+)\s*$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.Trim().Trim('"') : null;
    }

    private static string ExtractDescription(string text)
    {
        // description: "单行"
        var one = Regex.Match(text, @"^description:\s*""([^""]*)""", RegexOptions.Multiline);
        if (one.Success) return one.Groups[1].Value.Trim();

        one = Regex.Match(text, @"^description:\s*(?!>-)(.+)\s*$", RegexOptions.Multiline);
        if (one.Success)
        {
            var v = one.Groups[1].Value.Trim().Trim('"');
            if (v is not (">-" or "|-" or ">" or "|")) return v;
        }

        // description: >- 多行
        var folded = Regex.Match(
            text,
            @"^description:\s*>-?\s*\r?\n((?:[ \t]+.+\r?\n?)+)",
            RegexOptions.Multiline);
        if (folded.Success)
        {
            var lines = folded.Groups[1].Value
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0);
            return string.Join(" ", lines);
        }

        return "";
    }

    private static string ShortSummary(string description, string id)
    {
        // 优先用内置短标题，列表更易扫
        var known = id.ToLowerInvariant() switch
        {
            "archive-folders" => "归档项目 / 个人 / 报账",
            "expense-reimbursement" => "报销发票与支付截图打包",
            "graduate-admission-retest" => "复试名单导入、机试出题",
            "project-proposal-writer" => "申报书章节起草",
            "word-fillter-438c-08" => "GJB 438C [08] 需求规格",
            "word-fillter-438c-10" => "GJB 438C [10] 概要设计",
            _ => null,
        };
        if (known != null) return known;

        if (string.IsNullOrWhiteSpace(description)) return id;
        var t = description.Replace('\r', ' ').Replace('\n', ' ').Trim();
        // 取到句号/。或截断
        var cut = t.IndexOfAny(['。', '；', ';']);
        if (cut > 8 && cut < 48) return t[..(cut + 1)];
        return t.Length <= 42 ? t : t[..42] + "…";
    }
}
