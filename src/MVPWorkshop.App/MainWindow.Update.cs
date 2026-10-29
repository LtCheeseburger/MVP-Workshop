using System.Windows;
using System.Windows.Input;

namespace MVPWorkshop.App;

public partial class MainWindow
{
    private bool _updating;

    /// <summary>Checks GitHub for a newer build; quiet unless there is one (or <paramref name="manual"/>).</summary>
    private async Task CheckForUpdates(bool manual)
    {
        if (_updating) return;
        UpdateInfo? update;
        try
        {
            if (manual) SetStatus("Checking for updates");
            update = await Updater.CheckAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Update check failed: " + ex.Message);
            if (manual) MessageBox.Show(this, "Couldn't check for updates:\n\n" + ex.Message, "Updates", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (update is null)
        {
            if (manual) { SetStatus("Up to date"); MessageBox.Show(this, $"{AppInfo.Name} {AppInfo.Version} is the latest version.", "Updates", MessageBoxButton.OK, MessageBoxImage.Information); }
            return;
        }
        Log.Info($"Update available: {update.Tag}");
        var notes = update.Notes.Trim();
        if (notes.Length > 900) notes = notes[..900] + "…";
        var ask = $"{AppInfo.Name} {update.Version} is available (you have {AppInfo.Version}).\n\n{(notes == "" ? "" : notes + "\n\n")}Update now? The Workshop downloads it, closes, replaces its files and starts the new version.";
        if (MessageBox.Show(this, ask, "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        await InstallUpdate(update);
    }

    private async Task InstallUpdate(UpdateInfo update)
    {
        if (Updater.InstallProblem() is { } why) { MessageBox.Show(this, why, "Update", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!ConfirmDiscard("Update")) return;
        _updating = true;
        try
        {
            var staged = await Updater.DownloadAsync(update, new Progress<string>(SetStatus));
            Updater.LaunchSwap(staged);
            Log.Info($"Installing {update.Tag} and restarting");
            Closing -= OnClosing;  // changes were already saved or discarded above
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error("Update failed: " + ex.Message);
            SetStatus("Update failed");
            MessageBox.Show(this, $"The update failed; nothing was changed.\n\n{ex.Message}\n\nYou can download it from {update.PageUrl}", "Update", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _updating = false; }
    }

    private async void VersionLabel_Click(object sender, MouseButtonEventArgs e) => await CheckForUpdates(manual: true);
}
