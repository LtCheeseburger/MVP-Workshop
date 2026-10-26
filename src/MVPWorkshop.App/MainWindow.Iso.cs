using System.IO;
using System.Windows;
using MVPWorkshop.Core.Editing;
using Microsoft.Win32;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    private void OpenIso_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Open original game ISO", Filter = "ISO disc image|*.iso" };
        if (dialog.ShowDialog(this) == true) LoadGame(dialog.FileName);
    }
    private async void ExportIso_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting || !CheckPlayerDrafts()) return;
        if (Editor.Session is not { } session) { Log.Warn("Open a game folder or ISO first."); return; }
        string? template = null;
        if (session.GameRoot.DiscImage is null)
        {
            var input = new OpenFileDialog { Title = "Select the original ISO matching this extracted game folder", Filter = "ISO disc image|*.iso" };
            if (input.ShowDialog(this) != true) return;
            template = input.FileName;
        }
        var output = new SaveFileDialog { Title = "Export a new modified ISO", Filter = "ISO disc image|*.iso", FileName = "MVP-Workshop-modded.iso", OverwritePrompt = false };
        if (output.ShowDialog(this) != true) return;
        _exporting = true;
        try
        {
            await Task.Run(() => IsoModExporter.Export(session, output.FileName, template, message => Dispatcher.BeginInvoke(() => SetStatus(message))));
            Log.Info("Exported and verified ISO: " + output.FileName);
            SetStatus("Exported ISO: " + output.FileName);
            MessageBox.Show(this, "Exported and verified:\n" + output.FileName, "Export ISO", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { Log.Error("ISO export: " + ex.Message); MessageBox.Show(this, ex.Message, "Export ISO", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _exporting = false; }
    }
}
