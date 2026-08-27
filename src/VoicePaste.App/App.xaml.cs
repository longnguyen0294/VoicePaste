using System.Windows;
using VoicePaste.App.Composition;

namespace VoicePaste.App;

public partial class App : System.Windows.Application
{
    private AppHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            _host = await AppHost.CreateAsync(Dispatcher, CancellationToken.None);
            var showSettings = e.Args.Any(argument =>
                string.Equals(argument, "--show-settings", StringComparison.OrdinalIgnoreCase));
            _host.Start(showSettings);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"VoicePaste could not start: {exception.Message}",
                "VoicePaste",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            _host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.OnExit(e);
    }
}
