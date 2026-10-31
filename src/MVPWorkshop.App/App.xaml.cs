using System.Windows;
using System.Windows.Threading;

namespace MVPWorkshop.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => CrashReporter.Handle(args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()), "AppDomain");
        TaskScheduler.UnobservedTaskException += (_, args) => { CrashReporter.Handle(args.Exception, "TaskScheduler"); args.SetObserved(); };
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashReporter.Handle(e.Exception, "Dispatcher");
        e.Handled = true;
    }
}
