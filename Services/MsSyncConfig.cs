using System.IO;
using System.Text.Json;

namespace DeskWidgets.Services;

public sealed class MsSyncConfig
{
    public string ClientId { get; set; } = "";
    public string ListId { get; set; } = "";
    public string ListName { get; set; } = "侧栏待办";

    private static string PathFile =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeskWidgets",
            "ms-sync.json");

    public static MsSyncConfig Load()
    {
        try
        {
            if (!File.Exists(PathFile)) return new MsSyncConfig();
            var json = File.ReadAllText(PathFile);
            return JsonSerializer.Deserialize<MsSyncConfig>(json) ?? new MsSyncConfig();
        }
        catch
        {
            return new MsSyncConfig();
        }
    }

    public void Save()
    {
        var dir = System.IO.Path.GetDirectoryName(PathFile)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(PathFile, json);
    }

    public static string ConfigPath => PathFile;

    public static string SetupHelpText =>
        """
        在 Azure 门户注册应用后，把「应用程序(客户端) ID」写入本文件的 ClientId 字段。

        步骤：
        1. 打开 https://portal.azure.com/ → Microsoft Entra ID → 应用注册 → 新注册
        2. 名称随意，账户类型选「仅个人 Microsoft 帐户」
        3. 重定向 URI：公共客户端/本机 → http://localhost
        4. 注册后复制「应用程序(客户端) ID」填到 ClientId
        5. 认证 → 允许公共客户端流 = 是
        6. API 权限 → 添加 Microsoft Graph 委托权限 Tasks.ReadWrite、User.Read → 代表我同意
        7. 回到侧栏托盘菜单，点「登录并同步」
        """;
}
