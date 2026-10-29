using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace DeskWidgets.Services;

public sealed record CyberNewsItem(
    string Id,
    string Title,
    string Summary,
    string Link,
    string Source,
    DateTimeOffset? Published);

/// <summary>网安资讯 RSS；未读收件箱可跨次拉取累加；已读从列表消失。</summary>
public sealed class CyberNewsService
{
    private const int MaxUnreadInbox = 300;
    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    /// <summary>网络拉取最短间隔；切页签只读本地收件箱，整点/刷新才拉。</summary>
    private static readonly TimeSpan FetchMinInterval = TimeSpan.FromHours(1);

    private static readonly string[] FeedUrls =
    {
        // 快讯 / 科技热点（标题党少、扫一眼能懂）
        "https://www.solidot.org/index.rss",          // Solidot
        "https://www.ithome.com/rss/",                // IT之家
        "https://www.landiannews.com/feed",           // 蓝点网
        "https://www.oschina.net/news/rss",           // 开源中国·资讯
        "https://sspai.com/feed",                     // 少数派
        // 网安媒体（偏新闻，不是长文分析）
        "https://www.4hou.com/feed",                  // 嘶吼
        "https://www.freebuf.com/feed",               // FreeBuf
        "https://krebsonsecurity.com/feed/",          // Krebs
        // 技术详情博客（可慢看）
        "https://xz.aliyun.com/feed",                 // 先知
        "https://blog.netlab.360.com/rss",            // 360 Netlab
        "https://xlab.tencent.com/cn/atom.xml",       // 腾讯玄武
    };

    private static readonly Dictionary<string, string> FeedLabels = new()
    {
        ["solidot.org"] = "Solidot",
        ["ithome.com"] = "IT之家",
        ["landiannews.com"] = "蓝点网",
        ["oschina.net"] = "开源中国",
        ["sspai.com"] = "少数派",
        ["4hou.com"] = "嘶吼",
        ["freebuf.com"] = "FreeBuf",
        ["krebsonsecurity.com"] = "Krebs",
        ["xz.aliyun.com"] = "先知",
        ["netlab.360.com"] = "360 Netlab",
        ["tencent.com"] = "腾讯玄武",
    };

    private readonly string _readPath;
    private readonly string _favPath;
    private readonly string _inboxPath;
    private readonly string _lastFetchPath;
    private readonly HashSet<string> _readIds;
    private List<CyberNewsItem> _favorites;
    private readonly Dictionary<string, CyberNewsItem> _inbox;
    private DateTime _lastFetchUtc = DateTime.MinValue;

    public CyberNewsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeskWidgets");
        Directory.CreateDirectory(dir);
        _readPath = Path.Combine(dir, "cyber-news-read.json");
        _favPath = Path.Combine(dir, "cyber-news-fav.json");
        _inboxPath = Path.Combine(dir, "cyber-news-inbox.json");
        _lastFetchPath = Path.Combine(dir, "cyber-news-lastfetch.txt");
        _readIds = LoadReadIds(_readPath);
        _favorites = LoadFavorites(_favPath);
        _inbox = LoadInbox(_inboxPath);
        _lastFetchUtc = LoadLastFetchUtc(_lastFetchPath);
        // 启动时清掉已读残留
        PruneReadFromInbox(save: true);
    }

    public int UnreadCount => _inbox.Count;

    public IReadOnlyList<CyberNewsItem> CachedUnread =>
        _inbox.Values
            .OrderByDescending(i => i.Published ?? DateTimeOffset.MinValue)
            .ToList();

    public IReadOnlyList<CyberNewsItem> Favorites => _favorites;

    public bool IsFetchDue =>
        _lastFetchUtc == DateTime.MinValue
        || DateTime.UtcNow - _lastFetchUtc >= FetchMinInterval;

    public async Task<IReadOnlyList<CyberNewsItem>> RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        // 非强制：有本地缓存且未到拉取间隔 → 只返回收件箱
        if (!force && _inbox.Count > 0 && !IsFetchDue)
            return CachedUnread;

        var bag = new List<CyberNewsItem>();
        var anyFeedOk = false;
        foreach (var url in FeedUrls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var resp = await Http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) continue;
                // 安全客等源常不带 charset，按 UTF-8 读，避免标题乱码
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                var xml = DecodeXmlBytes(bytes, resp.Content.Headers.ContentType?.CharSet);
                bag.AddRange(ParseFeed(xml, url));
                anyFeedOk = true;
            }
            catch
            {
                // 单源失败忽略
            }
        }

        foreach (var item in bag)
        {
            if (string.IsNullOrWhiteSpace(item.Id)) continue;
            if (_readIds.Contains(item.Id)) continue;
            if (_inbox.ContainsKey(item.Id)) continue;
            _inbox[item.Id] = item;
        }

        TrimInbox();
        SaveInbox();
        // 全部源失败时不推进间隔，避免空收件箱卡住一小时
        if (anyFeedOk)
        {
            _lastFetchUtc = DateTime.UtcNow;
            SaveLastFetchUtc();
        }
        return CachedUnread;
    }

    public void MarkRead(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var changed = _readIds.Add(id);
        var removed = _inbox.Remove(id);
        if (changed) SaveReadIds();
        if (removed) SaveInbox();
    }

    public bool IsFavorite(string id) =>
        _favorites.Any(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public void AddFavorite(CyberNewsItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Id)) return;
        _favorites.RemoveAll(f => f.Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase));
        _favorites.Insert(0, item);
        if (_favorites.Count > 200)
            _favorites = _favorites.Take(200).ToList();
        SaveFavorites();
    }

    public void RemoveFavorite(string id)
    {
        var n = _favorites.RemoveAll(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (n > 0) SaveFavorites();
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // 部分源对非浏览器 UA 直接 403
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 DeskWidgets/1.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/rss+xml, application/atom+xml, application/xml, text/xml, */*");
        return c;
    }

    private static HashSet<string> LoadReadIds(string path)
    {
        try
        {
            if (!File.Exists(path)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? new();
            return new HashSet<string>(list.Where(s => !string.IsNullOrWhiteSpace(s)), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveReadIds()
    {
        try
        {
            var keep = _readIds.TakeLast(800).ToList();
            _readIds.Clear();
            foreach (var id in keep) _readIds.Add(id);
            File.WriteAllText(_readPath, JsonSerializer.Serialize(keep, JsonOpts));
        }
        catch { /* ignore */ }
    }

    private static List<CyberNewsItem> LoadFavorites(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<List<CyberNewsItem>>(File.ReadAllText(path)) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private void SaveFavorites()
    {
        try
        {
            File.WriteAllText(_favPath, JsonSerializer.Serialize(_favorites, JsonOpts));
        }
        catch { /* ignore */ }
    }

    private static Dictionary<string, CyberNewsItem> LoadInbox(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
            var list = JsonSerializer.Deserialize<List<CyberNewsItem>>(File.ReadAllText(path)) ?? new();
            var map = new Dictionary<string, CyberNewsItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in list)
            {
                if (string.IsNullOrWhiteSpace(item.Id)) continue;
                map[item.Id] = item;
            }
            return map;
        }
        catch
        {
            return new Dictionary<string, CyberNewsItem>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveInbox()
    {
        try
        {
            var list = _inbox.Values
                .OrderByDescending(i => i.Published ?? DateTimeOffset.MinValue)
                .ToList();
            File.WriteAllText(_inboxPath, JsonSerializer.Serialize(list, JsonOpts));
        }
        catch { /* ignore */ }
    }

    private void PruneReadFromInbox(bool save)
    {
        var drop = _inbox.Keys.Where(id => _readIds.Contains(id)).ToList();
        foreach (var id in drop) _inbox.Remove(id);
        if (save && drop.Count > 0) SaveInbox();
    }

    private void TrimInbox()
    {
        if (_inbox.Count <= MaxUnreadInbox) return;
        var keep = _inbox.Values
            .OrderByDescending(i => i.Published ?? DateTimeOffset.MinValue)
            .Take(MaxUnreadInbox)
            .ToDictionary(i => i.Id, i => i, StringComparer.OrdinalIgnoreCase);
        _inbox.Clear();
        foreach (var (k, v) in keep) _inbox[k] = v;
    }

    private static IEnumerable<CyberNewsItem> ParseFeed(string xml, string feedUrl)
    {
        var doc = new XmlDocument { XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        });
        doc.Load(reader);

        var nsmgr = new XmlNamespaceManager(doc.NameTable);
        nsmgr.AddNamespace("atom", "http://www.w3.org/2005/Atom");
        var source = LabelFor(feedUrl);

        var items = doc.SelectNodes("//item");
        if (items is { Count: > 0 })
        {
            foreach (XmlNode node in items)
            {
                var title = Inner(node, "title");
                var link = Inner(node, "link");
                var guid = Inner(node, "guid");
                var descHtml = node.SelectSingleNode("description")?.InnerText;
                var contentHtml = node.SelectSingleNode("*[local-name()='encoded']")?.InnerText;
                var pub = Inner(node, "pubDate");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link)) continue;

                var summaryHtml = !string.IsNullOrWhiteSpace(contentHtml) ? contentHtml : descHtml;
                yield return new CyberNewsItem(
                    Id: !string.IsNullOrWhiteSpace(guid) ? guid.Trim() : link.Trim(),
                    Title: Decode(title),
                    Summary: StripHtml(summaryHtml),
                    Link: link.Trim(),
                    Source: source,
                    Published: TryParseDate(pub));
            }
            yield break;
        }

        // 先知等 Atom：可能无命名空间；空结果时再试 local-name
        var entries = doc.SelectNodes("//atom:entry", nsmgr);
        if (entries == null || entries.Count == 0)
            entries = doc.SelectNodes("//*[local-name()='entry']");
        if (entries == null || entries.Count == 0) yield break;
        foreach (XmlNode node in entries)
        {
            var title = InnerLocal(node, "title");
            var link = AtomLink(node, nsmgr) ?? InnerLocal(node, "link");
            var id = InnerLocal(node, "id") ?? link;
            var summary = InnerLocal(node, "summary") ?? InnerLocal(node, "content");
            var updated = InnerLocal(node, "updated") ?? InnerLocal(node, "published");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link)) continue;
            yield return new CyberNewsItem(
                Id: (id ?? link).Trim(),
                Title: Decode(title),
                Summary: StripHtml(summary),
                Link: link.Trim(),
                Source: source,
                Published: TryParseDate(updated));
        }
    }

    private static string DecodeXmlBytes(byte[] bytes, string? charset)
    {
        Encoding enc;
        try
        {
            enc = !string.IsNullOrWhiteSpace(charset)
                ? Encoding.GetEncoding(charset)
                : Encoding.UTF8;
        }
        catch
        {
            enc = Encoding.UTF8;
        }
        var text = enc.GetString(bytes);
        // XML 声明里的 encoding 与实际不符时，优先按声明再解一次
        var m = Regex.Match(text, @"encoding\s*=\s*[""'](?<e>[^""']+)[""']", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            try
            {
                var declared = Encoding.GetEncoding(m.Groups["e"].Value);
                if (!string.Equals(declared.WebName, enc.WebName, StringComparison.OrdinalIgnoreCase))
                    text = declared.GetString(bytes);
            }
            catch { /* keep */ }
        }
        return text;
    }

    private static string LabelFor(string feedUrl)
    {
        foreach (var (host, label) in FeedLabels)
            if (feedUrl.Contains(host, StringComparison.OrdinalIgnoreCase))
                return label;
        return "网安";
    }

    private static string? Inner(XmlNode parent, string name) =>
        parent.SelectSingleNode(name)?.InnerText?.Trim();

    private static string? InnerLocal(XmlNode parent, string local) =>
        parent.SelectSingleNode($"*[local-name()='{local}']")?.InnerText?.Trim();

    private static string? AtomLink(XmlNode entry, XmlNamespaceManager nsmgr)
    {
        var links = entry.SelectNodes("atom:link", nsmgr);
        if (links == null || links.Count == 0)
            links = entry.SelectNodes("*[local-name()='link']");
        if (links == null || links.Count == 0) return null;
        foreach (XmlNode link in links)
        {
            var rel = link.Attributes?["rel"]?.Value;
            var href = link.Attributes?["href"]?.Value;
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (string.IsNullOrEmpty(rel) || rel.Equals("alternate", StringComparison.OrdinalIgnoreCase))
                return href.Trim();
        }
        return links[0]?.Attributes?["href"]?.Value?.Trim();
    }

    private static DateTime LoadLastFetchUtc(string path)
    {
        try
        {
            if (!File.Exists(path)) return DateTime.MinValue;
            return DateTime.TryParse(File.ReadAllText(path).Trim(), null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var t)
                ? t.ToUniversalTime()
                : DateTime.MinValue;
        }
        catch { return DateTime.MinValue; }
    }

    private void SaveLastFetchUtc()
    {
        try
        {
            File.WriteAllText(_lastFetchPath, _lastFetchUtc.ToString("o"));
        }
        catch { /* ignore */ }
    }

    private static DateTimeOffset? TryParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTimeOffset.TryParse(raw, out var dto) ? dto : null;
    }

    private static string Decode(string s) =>
        System.Net.WebUtility.HtmlDecode(s).Trim();

    private static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var t = System.Net.WebUtility.HtmlDecode(html);
        t = Regex.Replace(t, "<[^>]+>", " ");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        if (t.Length > 280) t = t[..280] + "…";
        return t;
    }
}
