using System.Windows;
using System.Windows.Threading;

namespace MVPWorkshop.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled: " + e.Exception.Message);
        MessageBox.Show(e.Exception.ToString(), "MVP-Workshop", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
