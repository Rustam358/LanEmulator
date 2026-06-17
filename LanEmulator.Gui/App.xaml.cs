using System.Windows;

namespace LanEmulator.Gui;

public partial class App : Application
{
    private static System.Threading.Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new System.Threading.Mutex(true, @"Global\LanEmulatorAdapter", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Another instance of LanEmulator is already running.",
                "LanEmulator", MessageBoxButton.OK, MessageBoxImage.Information);
            _mutex?.Dispose();
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
