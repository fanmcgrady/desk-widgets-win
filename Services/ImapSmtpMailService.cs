using System.IO;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace DeskWidgets.Services;

public sealed class MailMessageSummary
{
    public string Id { get; init; } = "";
    public UniqueId Uid { get; init; }
    public string Subject { get; init; } = "";
    public string From { get; init; } = "";
    public string Preview { get; init; } = "";
    public DateTimeOffset? Received { get; init; }
    public bool IsRead { get; init; }
}

public sealed class MailAttachmentInfo
{
    public int Index { get; init; }
    public string FileName { get; init; } = "附件";
    public long Size { get; init; }
    public string ContentType { get; init; } = "";
}

public sealed class MailMessageDetail
{
    public string Id { get; init; } = "";
    public UniqueId Uid { get; init; }
    public string Subject { get; init; } = "";
    public string From { get; init; } = "";
    public string FromAddress { get; init; } = "";
    public string To { get; init; } = "";
    public string BodyText { get; init; } = "";
    public DateTimeOffset? Received { get; init; }
    public IReadOnlyList<MailAttachmentInfo> Attachments { get; init; } = Array.Empty<MailAttachmentInfo>();
}

public sealed class ImapSmtpMailService
{
    private MailAccountConfig _cfg;

    public ImapSmtpMailService() => _cfg = MailAccountConfig.Load();

    public MailAccountConfig Config => _cfg;
    public void Reload() => _cfg = MailAccountConfig.Load();

    public async Task<IReadOnlyList<MailMessageSummary>> ListInboxAsync(int top = 40, CancellationToken ct = default)
    {
        EnsureConfigured();
        using var client = await ConnectImapAsync(ct).ConfigureAwait(false);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);

        // 只拉信封 + 预览，避免每封都 GetMessage（川大邮箱会很慢）
        var uids = await inbox.SearchAsync(SearchQuery.All, ct).ConfigureAwait(false);
        if (uids.Count == 0)
        {
            await client.DisconnectAsync(true, ct).ConfigureAwait(false);
            return Array.Empty<MailMessageSummary>();
        }

        var take = uids.Count <= top
            ? uids.Reverse().ToList()
            : uids.Skip(uids.Count - top).Reverse().ToList();

        // 只拉信封，列表秒开；点开后再下正文
        var fetched = await inbox.FetchAsync(
            take,
            MessageSummaryItems.UniqueId
            | MessageSummaryItems.Envelope
            | MessageSummaryItems.Flags
            | MessageSummaryItems.InternalDate,
            ct).ConfigureAwait(false);

        var byUid = fetched.ToDictionary(x => x.UniqueId);
        var summaries = new List<MailMessageSummary>(take.Count);
        foreach (var uid in take)
        {
            if (!byUid.TryGetValue(uid, out var s)) continue;
            var subject = s.Envelope?.Subject;
            summaries.Add(new MailMessageSummary
            {
                Id = uid.ToString(),
                Uid = uid,
                Subject = string.IsNullOrWhiteSpace(subject) ? "(无主题)" : subject.Trim(),
                From = FormatMailbox(s.Envelope?.From?.Mailboxes.FirstOrDefault()),
                Preview = "",
                Received = s.InternalDate ?? s.Envelope?.Date,
                IsRead = s.Flags?.HasFlag(MessageFlags.Seen) == true,
            });
        }

        await client.DisconnectAsync(true, ct).ConfigureAwait(false);
        return summaries;
    }

    public async Task<MailMessageDetail> GetMessageAsync(UniqueId uid, CancellationToken ct = default)
    {
        EnsureConfigured();
        using var client = await ConnectImapAsync(ct).ConfigureAwait(false);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, ct).ConfigureAwait(false);
        var msg = await inbox.GetMessageAsync(uid, ct).ConfigureAwait(false);
        try { await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true, ct).ConfigureAwait(false); }
        catch { /* 部分服务器标记已读会失败，忽略 */ }

        var fromMb = msg.From.Mailboxes.FirstOrDefault();
        // 纯文本优先；美团发票等信只有 HTML，需剥离标签并压空白
        var body = CleanBodyText(msg.TextBody);
        if (string.IsNullOrWhiteSpace(body))
            body = StripHtml(msg.HtmlBody ?? "");

        var attachments = ListAttachments(msg);
        var detail = new MailMessageDetail
        {
            Id = uid.ToString(),
            Uid = uid,
            Subject = string.IsNullOrWhiteSpace(msg.Subject) ? "(无主题)" : msg.Subject.Trim(),
            From = FormatMailbox(fromMb),
            FromAddress = fromMb?.Address ?? "",
            To = string.Join(", ", msg.To.Mailboxes.Select(FormatMailbox)),
            BodyText = body?.Trim() ?? "",
            Received = msg.Date,
            Attachments = attachments,
        };

        await client.DisconnectAsync(true, ct).ConfigureAwait(false);
        return detail;
    }

    public async Task SaveAttachmentAsync(UniqueId uid, int attachmentIndex, string savePath, CancellationToken ct = default)
    {
        EnsureConfigured();
        using var client = await ConnectImapAsync(ct).ConfigureAwait(false);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);
        var msg = await inbox.GetMessageAsync(uid, ct).ConfigureAwait(false);
        var parts = CollectAttachmentParts(msg);
        if (attachmentIndex < 0 || attachmentIndex >= parts.Count)
            throw new InvalidOperationException("附件不存在或已失效。");

        var part = parts[attachmentIndex];
        await using (var fs = File.Create(savePath))
        {
            if (part.Content == null)
                throw new InvalidOperationException("附件内容为空。");
            await part.Content.DecodeToAsync(fs, ct).ConfigureAwait(false);
        }

        await client.DisconnectAsync(true, ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<MailAttachmentInfo> ListAttachments(MimeMessage msg)
    {
        var parts = CollectAttachmentParts(msg);
        var list = new List<MailAttachmentInfo>(parts.Count);
        for (var i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            var name = p.FileName;
            if (string.IsNullOrWhiteSpace(name))
                name = $"attachment-{i + 1}";
            long size = 0;
            try
            {
                // 优先 ContentDisposition 声明的大小
                if (p.ContentDisposition?.Size != null)
                    size = p.ContentDisposition.Size.Value;
            }
            catch { /* ignore */ }
            list.Add(new MailAttachmentInfo
            {
                Index = i,
                FileName = name,
                Size = size,
                ContentType = p.ContentType?.MimeType ?? "",
            });
        }
        return list;
    }

    private static List<MimePart> CollectAttachmentParts(MimeMessage msg)
    {
        var parts = new List<MimePart>();
        var seen = new HashSet<MimePart>();

        void TryAdd(MimePart? part)
        {
            if (part == null || !seen.Add(part)) return;
            var isTextBody = part.ContentType.IsMimeType("text", "plain")
                             || part.ContentType.IsMimeType("text", "html");
            if (isTextBody && !part.IsAttachment) return;

            if (part.IsAttachment
                || !string.IsNullOrWhiteSpace(part.FileName)
                || part.ContentDisposition?.Disposition == ContentDisposition.Attachment
                || (!isTextBody && part.ContentType.MediaType.Equals("application", StringComparison.OrdinalIgnoreCase)))
            {
                parts.Add(part);
            }
        }

        foreach (var part in msg.BodyParts.OfType<MimePart>())
            TryAdd(part);

        // MimeKit 的 Attachments 覆盖部分仅声明在该集合里的附件
        foreach (var ent in msg.Attachments.OfType<MimePart>())
            TryAdd(ent);

        return parts;
    }

    public async Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        EnsureConfigured();
        var addresses = SplitAddresses(to);
        if (addresses.Count == 0)
            throw new InvalidOperationException("请填写收件人邮箱。");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(
            string.IsNullOrWhiteSpace(_cfg.DisplayName) ? _cfg.Email : _cfg.DisplayName,
            _cfg.Email.Trim()));
        foreach (var a in addresses)
            message.To.Add(MailboxAddress.Parse(a));
        message.Subject = subject ?? "";
        message.Body = new TextPart("plain")
        {
            Text = body ?? "",
        };

        using var smtp = new SmtpClient();
        smtp.ServerCertificateValidationCallback = (_, _, _, _) => true;
        var secure = _cfg.SmtpSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
        // 465 用 SslOnConnect；587 常用 StartTls
        if (_cfg.SmtpPort == 587) secure = SecureSocketOptions.StartTls;
        await smtp.ConnectAsync(_cfg.SmtpHost.Trim(), _cfg.SmtpPort, secure, ct).ConfigureAwait(false);
        await smtp.AuthenticateAsync(_cfg.Email.Trim(), _cfg.GetPassword(), ct).ConfigureAwait(false);
        await smtp.SendAsync(message, ct).ConfigureAwait(false);
        await smtp.DisconnectAsync(true, ct).ConfigureAwait(false);
    }

    /// <summary>未读封数（IMAP SEARCH UNSEEN），供角标轮询。</summary>
    public async Task<int> CountUnreadAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        using var client = await ConnectImapAsync(ct).ConfigureAwait(false);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct).ConfigureAwait(false);
        var unseen = await inbox.SearchAsync(SearchQuery.NotSeen, ct).ConfigureAwait(false);
        await client.DisconnectAsync(true, ct).ConfigureAwait(false);
        return unseen.Count;
    }

    public async Task TestAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        using var imap = await ConnectImapAsync(ct).ConfigureAwait(false);
        await imap.DisconnectAsync(true, ct).ConfigureAwait(false);

        using var smtp = new SmtpClient();
        smtp.ServerCertificateValidationCallback = (_, _, _, _) => true;
        var secure = _cfg.SmtpSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
        if (_cfg.SmtpPort == 587) secure = SecureSocketOptions.StartTls;
        await smtp.ConnectAsync(_cfg.SmtpHost.Trim(), _cfg.SmtpPort, secure, ct).ConfigureAwait(false);
        await smtp.AuthenticateAsync(_cfg.Email.Trim(), _cfg.GetPassword(), ct).ConfigureAwait(false);
        await smtp.DisconnectAsync(true, ct).ConfigureAwait(false);
    }

    private async Task<ImapClient> ConnectImapAsync(CancellationToken ct)
    {
        var client = new ImapClient();
        client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        var secure = _cfg.ImapSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
        if (_cfg.ImapPort == 143) secure = SecureSocketOptions.StartTlsWhenAvailable;
        await client.ConnectAsync(_cfg.ImapHost.Trim(), _cfg.ImapPort, secure, ct).ConfigureAwait(false);
        await client.AuthenticateAsync(_cfg.Email.Trim(), _cfg.GetPassword(), ct).ConfigureAwait(false);
        return client;
    }

    private void EnsureConfigured()
    {
        if (!_cfg.IsConfigured)
            throw new InvalidOperationException("请先配置邮箱账号（川大默认为 mail.scu.edu.cn）。");
        if (string.IsNullOrEmpty(_cfg.GetPassword()))
            throw new InvalidOperationException("邮箱密码无效，请重新在设置里填写。");
    }

    private static List<string> SplitAddresses(string to) =>
        (to ?? "")
            .Split([';', ',', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(a => a.Contains('@'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string FormatMailbox(MailboxAddress? mb)
    {
        if (mb == null) return "";
        if (!string.IsNullOrWhiteSpace(mb.Name)) return mb.Name.Trim();
        return mb.Address ?? "";
    }

    private static string CleanBodyText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        // 去掉控制符（保留换行/制表），避免个别信把二进制渣滓当正文
        var t = Regex.Replace(text, @"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]", "");
        t = Regex.Replace(t, "[ \\t]+", " ");
        t = Regex.Replace(t, @"[ \t]*\r?\n[ \t]*", "\n");
        t = Regex.Replace(t, @"\n{3,}", "\n\n");
        return t.Trim();
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var t = Regex.Replace(html, "(?is)<script.*?>.*?</script>", " ");
        t = Regex.Replace(t, "(?is)<style.*?>.*?</style>", " ");
        t = Regex.Replace(t, "(?is)<head.*?>.*?</head>", " ");
        t = Regex.Replace(t, "(?i)<br\\s*/?>", "\n");
        t = Regex.Replace(t, "(?i)</p>", "\n");
        t = Regex.Replace(t, "(?i)</div>", "\n");
        t = Regex.Replace(t, "(?i)</tr>", "\n");
        t = Regex.Replace(t, "(?i)</h[1-6]>", "\n");
        t = Regex.Replace(t, "(?i)<hr[^>]*>", "\n");
        t = Regex.Replace(t, "<[^>]+>", " ");
        t = System.Net.WebUtility.HtmlDecode(t);
        return CleanBodyText(t);
    }
}
