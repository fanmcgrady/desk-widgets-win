using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopBox.Services;

public sealed class AppConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string DropMode { get; set; } = "move"; // move | copy
    public LayoutConfig Layout { get; set; } = new();
    public AutoSortConfig AutoSort { get; set; } = new();
    public List<ZoneConfig> Zones { get; set; } = [];

    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopBox");

    public static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    /// <summary>旧版多分区根目录（桌面「桌面分区」），自动整理时仍排除该路径。</summary>
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "桌面分区");

    /// <summary>内存区（TMP）默认映射到 X 盘根目录。</summary>
    public static string DefaultMemoryRoot => @"X:\";

    public static AppConfig LoadOrCreate()
    {
        Directory.CreateDirectory(ConfigDirectory);
        if (File.Exists(ConfigPath))
        {
            var json = File.ReadAllText(ConfigPath);
            var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (loaded is not null && loaded.Zones.Count > 0)
            {
                loaded.Normalize();
                loaded.Save();
                return loaded;
            }
        }

        var created = CreateDefault();
        created.Save();
        return created;
    }

    public static AppConfig CreateDefault()
    {
        return new AppConfig
        {
            DropMode = "move",
            Layout = new LayoutConfig(),
            AutoSort = new AutoSortConfig(),
            Zones =
            [
                new ZoneConfig { Id = "todo", Name = "内存", English = "TMP", Folder = DefaultMemoryRoot }
            ]
        };
    }

    public void Normalize()
    {
        Layout ??= new LayoutConfig();
        AutoSort ??= new AutoSortConfig();
        if (string.IsNullOrWhiteSpace(AutoSort.TargetZoneId))
            AutoSort.TargetZoneId = "todo";

        // 只保留内存区，少挂 IShellView
        Zones.RemoveAll(z => z.Id is not "todo");
        var memory = Zones.FirstOrDefault(z => z.Id == "todo");
        if (memory is null)
        {
            Zones = CreateDefault().Zones;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(memory.Name))
                memory.Name = "内存";
            memory.English = "TMP";
            if (string.IsNullOrWhiteSpace(memory.Folder))
                memory.Folder = DefaultMemoryRoot;
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDirectory);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public void EnsureFolders()
    {
        foreach (var zone in Zones)
        {
            if (string.IsNullOrWhiteSpace(zone.Folder))
                continue;
            Directory.CreateDirectory(zone.Folder);
        }
    }

    public ZoneConfig? FindZone(string id) => Zones.FirstOrDefault(z => z.Id == id);
}

public sealed class AutoSortConfig
{
    /// <summary>是否启用：桌面新文件自动移入目标区（默认关，按需开以省资源）。</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>目标分区 id，默认内存区（X:\）。</summary>
    public string TargetZoneId { get; set; } = "todo";
}

public sealed class LayoutConfig
{
    /// <summary>已废弃：布局固定贴满屏幕外沿，读入后忽略。</summary>
    public double MarginX { get; set; }

    /// <summary>已废弃：布局固定贴满屏幕外沿，读入后忽略。</summary>
    public double MarginY { get; set; }

    /// <summary>相对屏幕宽的竖向白槽半宽。</summary>
    public double GutterX { get; set; } = 0.026;

    /// <summary>相对屏幕高的横向白槽半高（偏大以避开中心 Logo）。</summary>
    public double GutterY { get; set; } = 0.078;
}

public sealed class ZoneConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string English { get; set; } = "";
    public string Folder { get; set; } = "";
}
