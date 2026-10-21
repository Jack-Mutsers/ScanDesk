using System.Windows;

namespace ScanDesk;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "ScanDesk", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        PageStore.StartSession();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        PageStore.EndSession();
        base.OnExit(e);
    }
}
