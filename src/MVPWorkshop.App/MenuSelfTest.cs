using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Formats;

namespace MVPWorkshop.App;

public static class MenuSelfTest
{
    public static async void Run(MainWindow window, string reportPath)
    {
        var report = new StringBuilder(); int failures = 0;
        void Check(string name, bool passed) { report.AppendLine($"{(passed ? "PASS" : "FAIL")} {name}"); if (!passed) failures++; }
        try
        {
            var nodes = await window.GetAssetIndex();
            var menus = nodes.Where(n => n.Extension == ".fel").OrderByDescending(n => n.Name == "fes_mainmenu.fel").ThenByDescending(n => n.Name == "fes_teamselect.fel").ThenBy(n => n.Path).Select(n => (Node: n, File: FelFile.Read(FormatInspector.ReadText(n.Open()))))
                .Where(m => m.File.Elements.Any(e => e.Kind == "BU")).Take(3).ToList();
            if (menus.Count < 2) throw new InvalidOperationException("Need at least two decoded FEL button screens");
            var host = new MenuSimulatorView(menus[0].Node); window.Content = host;
            for (int i = 0; i < 240 && !host.Ready; i++) await Task.Delay(100);
            await Task.Delay(800);
            Check("simulator loaded decoded context", host.Ready && host.ContextSummary.Contains("player.name") && !host.ContextSummary.Contains("home.name = \n"));
            Check("visible authored FEL controls discovered", host.Session.Controls.Count > 0);
            report.AppendLine("FEL source: " + host.Session.Screen);
            var origin = host.Session.Screen;
            var focused = host.Session.Focus;
            host.Move(0, 1); host.Move(0, -1);
            Check("navigation retains valid focus", host.Session.Controls.Any(c => c.Id == host.Session.Focus));
            if (host.CurrentPreview is FelView dataScreen && dataScreen.FirstVisibleTextName is { } field)
            {
                host.SetPlayback(false); dataScreen.ConfigurePreview(null, double.MaxValue, []);
                var pixels = dataScreen.SimulationPixels(); host.SetPreviewBinding(field, "home.name"); await Task.Delay(100);
                Check("decoded context binding changes rendered screen", !pixels.SequenceEqual(dataScreen.SimulationPixels()));
                host.SetPlayback(true);
            }
            else Check("decoded context has a visible binding target", false);
            focused = host.Session.Focus;
            host.Session.Routes.Add(new(origin, host.Session.Focus!, menus[1].Node.Path, false));
            host.Activate(); await Task.Delay(600);
            Check("connected FEL activation transitions", host.Session.Screen == menus[1].Node.Path);
            var second = host.Session.Screen;
            host.Session.Open(origin, true); host.ShowScreen(); await Task.Delay(600);
            Check("popup is modal with authored preview", host.Session.IsPopup && host.CurrentPreview is FelView);
            host.Back(); await Task.Delay(600);
            Check("popup back restores previous screen", !host.Session.IsPopup && host.Session.Screen == second);
            host.Back(); await Task.Delay(600);
            Check("transition back restores valid focus", host.Session.Screen == origin && host.Session.Focus == focused);
            var before = host.CurrentPreview;
            var original = menus[0].Node.ReadAll();
            Editor.Apply(menus[0].Node, Encoding.UTF8.GetBytes(FormatInspector.ReadText(menus[0].Node.Open()) + "\n# simulator live edit check\n"), "Menu simulator validation");
            await Task.Delay(1600);
            Check("live project edits rebuild preview and preserve routes", !ReferenceEquals(before, host.CurrentPreview) && host.Session.Routes.Count == 1);
            Editor.Session!.Undo(); await Task.Delay(1200);
            Check("undo restores source bytes", menus[0].Node.ReadAll().SequenceEqual(original));
            var apt = nodes.First(n => n.Path.EndsWith("stdPopup.apt", StringComparison.OrdinalIgnoreCase));
            host.Session.Open(apt.Path); host.ShowScreen(); await Task.Delay(1200);
            Check("connected APT preview mounts", host.CurrentPreview is AptSceneView);
            report.AppendLine("APT source: " + apt.Path);
            if (host.CurrentPreview is AptSceneView scene)
            {
                scene.SimulationEvent("", "missingTestHandler");
                Check("unsupported handler explicitly diagnosed", host.Session.Trace.Any(t => t.Contains("missingTestHandler") && t.StartsWith("diagnostic")));
            }
            host.Session.Reset(origin); host.ShowScreen(); await Task.Delay(700);
            Check("reset clears navigation history", !host.Session.Back());
            window.UpdateLayout();
            var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using (var stream = File.Create(reportPath + ".png")) png.Save(stream);
            report.AppendLine(string.Join("\n", host.Session.Trace));
        }
        catch (Exception ex) { failures++; report.AppendLine("FAIL " + ex); }
        report.AppendLine($"Failures: {failures}"); File.WriteAllText(reportPath, report.ToString());
        Application.Current.Shutdown(failures == 0 ? 0 : 1);
    }
}
