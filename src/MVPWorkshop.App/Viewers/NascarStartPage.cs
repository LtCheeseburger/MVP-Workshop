using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MVPWorkshop.App.Viewers;

public static class NascarStartPage
{
    public static FrameworkElement Build(MainWindow window, Settings settings)
    {
        var panel = new StackPanel { Margin = new Thickness(46), MaxWidth = 1040, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(new TextBlock { Text = "NASCAR", FontSize = 64, FontWeight = FontWeights.Black, FontStyle = FontStyles.Italic, Foreground = Ui.Res("Accent") });
        panel.Children.Add(new TextBlock { Text = "WORKSHOP", FontSize = 32, FontWeight = FontWeights.Bold, Foreground = Ui.Res("Text") });
        panel.Children.Add(new TextBlock { Text = $"NASCAR 06: Total Team Control · Xbox · v{AppInfo.Version}", FontSize = 15, Foreground = Ui.Res("Muted"), Margin = new Thickness(0, 14, 0, 26) });
        panel.Children.Add(new TextBlock { Text = "Texture and database editing, archive exports, and partial model previews. Full format support is still in development.", TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("Muted"), Margin = new Thickness(0, 0, 0, 20) });
        var open = B.Button("Open game / DATA folder…", "", (_, _) => window.PromptOpenDataFolder(), "Primary");
        open.HorizontalAlignment = HorizontalAlignment.Left; panel.Children.Add(open);
        foreach (var recent in settings.Recent.Take(5))
        {
            var path = recent;
            panel.Children.Add(B.Button(path, "", (_, _) => { if (Directory.Exists(path)) window.LoadGame(path); }));
        }
        panel.Children.Add(new TextBlock { Text = "ASSET WORKBENCH", Style = Ui.Style("PanelHeader"), Margin = new Thickness(0, 32, 0, 12) });
        foreach (var row in new[]
        {
            new { Kind = "TERF / DAT", Support = "Browse entries and nested archives; decode compression 0 and 5; extract and export edited archives" },
            new { Kind = "MMAP textures", Support = "PAL8, BGRA32 and linear luminance previews; named textures and mipmaps; PNG export and image replacement" },
            new { Kind = "EA audio", Support = "SCHl / BNKl / ABKC viewers and existing codec playback; unsupported codecs report their limits" },
            new { Kind = "TMdl models", Support = "Textured cars, characters and static track scenes; export visible parts and instances to OBJ with material files and PNG textures" },
            new { Kind = "EA DB databases", Support = "Tables and original field IDs; packed numbers, floats and text; field editing, CSV export and checksum updates" },
            new { Kind = "QKL quick-load caches", Support = "Source/cache reference inspection; matching archive headers and cached entries synchronized on export" },
            new { Kind = "MAD movies", Support = "Playback and MP4 export with FFmpeg installed; animation and native frontend scripts remain in research" },
        })
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var kind = new TextBlock { Text = row.Kind, Foreground = Ui.Res("Text"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 16, 0) };
            var support = new TextBlock { Text = row.Support, Foreground = Ui.Res("Muted"), TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(support, 1); grid.Children.Add(kind); grid.Children.Add(support); panel.Children.Add(grid);
        }
        panel.Children.Add(new TextBlock { Text = "Save edits into a project, then Export Mod to a separate folder. Original game files stay untouched. Complete format support is still in development.", TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Ui.Res("Muted"), Margin = new Thickness(0, 18, 0, 0) });
        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Ui.Res("WindowBg") };
    }
}
