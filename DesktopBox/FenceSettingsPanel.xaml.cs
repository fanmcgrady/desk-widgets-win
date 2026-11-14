using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DesktopBox.Services;
using WinForms = System.Windows.Forms;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace DesktopBox;

public partial class FenceSettingsPanel : System.Windows.Controls.UserControl
{
    private readonly List<ZoneRow> _rows = [];
    private bool _loading;

    public FenceSettingsPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => ReloadFromDisk();
    }

    public void ReloadFromDisk()
    {
        _loading = true;
        try
        {
            var cfg = AppConfig.LoadOrCreate();
            ChkAutoSort.IsChecked = cfg.AutoSort?.Enabled ?? true;
            ChkShowFences.IsChecked = !DesktopBoxRuntime.IsPaused;
            RadioMove.IsChecked = !string.Equals(cfg.DropMode, "copy", StringComparison.OrdinalIgnoreCase);
            RadioCopy.IsChecked = string.Equals(cfg.DropMode, "copy", StringComparison.OrdinalIgnoreCase);

            ZoneList.Children.Clear();
            _rows.Clear();
            foreach (var zone in cfg.Zones)
            {
                var row = BuildZoneRow(zone);
                _rows.Add(row);
                ZoneList.Children.Add(row.Root);
            }

            StatusText.Text = DesktopBoxRuntime.IsRunning
                ? "围栏已随工作区启动"
                : "围栏未启动";
        }
        finally
        {
            _loading = false;
        }
    }

    private ZoneRow BuildZoneRow(ZoneConfig zone)
    {
        var name = string.IsNullOrWhiteSpace(zone.Name) ? zone.Id : zone.Name;
        var title = new TextBlock
        {
            Text = name,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Width = 48,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var pathBox = new WpfTextBox
        {
            Text = zone.Folder,
            FontSize = 11,
            Height = 24,
            Padding = new Thickness(6, 1, 6, 1),
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent
        };

        var pathBorder = new Border
        {
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x08, 0, 0, 0)),
            CornerRadius = new CornerRadius(5),
            Child = pathBox,
            Margin = new Thickness(0, 0, 2, 0)
        };

        var browse = new System.Windows.Controls.Button
        {
            Content = "更换",
            Width = 36,
            Height = 24,
            Padding = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "选择文件夹"
        };
        browse.Click += (_, _) =>
        {
            using var dlg = new WinForms.FolderBrowserDialog
            {
                Description = $"选择「{zone.Name}」文件夹",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(pathBox.Text) ? pathBox.Text : ""
            };
            if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                pathBox.Text = dlg.SelectedPath;
        };

        var open = new System.Windows.Controls.Button
        {
            Content = "打开",
            Width = 36,
            Height = 24,
            Padding = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "打开文件夹",
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4))
        };
        open.Click += (_, _) =>
        {
            var folder = pathBox.Text.Trim();
            if (folder.Length == 0) return;
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        };

        var root = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(browse, Dock.Right);
        DockPanel.SetDock(open, Dock.Right);
        DockPanel.SetDock(title, Dock.Left);
        root.Children.Add(browse);
        root.Children.Add(open);
        root.Children.Add(title);
        root.Children.Add(pathBorder);

        return new ZoneRow(zone.Id, zone.Name, zone.English, pathBox, root);
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var cfg = AppConfig.LoadOrCreate();
        cfg.DropMode = RadioCopy.IsChecked == true ? "copy" : "move";
        if (cfg.AutoSort is null)
            cfg.AutoSort = new AutoSortConfig();
        cfg.AutoSort.Enabled = ChkAutoSort.IsChecked == true;

        foreach (var row in _rows)
        {
            var zone = cfg.Zones.FirstOrDefault(z => z.Id == row.Id);
            if (zone is null)
            {
                zone = new ZoneConfig
                {
                    Id = row.Id,
                    Name = row.Name,
                    English = row.English
                };
                cfg.Zones.Add(zone);
            }

            zone.Folder = row.PathBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(zone.Name))
                zone.Name = row.Name;
            if (string.IsNullOrWhiteSpace(zone.English))
                zone.English = row.English;
        }

        try
        {
            DesktopBoxRuntime.SaveAndReload(cfg);
            DesktopBoxRuntime.SetPaused(ChkShowFences.IsChecked != true);
            StatusText.Text = "已保存并重载";
            ReloadFromDisk();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void BtnOpenConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(AppConfig.ConfigPath))
                AppConfig.LoadOrCreate().Save();

            Process.Start(new ProcessStartInfo
            {
                FileName = AppConfig.ConfigPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        DesktopBoxRuntime.PreviewZones();
        StatusText.Text = "已刷新";
    }

    private void ChkAutoSort_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        DesktopBoxRuntime.SetAutoSortEnabled(ChkAutoSort.IsChecked == true);
        StatusText.Text = ChkAutoSort.IsChecked == true ? "已开启自动归类" : "已关闭自动归类";
    }

    private void ChkShowFences_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        DesktopBoxRuntime.SetPaused(ChkShowFences.IsChecked != true);
        StatusText.Text = ChkShowFences.IsChecked == true ? "围栏已显示" : "围栏已隐藏";
    }

    private sealed class ZoneRow
    {
        public ZoneRow(string id, string name, string english, WpfTextBox pathBox, FrameworkElement root)
        {
            Id = id;
            Name = name;
            English = english;
            PathBox = pathBox;
            Root = root;
        }

        public string Id { get; }
        public string Name { get; }
        public string English { get; }
        public WpfTextBox PathBox { get; }
        public FrameworkElement Root { get; }
    }
}
