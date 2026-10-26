using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MVPWorkshop.App.Viewers;

public static class StartPage
{
    public static FrameworkElement Build(MainWindow w, Settings settings)
    {
        var sp = new StackPanel { Margin = new Thickness(48, 40, 48, 40), MaxWidth = 880, HorizontalAlignment = HorizontalAlignment.Left };

        var logo = new Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/MvpWorkshop.png")),
            Width = 420, Height = 168, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "MVP Workshop"
        };
        sp.Children.Add(logo);
        sp.Children.Add(new TextBlock { Text = "MVP-Workshop " + AppInfo.Version, Margin = new Thickness(0, 16, 0, 0), FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("Text") });
        sp.Children.Add(new TextBlock { Text = "MVP 07 NCAA Baseball (PS2) and MVP 06 NCAA Baseball (Xbox) · asset explorer, editor and format research workbench", Foreground = Ui.Res("Muted"), FontSize = 13 });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 28, 0, 0) };
        actions.Children.Add(B.Button("Open Game Folder", "", (_, _) => w.PromptOpenFolder(), "Primary"));
        sp.Children.Add(actions);

        if (settings.Recent.Count > 0)
        {
            sp.Children.Add(Section("RECENT"));
            foreach (var r in settings.Recent)
            {
                var path = r;
                var b = B.Button(path, "", (_, _) => { if (Directory.Exists(path) || File.Exists(path)) w.LoadGame(path); else Log.Warn($"{path} no longer exists."); });
                b.HorizontalAlignment = HorizontalAlignment.Left;
                sp.Children.Add(b);
            }
        }

        sp.Children.Add(Section("WHAT IT READS"));
        var grid = new UniformGridish(3);
        foreach (var (g, title, body, brush) in new[]
        {
            ("", "BIGF archives", "Nested archives and RefPack (10 FB) compression are unpacked transparently.", "Accent"),
            ("", "SSH textures", "PS2 PAL4/PAL8/RGBA and EA GST textures; Xbox .xsh DXT1/DXT3, PAL8 and BGRA textures with Morton un-swizzling.", "Good"),
            ("", "EA audio", "SCHl streams and BNKl banks — EA-XA, PS-ADPCM and MicroTalk speech decode and playback.", "Info"),
            ("", "ELF objects", "Executables, IRX modules and .ord/.o model objects — sections and symbols.", "Bad"),
            ("", "Data tables", "CSV/TSV databases, text configs, stadium and audio data.", "Muted"),
            ("", "UI and menus", "FEL menu composition, native bitmap fonts, included layers, button states and animation scrubbing.", "Accent"),
            ("", "Format survey", "Walks every file in every archive and reports what parses and what doesn't.", "Good"),
        })
            grid.Add(Tile(g, title, body, brush));
        sp.Children.Add(grid.Panel);

        sp.Children.Add(Section("TIPS"));
        foreach (var tip in new[]
        {
            "Double-click a file in the Data Explorer to open it. Archives expand like folders.",
            "Categories groups UI, Teams (uniforms, stadiums, logos, rosters), Players, Audio and more. Switch to Files and archives for original paths.",
            "Open UI → Menus and layouts for visual menus. Preview edits affect only the canvas; Export PNG saves what you see.",
            "Ctrl+F searches every file name across all archives; terms are AND-ed (e.g. “ghead face”).",
            "Every document has Hex (with a data inspector), Strings and Chunks tabs for format research.",
            "Nothing is ever written to the game folder. Exports go wherever you choose.",
        })
            sp.Children.Add(new TextBlock { Text = "•  " + tip, Foreground = Ui.Res("Muted"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });

        return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Ui.Res("WindowBg") };
    }

    private static TextBlock Section(string s) => new() { Text = s, Style = Ui.Style("PanelHeader"), Margin = new Thickness(0, 30, 0, 10) };

    private static Border Tile(string glyph, string title, string body, string brush)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = glyph, Style = Ui.Style("Glyph"), FontSize = 20, Foreground = Ui.Res(brush) });
        sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4), Foreground = Ui.Res("Text") });
        sp.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("Muted"), FontSize = 11.5 });
        return new Border { Child = sp, Background = Ui.Res("PanelBg"), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(16), Margin = new Thickness(0, 0, 10, 10) };
    }

    private sealed class UniformGridish(int cols)
    {
        public System.Windows.Controls.Primitives.UniformGrid Panel { get; } = new() { Columns = cols };
        public void Add(UIElement e) => Panel.Children.Add(e);
    }
}
