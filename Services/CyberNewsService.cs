using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace DeskWidgets.Services;

public sealed record CyberFeed(string Label, string Url);

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
    /// <summary>单源每次最多收下这么多条，避免整站归档把收件箱塞满。</summary>
    private const int MaxItemsPerFeed = 25;
    /// <summary>发布时间早于该窗口的未读从收件箱清掉，避免旧博客占满列表。</summary>
    private static readonly TimeSpan InboxMaxAge = TimeSpan.FromDays(45);
    private static readonly HttpClient Http = CreateClient(useProxy: false);
    private static readonly HttpClient HttpProxy = CreateClient(useProxy: true);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    /// <summary>网络拉取最短间隔；切页签只读本地收件箱，定时/刷新才拉。</summary>
    private static readonly TimeSpan FetchMinInterval = TimeSpan.FromMinutes(15);
    private const string SocksProxy = "socks5://127.0.0.1:1080";
    /// <summary>安全客「安全知识」栏目没有单独 RSS，走帖子接口。</summary>
    private const string AnquankeKnowledgeApi =
        "https://api.anquanke.com/data/v1/posts?size=20&page=1&category=knowledge";

    private static readonly CyberFeed[] DefaultFeeds =
    {
        new("安全客", "https://api.anquanke.com/data/v1/rss"),
        new("绿盟科技技术博客", "https://blog.nsfocus.net/feed/"),
        new("嘶吼", "https://www.4hou.com/feed"),
        new("游侠安全网", "https://www.youxia.org/feed/"),
        new("HackerNews", "https://hackernews.cc/feed"),
        new("阿尔法实验室", "https://blog.topsec.com.cn/feed/"),
        new("安全村", "https://wechat2rss.xlab.app/feed/13267b55f2dbff6e536b4d593408fe0ef977cb43.xml"),
        new("安全客-安全知识", AnquankeKnowledgeApi),
        new("安全牛", "https://wechat2rss.xlab.app/feed/10f1ba549b70cdb4216f7ade606d30a813305aa1.xml"),
        new("奇安信A-TEAM", "https://wechat2rss.xlab.app/feed/7e9f1ce42f56754f4a6bacb9a67385e48ca643fb.xml"),
        new("倾旋的博客", "https://payloads.online/index.xml"),
        new("腾讯安全响应中心", "https://security.tencent.com/index.php/feed/blog/0"),
        new("腾讯科恩", "https://keenlab.tencent.com/zh/atom.xml"),
    };

    private readonly string _readPath;
    private readonly string _favPath;
    private readonly string _inboxPath;
    private readonly string _lastFetchPath;
    private readonly string _feedsPath;
    private readonly List<CyberFeed> _feeds;
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
        _feedsPath = Path.Combine(dir, "cyber-news-feeds.json");
        _feeds = LoadFeeds(_feedsPath, out var feedsDirty);
        if (feedsDirty) SaveFeeds();
        _readIds = LoadReadIds(_readPath);
        _favorites = LoadFavorites(_favPath);
        _inbox = LoadInbox(_inboxPath);
        _lastFetchUtc = LoadLastFetchUtc(_lastFetchPath);
        // 启动时清掉已读残留，并去掉已下线 / 非网安源 / 过期未读
        PruneReadFromInbox(save: false);
        PruneDroppedSourcesFromInbox(save: false);
        PruneStaleFromInbox(save: true);
    }

    private void PruneDroppedSourcesFromInbox(bool save)
    {
        // 旧综合科技源 + 已下线源；以及 Source 标签不在当前 FeedLabels 里的条目
        var droppedHosts = new[]
        {
            "ithome.com", "solidot.org", "landiannews.com", "oschina.net", "sspai.com",
            "cn-sec.com",
        };
        var droppedLabels = new[]
        {
            "IT之家", "Solidot", "蓝点网", "开源中国", "少数派", "CN-SEC",
        };
        var keepLabels = new HashSet<string>(_feeds.Select(f => f.Label), StringComparer.OrdinalIgnoreCase);
        var remove = _inbox.Where(kv =>
            {
                var item = kv.Value;
                if (droppedLabels.Any(l =>
                        item.Source.Contains(l, StringComparison.OrdinalIgnoreCase)))
                    return true;
                if (droppedHosts.Any(h =>
                        (item.Link?.Contains(h, StringComparison.OrdinalIgnoreCase) ?? false)
                        || kv.Key.Contains(h, StringComparison.OrdinalIgnoreCase)))
                    return true;
                // Source 对不上当前网安源标签的，一律清掉
                return !keepLabels.Contains(item.Source);
            })
            .Select(kv => kv.Key)
            .ToList();
        if (remove.Count == 0)
        {
            if (save) SaveInbox();
            return;
        }
        foreach (var id in remove)
            _inbox.Remove(id);
        if (save) SaveInbox();
    }

    public IReadOnlyList<CyberFeed> Sources => _feeds;

    /// <summary>加上一个订阅。失败时返回原因。</summary>
    public string? TryAddSource(string label, string url)
    {
        url = (url ?? "").Trim();
        label = (label ?? "").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            return "地址要以 http:// 或 https:// 开头";
        if (_feeds.Any(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase)))
            return "已经有这个地址";
        if (string.IsNullOrWhiteSpace(label))
            label = string.IsNullOrWhiteSpace(uri.Host) ? url : uri.Host;
        if (label.Length > 40) label = label[..40];
        _feeds.Add(new CyberFeed(label, url));
        SaveFeeds();
        return null;
    }

    public bool RemoveSource(string url)
    {
        var feed = _feeds.FirstOrDefault(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase));
        if (feed == null) return false;
        _feeds.Remove(feed);
        SaveFeeds();
        PruneDroppedSourcesFromInbox(save: true);
        return true;
    }

    /// <summary>未读条数（角标）；已读仍留在热点列表里。</summary>
    public int UnreadCount => _inbox.Values.Count(i => !IsReadId(i.Id) && !IsReadId(i.Link));

    /// <summary>热点列表：最近拉取到的条目（含已读）。</summary>
    public IReadOnlyList<CyberNewsItem> CachedUnread =>
        _inbox.Values
            .OrderByDescending(i => i.Published ?? DateTimeOffset.MinValue)
            .ToList();

    public bool IsRead(CyberNewsItem item) =>
        IsReadId(item.Id) || IsReadId(item.Link);

    public IReadOnlyList<CyberNewsItem> Favorites => _favorites;

    public bool IsFetchDue =>
        _lastFetchUtc == DateTime.MinValue
        || DateTime.UtcNow - _lastFetchUtc >= FetchMinInterval;

    /// <summary>上次成功拉取的本地时间文案；从未拉过则空。</summary>
    public string LastFetchLocalText =>
        _lastFetchUtc == DateTime.MinValue
            ? ""
            : _lastFetchUtc.ToLocalTime().ToString("MM-dd HH:mm");

    public async Task<IReadOnlyList<CyberNewsItem>> RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        // 非强制：有本地缓存且未到拉取间隔 → 只返回收件箱
        if (!force && _inbox.Count > 0 && !IsFetchDue)
            return CachedUnread;

        // 多源并行拉，避免单个大源拖死后面的源
        using var gate = new SemaphoreSlim(4);
        var tasks = _feeds.Select(async feed =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await FetchOneFeedAsync(feed, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        var chunks = await Task.WhenAll(tasks).ConfigureAwait(false);
        var bag = chunks.SelectMany(x => x).ToList();
        var anyFeedOk = chunks.Any(x => x.Count > 0);

        foreach (var item in bag)
        {
            if (string.IsNullOrWhiteSpace(item.Id)) continue;
            // 已读也留在列表里，否则源上暂时没新文时热点会空
            _inbox[item.Id] = item;
        }

        // 归档源会带回很旧的条目，合并后再按发布时间裁掉
        PruneStaleFromInbox(save: false);
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

    private async Task<List<CyberNewsItem>> FetchOneFeedAsync(CyberFeed feed, CancellationToken ct)
    {
        try
        {
            if (string.Equals(feed.Url, AnquankeKnowledgeApi, StringComparison.OrdinalIgnoreCase))
            {
                var posts = await FetchAnquankeKnowledgeAsync(feed.Label, ct).ConfigureAwait(false);
                return posts.Count == 0 ? new List<CyberNewsItem>() : posts.Take(MaxItemsPerFeed).ToList();
            }

            var xml = await DownloadFeedXmlAsync(feed.Url, ct).ConfigureAwait(false);
            if (xml == null) return new List<CyberNewsItem>();
            return ParseFeed(xml, feed.Label).Take(MaxItemsPerFeed).ToList();
        }
        catch
        {
            return new List<CyberNewsItem>();
        }
    }

    public void MarkRead(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        var changed = false;
        foreach (var key in ReadIdAliases(id))
        {
            if (_readIds.Add(key)) changed = true;
        }
        if (changed) SaveReadIds();
    }

    private bool IsReadId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        foreach (var key in ReadIdAliases(id))
        {
            if (_readIds.Contains(key)) return true;
        }
        return false;
    }

    /// <summary>hackernews 旧 guid 是 0.0.0.0，和新的正式地址都算同一条。</summary>
    private static IEnumerable<string> ReadIdAliases(string id)
    {
        id = id.Trim();
        yield return id;
        var fixedLink = FixBrokenFeedLink(id);
        if (!string.Equals(fixedLink, id, StringComparison.OrdinalIgnoreCase))
            yield return fixedLink;
        if (id.StartsWith("https://hackernews.cc/", StringComparison.OrdinalIgnoreCase))
            yield return "http://0.0.0.0:8080/" + id["https://hackernews.cc/".Length..];
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

    private static HttpClient CreateClient(bool useProxy)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8),
        };
        if (useProxy)
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(SocksProxy);
        }

        var c = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(18) };
        // 部分源对非浏览器 UA 直接 403
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 DeskWidgets/1.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/rss+xml, application/atom+xml, application/xml, text/xml, */*");
        return c;
    }

    /// <summary>先直连。失败或拿到的不是订阅正文时，再走本机 SOCKS5 1080。</summary>
    private static async Task<byte[]?> DownloadBytesAsync(string url, CancellationToken ct)
    {
        var direct = await TryDownloadBytesAsync(Http, url, ct);
        if (direct != null) return direct;
        return await TryDownloadBytesAsync(HttpProxy, url, ct);
    }

    private static async Task<byte[]?> TryDownloadBytesAsync(HttpClient client, string url, CancellationToken ct)
    {
        try
        {
            using var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length == 0 ? null : bytes;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> DownloadFeedXmlAsync(string url, CancellationToken ct)
    {
        var direct = await TryDownloadFeedXmlAsync(Http, url, ct);
        if (direct != null) return direct;
        return await TryDownloadFeedXmlAsync(HttpProxy, url, ct);
    }

    private static async Task<string?> TryDownloadFeedXmlAsync(HttpClient client, string url, CancellationToken ct)
    {
        try
        {
            using var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length < 32) return null;
            var xml = DecodeXmlBytes(bytes, resp.Content.Headers.ContentType?.CharSet);
            if (xml.Contains("<rss", StringComparison.OrdinalIgnoreCase)
                || xml.Contains("<feed", StringComparison.OrdinalIgnoreCase))
                return xml;
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<List<CyberNewsItem>> FetchAnquankeKnowledgeAsync(string label, CancellationToken ct)
    {
        var list = new List<CyberNewsItem>();
        var bytes = await DownloadBytesAsync(AnquankeKnowledgeApi, ct);
        if (bytes == null || bytes.Length == 0) return list;
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var post in data.EnumerateArray())
        {
            if (post.TryGetProperty("category_slug", out var slug)
                && !string.Equals(slug.GetString(), "knowledge", StringComparison.OrdinalIgnoreCase))
                continue;
            var title = post.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(title) || !post.TryGetProperty("id", out var idEl)) continue;
            if (!idEl.TryGetInt32(out var id)) continue;
            var link = $"https://www.anquanke.com/post/id/{id}";
            var desc = post.TryGetProperty("desc", out var descEl) ? descEl.GetString() : null;
            DateTimeOffset? published = null;
            if (post.TryGetProperty("date", out var dateEl))
                published = TryParseDate(dateEl.GetString());
            list.Add(new CyberNewsItem(
                Id: link,
                Title: Decode(title),
                Summary: StripHtml(desc),
                Link: link,
                Source: label,
                Published: published));
        }
        return list;
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
            // HashSet 无序，不能 TakeLast；超限时按当前枚举截断即可
            var keep = _readIds.Count <= 800
                ? _readIds.ToList()
                : _readIds.Take(800).ToList();
            if (keep.Count != _readIds.Count)
            {
                _readIds.Clear();
                foreach (var id in keep) _readIds.Add(id);
            }
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
        // 已读不再踢出收件箱；角标用 UnreadCount 区分
        if (save) SaveInbox();
    }

    /// <summary>清掉太旧的未读；并在收件箱几乎全是旧文时重置拉取时间，下次强制再拉。</summary>
    private void PruneStaleFromInbox(bool save)
    {
        var cutoff = DateTimeOffset.UtcNow - InboxMaxAge;
        var before = _inbox.Count;
        var drop = _inbox
            .Where(kv =>
            {
                var published = kv.Value.Published;
                // 没有时间的条目保留（部分源不带 pubDate）；有时间且过旧的丢掉
                return published != null && published < cutoff;
            })
            .Select(kv => kv.Key)
            .ToList();
        foreach (var id in drop)
            _inbox.Remove(id);
        if (drop.Count > 0 && before > 0 && _inbox.Count * 3 < before)
        {
            // 清掉大半旧文后立刻再拉，避免还要等整点间隔
            _lastFetchUtc = DateTime.MinValue;
            try { if (File.Exists(_lastFetchPath)) File.Delete(_lastFetchPath); }
            catch { /* ignore */ }
        }
        if (save && (drop.Count > 0 || before != _inbox.Count))
            SaveInbox();
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

    private static IEnumerable<CyberNewsItem> ParseFeed(string xml, string source)
    {
        xml = SanitizeXmlEntities(xml);
        var doc = new XmlDocument { XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            CheckCharacters = false,
        });
        doc.Load(reader);

        var nsmgr = new XmlNamespaceManager(doc.NameTable);
        nsmgr.AddNamespace("atom", "http://www.w3.org/2005/Atom");
        var items = doc.SelectNodes("//item");
        if (items is { Count: > 0 })
        {
            foreach (XmlNode node in items)
            {
                var title = Inner(node, "title");
                var link = Inner(node, "link");
                var guid = Inner(node, "guid");
                // 安全客等源只有 guid、没有 link
                if (string.IsNullOrWhiteSpace(link) && LooksLikeHttpUrl(guid))
                    link = guid;
                var descHtml = node.SelectSingleNode("description")?.InnerText;
                var contentHtml = node.SelectSingleNode("*[local-name()='encoded']")?.InnerText;
                var pub = Inner(node, "pubDate");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link)) continue;
                link = FixBrokenFeedLink(link);
                if (!string.IsNullOrWhiteSpace(guid))
                    guid = FixBrokenFeedLink(guid);

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
            link = FixBrokenFeedLink(link);
            if (!string.IsNullOrWhiteSpace(id))
                id = FixBrokenFeedLink(id);
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

    private static List<CyberFeed> LoadFeeds(string path, out bool dirty)
    {
        dirty = false;
        try
        {
            if (!File.Exists(path))
            {
                var fresh = DefaultFeeds.ToList();
                File.WriteAllText(path, JsonSerializer.Serialize(fresh, JsonOpts));
                return fresh;
            }
            var loaded = JsonSerializer.Deserialize<List<CyberFeed>>(File.ReadAllText(path));
            if (loaded == null) return DefaultFeeds.ToList();
            var filtered = loaded
                .Where(f => !string.IsNullOrWhiteSpace(f.Url) && !string.IsNullOrWhiteSpace(f.Label))
                .Where(f => !f.Url.Contains("schneier.com", StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.Url.Contains("lonelysec.com", StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.Url.Contains("csdn.net", StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.Url.Contains("troyhunt.com", StringComparison.OrdinalIgnoreCase))
                .ToList();
            dirty = filtered.Count != loaded.Count;
            return filtered;
        }
        catch
        {
            return DefaultFeeds.ToList();
        }
    }

    private void SaveFeeds()
    {
        try
        {
            File.WriteAllText(_feedsPath, JsonSerializer.Serialize(_feeds, JsonOpts));
        }
        catch { /* ignore */ }
    }

    /// <summary>hackernews.cc 的订阅把站点地址写成了 0.0.0.0:8080。</summary>
    private static string FixBrokenFeedLink(string link) =>
        link.Replace("http://0.0.0.0:8080", "https://hackernews.cc", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeHttpUrl(string? s) =>
        !string.IsNullOrWhiteSpace(s)
        && (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// <summary>部分源会写出 &mdash、&nbsp 这类不完整实体，XmlDocument 直接崩。</summary>
    private static string SanitizeXmlEntities(string xml) =>
        Regex.Replace(
            xml ?? "",
            @"&(?!(amp|lt|gt|quot|apos|#\d+|#x[0-9a-fA-F]+);)",
            "&amp;",
            RegexOptions.IgnoreCase);

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
