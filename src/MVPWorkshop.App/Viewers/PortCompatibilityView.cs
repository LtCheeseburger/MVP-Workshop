using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using MVPWorkshop.Core.Install;

namespace MVPWorkshop.App.Viewers;

public sealed class PortCompatibilityView : StackPanel
{
    private readonly MainWindow _window;
    public PortCompatibilityView(MainWindow window)
    {
        _window = window; Margin = new Thickness(0, 14, 0, 0);
        Loaded += (_, _) => { window.PortSettingsChanged -= Refresh; window.PortSettingsChanged += Refresh; Refresh(); };
        Unloaded += (_, _) => window.PortSettingsChanged -= Refresh;
    }
    private void Refresh()
    {
        Children.Clear();
        var info = _window.ReadPortCompatibility();
        Children.Add(new TextBlock { Text = info.Summary, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("Muted") });
        foreach (var warning in info.Warnings) Children.Add(new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("Accent"), Margin = new Thickness(0, 4, 0, 0) });
        var links = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        void Link(string text, string url)
        {
            var button = B.Button(text, null, (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Open updates"); }
            }, "Outline", url);
            button.Tag = url; links.Children.Add(button);
        }
        Link(info.RequiresNewerWorkshop ? "Update Workshop" : "Workshop updates", PortCompatibilityInfo.WorkshopUpdates);
        Link("Port updates", PortCompatibilityInfo.PortUpdates);
        Children.Add(links);
    }
}
