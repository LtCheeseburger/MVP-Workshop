using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MVPWorkshop.App;

/// <summary>
/// Developer aid: <c>MVP-Workshop.exe --snapshot out.png [--open vpath]... [--tab N] [--size WxH]</c>
/// opens the given assets, renders the window to a PNG and exits. Used to check layouts without a human at the screen.
/// </summary>
public static class Snapshot
{
    public static void RunIfRequested(MainWindow w)
    {
        var args = Environment.GetCommandLineArgs();
        var i = Array.IndexOf(args, "--snapshot");
        if (i < 0 || i + 1 >= args.Length) return;
        var outPath = args[i + 1];

        var size = Array.IndexOf(args, "--size");
        if (size > 0 && args[size + 1].Split('x') is [var sw, var sh]) { w.Width = double.Parse(sw); w.Height = double.Parse(sh); }

        for (var k = 0; k < args.Length - 1; k++)
        {
            if (args[k] == "--open" && w.FindNode(args[k + 1]) is { } n)
            {
                w.Reveal(n);
                if (!n.IsFolder) w.OpenAsset(n);
            }
            if (args[k] == "--reveal" && w.FindNode(args[k + 1]) is { } r) w.Reveal(r);
        }
        if (args.Contains("--demo-edits")) DemoEdits(w);
        var tab = Array.IndexOf(args, "--tab");
        if (args.Contains("--categories")) w.BrowseMode.SelectedIndex = 0;

        var wait = Array.IndexOf(args, "--wait") is var wi and > 0 && int.TryParse(args[wi + 1], out var ms) ? ms : 2500;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(wait) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var teamIndex = Array.IndexOf(args, "--team");
            if (teamIndex >= 0 && teamIndex + 1 < args.Length)
            {
                var teams = w.Tree.Items.OfType<TreeItem>().FirstOrDefault(i => i.Name == "Teams");
                var item = teams?.Children.FirstOrDefault(i => i.Team?.Team.Abbreviation.Equals(args[teamIndex + 1], StringComparison.OrdinalIgnoreCase) == true);
                if (item?.Team is { } profile) { item.IsExpanded = true; item.IsSelected = true; w.OpenTeam(profile); }
            }
            var aptIndex = Array.IndexOf(args, "--apt-frame");
            if (aptIndex >= 0 && aptIndex + 1 < args.Length && int.TryParse(args[aptIndex + 1], out var aptFrame) && FindChild<MVPWorkshop.App.Viewers.AptSceneView>(w) is { } apt)
            {
                var actionIndex = Array.IndexOf(args, "--apt-action");
                apt.Configure(aptFrame, actionIndex >= 0 && actionIndex + 1 < args.Length ? args[actionIndex + 1] : null, args.Contains("--apt-smooth"), args.Contains("--apt-bounds"), !args.Contains("--apt-noscript"), !args.Contains("--apt-nobackdrop"));
            }
            if (args.Contains("--categories"))
                foreach (var item in w.Tree.Items.OfType<TreeItem>())
                    if (item.Name is "UI" or "Teams") item.IsExpanded = true;
            if ((args.Contains("--menu-animation") || args.Contains("--menu-time") || args.Contains("--menu-show")) && FindChild<MVPWorkshop.App.Viewers.FelView>(w) is { } menu)
            {
                var ai = Array.IndexOf(args, "--menu-animation");
                var timeIndex = Array.IndexOf(args, "--menu-time");
                var shows = args.Select((value, at) => (value, at)).Where(v => v.value == "--menu-show" && v.at + 1 < args.Length).Select(v => args[v.at + 1]);
                menu.ConfigurePreview(ai >= 0 && ai + 1 < args.Length ? args[ai + 1] : null,
                    timeIndex >= 0 && timeIndex + 1 < args.Length && double.TryParse(args[timeIndex + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : 0, shows);
            }
            if (tab > 0 && int.TryParse(args[tab + 1], out var ti)) SelectInnerTab(w, ti);
            var t2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            t2.Tick += (_, _) =>
            {
                t2.Stop();
                var dpi = VisualTreeHelper.GetDpi(w);
                var rtb = new RenderTargetBitmap((int)(w.ActualWidth * dpi.DpiScaleX), (int)(w.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                rtb.Render(w);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(outPath)) enc.Save(fs);
                if (FindChild<MVPWorkshop.App.Viewers.AptSceneView>(w) is { } aptView) { File.WriteAllText(outPath + ".limitations.txt", aptView.PreviewLimitations); if (Array.IndexOf(args, "--apt-stage") is var si and >= 0 && si + 1 < args.Length) aptView.SaveStage(args[si + 1]); if (Array.IndexOf(args, "--apt-pick") is var pi and >= 0 && pi + 1 < args.Length && args[pi + 1].Split(',') is [var px, var py]) File.WriteAllText(outPath + ".pick.txt", aptView.ElementsAt(double.Parse(px), double.Parse(py))); if (args.Contains("--apt-validate-fonts")) File.WriteAllText(outPath + ".fonts.txt", aptView.ValidateFonts()); }
                Application.Current.Shutdown();
            };
            t2.Start();
        };
        timer.Start();
    }

    /// <summary>Applies a few representative edits (saved, saved, preview-only) so the Changes view and edit markers can be checked in a snapshot.</summary>
    private static void DemoEdits(MainWindow w)
    {
        if (w.FindNode("EACN/BUNDLE.BIG/cafeLib/stdPopup.big/stdPopup.apt") is { } apt && w.FindNode("EACN/BUNDLE.BIG/cafeLib/stdPopup.big/stdPopup.const") is { } cn)
        {
            var data = apt.ReadAll();
            var file = MVPWorkshop.Core.Formats.AptFile.Read(data, MVPWorkshop.Core.Formats.AptConstants.Read(cn.ReadAll()).MovieOffset);
            if (file.Characters.OfType<MVPWorkshop.Core.Formats.AptCharacter>().FirstOrDefault(c => c.Type == MVPWorkshop.Core.Formats.AptCharacterType.EditText) is { } field)
                Editor.Apply(apt, MVPWorkshop.Core.Formats.AptEditor.EditText(data, field, new MVPWorkshop.Core.Formats.EditTextChange { Text = "Hello from the editor", FontHeight = 24 }, out _), "stdPopup.apt: text field [25] text → “Hello from the editor”");
        }
        if (w.FindNode("DATA/FEENG.LOC") is { } loc)
            Editor.Apply(loc, MVPWorkshop.Core.Formats.LocEditor.Write(loc.ReadAll(), new Dictionary<int, string> { [3] = "Edited string" }), "FEENG.LOC: string 3 → “Edited string”");
        if (w.FindNode("DATA/GHEAD.BIG") is { } head && head.Children.FirstOrDefault(c => c.IsTexturePack) is { } gst)
        {
            var img = MVPWorkshop.Core.Formats.SshFile.Read(gst.ReadAll()).Images[0];
            Editor.Session!.CommitPreviewBytes(gst, gst.ReadAll(), $"Replace texture {img.Tag} with face.png", "GST422 is EA's GST-compressed format; writing it back is not implemented, so a replacement can only be previewed.");
        }
        w.ShowChanges_Click(w, new RoutedEventArgs());
    }

    private static void SelectInnerTab(MainWindow w, int index)
    {
        if (w.Docs.SelectedItem is DocumentTab { Content: FrameworkElement root })
        {
            var tc = FindChild<System.Windows.Controls.TabControl>(root);
            if (tc is not null && index < tc.Items.Count) tc.SelectedIndex = index;
        }
    }

    private static T? FindChild<T>(DependencyObject d) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } r) return r;
        }
        return null;
    }
}


