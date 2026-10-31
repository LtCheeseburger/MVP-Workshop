using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using MVPWorkshop.App.Viewers;
using MVPWorkshop.Core.Install;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    internal event Action? PortSettingsChanged;
    internal PortCompatibilityInfo ReadPortCompatibility()
    {
        var portDirectory = Path.GetDirectoryName(InstalledModsDirectory())!;
        return PortCompatibility.Read(portDirectory, _gameRoot?.DiskPath ?? "");
    }

    internal Window CreateExportSummary(string text, bool compatibilityWarnings)
    {
        var panel = new DockPanel { Margin = new Thickness(20) };
        var dialog = new Window { Title = "Mod exported", Owner = this, Content = panel, Width = 740, MaxHeight = 680,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Ui.Res("WindowBg"), Foreground = Ui.Res("Text") };
        var footer = new StackPanel { Margin = new Thickness(0, 16, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        if (compatibilityWarnings)
        {
            var links = new WrapPanel();
            foreach (var (label, url) in new[] { ("Update Workshop", PortCompatibilityInfo.WorkshopUpdates), ("Port updates", PortCompatibilityInfo.PortUpdates) })
            {
                var link = B.Button(label, null, (_, _) =>
                {
                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                    catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Open updates"); }
                }, "Outline", url);
                link.Tag = url; links.Children.Add(link);
            }
            footer.Children.Add(links);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var open = B.Button("Open folder", null, (_, _) => dialog.DialogResult = true, "Primary");
        var close = B.Button("Close", null, (_, _) => dialog.Close(), "Outline"); close.IsCancel = true;
        buttons.Children.Add(open); buttons.Children.Add(close); footer.Children.Add(buttons);
        panel.Children.Add(new ScrollViewer { MaxHeight = 500, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } });
        return dialog;
    }
}
