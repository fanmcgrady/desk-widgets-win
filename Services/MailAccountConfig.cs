using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeskWidgets.Services;

/// <summary>IMAP/SMTP 邮箱配置。默认按川大 mail.scu.edu.cn。</summary>
public sealed class MailAccountConfig
{
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ImapHost { get; set; } = "mail.scu.edu.cn";
    public int ImapPort { get; set; } = 993;
    public bool ImapSsl { get; set; } = true;
    public string SmtpHost { get; set; } = "mail.scu.edu.cn";
    public int SmtpPort { get; set; } = 465;
    public bool SmtpSsl { get; set; } = true;
    /// <summary>DPAPI 保护后的 Base64，不存明文密码。</summary>
    public string PasswordProtected { get; set; } = "";

    public static string ConfigPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeskWidgets",
            "mail-account.json");

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Email)
        && !string.IsNullOrWhiteSpace(PasswordProtected)
        && !string.IsNullOrWhiteSpace(ImapHost)
        && !string.IsNullOrWhiteSpace(SmtpHost);

    public static MailAccountConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return CreateDefault();
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<MailAccountConfig>(json) ?? CreateDefault();
        }
        catch
        {
            return CreateDefault();
        }
    }

    public static MailAccountConfig CreateDefault() => new();

    public void Save()
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }

    public void SetPassword(string plain)
    {
        if (string.IsNullOrEmpty(plain))
        {
            PasswordProtected = "";
            return;
        }
        var bytes = Encoding.UTF8.GetBytes(plain);
        var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        PasswordProtected = Convert.ToBase64String(protectedBytes);
    }

    public string GetPassword()
    {
        if (string.IsNullOrWhiteSpace(PasswordProtected)) return "";
        try
        {
            var protectedBytes = Convert.FromBase64String(PasswordProtected);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }
}
