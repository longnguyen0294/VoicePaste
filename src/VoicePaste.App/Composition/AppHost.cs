using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using VoicePaste.App.UI;
using VoicePaste.Core;
using VoicePaste.Providers.OpenAI;
using VoicePaste.Windows.Audio;
using VoicePaste.Windows.Infrastructure;
using VoicePaste.Windows.Input;
using VoicePaste.Windows.Insertion;

namespace VoicePaste.App.Composition;

internal sealed class AppHost : IAsyncDisposable
{
    private readonly IGlobalHotkeyService _hotkeyService;
    private readonly IDictationSessionCoordinator _coordinator;
    private readonly WindowsTextInsertionService _insertionService;
    private readonly OpenAiSpeechToTextProvider _speechProvider;
    private readonly AppStatusController _status;
    private readonly TrayIconController _tray;
    private readonly StatusOverlayWindow _overlay;
    private readonly SettingsWindow _settings;
    private readonly AppSettings _appSettings;
    private bool _paused;
    private bool _disposed;

    private AppHost(
        IGlobalHotkeyService hotkeyService,
        IDictationSessionCoordinator coordinator,
        WindowsTextInsertionService insertionService,
        OpenAiSpeechToTextProvider speechProvider,
        AppStatusController status,
        TrayIconController tray,
        StatusOverlayWindow overlay,
        SettingsWindow settings,
        AppSettings appSettings)
    {
        _hotkeyService = hotkeyService;
        _coordinator = coordinator;
        _insertionService = insertionService;
        _speechProvider = speechProvider;
        _status = status;
        _tray = tray;
        _overlay = overlay;
        _settings = settings;
        _appSettings = appSettings;
    }

    public static async Task<AppHost> CreateAsync(
        Dispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var settingsStore = new JsonSettingsStore();
        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(true);
        var cleanup = new TemporaryAudioCleanupService();
        await cleanup.CleanupExpiredAsync(TimeSpan.FromMinutes(15), cancellationToken).ConfigureAwait(true);

        var status = new AppStatusController(dispatcher);
        var targetTracker = new WindowsForegroundWindowTracker();
        var clipboard = new WindowsClipboardService();
        var insertion = new WindowsTextInsertionService(
            targetTracker,
            clipboard,
            status,
            settings.RestoreClipboard);
        var credentialStore = new WindowsCredentialStore();
        var providerOptions = OpenAiSpeechProviderOptions.Default with
        {
            CredentialReference = settings.CredentialReference ??
                                  OpenAiSpeechProviderOptions.DefaultCredentialReference,
        };
        var provider = new OpenAiSpeechToTextProvider(
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            credentialStore,
            providerOptions,
            ownsHttpClient: true);
        var coordinator = new DictationSessionCoordinator(
            targetTracker,
            new WasapiAudioCaptureService(),
            provider,
            insertion,
            status,
            settings);
        var hotkey = new RawInputHotkeyService();
        var overlay = new StatusOverlayWindow();
        AppHost? host = null;
        var tray = new TrayIconController(
            () => host?.ShowSettings(),
            () => host?.TogglePause(),
            () => System.Windows.Application.Current.Shutdown());
        var settingsWindow = new SettingsWindow(
            status,
            new WasapiMicrophoneCatalog(),
            credentialStore,
            providerOptions.CredentialReference);
        status.Attach(overlay, tray, settingsWindow);
        host = new AppHost(
            hotkey,
            coordinator,
            insertion,
            provider,
            status,
            tray,
            overlay,
            settingsWindow,
            settings);
        return host;
    }

    public void Start(bool showSettings = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _hotkeyService.Pressed += OnHotkeyPressed;
        _hotkeyService.Released += OnHotkeyReleased;
        _hotkeyService.Interrupted += OnHotkeyInterrupted;
        _hotkeyService.Start(_appSettings.Hotkey);
        _tray.SetPaused(paused: false);
        if (showSettings)
        {
            ShowSettings();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _hotkeyService.Pressed -= OnHotkeyPressed;
        _hotkeyService.Released -= OnHotkeyReleased;
        _hotkeyService.Interrupted -= OnHotkeyInterrupted;
        _hotkeyService.Unregister();
        _hotkeyService.Dispose();
        _status.Detach();
        _tray.Dispose();
        _overlay.Close();
        _settings.AllowClose();
        _settings.Close();
        try
        {
            await _coordinator.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _speechProvider.Dispose();
            _insertionService.Dispose();
            _disposed = true;
        }
    }

    private void ShowSettings()
    {
        if (!_settings.IsVisible)
        {
            _settings.Show();
        }

        if (_settings.WindowState == WindowState.Minimized)
        {
            _settings.WindowState = WindowState.Normal;
        }

        _settings.Activate();
    }

    private void TogglePause()
    {
        _paused = !_paused;
        if (_paused)
        {
            _hotkeyService.Unregister();
            _ = ExecuteSafelyAsync(() => _coordinator.CancelAsync());
        }
        else
        {
            _hotkeyService.Start(_appSettings.Hotkey);
        }

        _tray.SetPaused(_paused);
    }

    private async void OnHotkeyPressed(object? sender, EventArgs args)
    {
        if (!_paused)
        {
            await ExecuteSafelyAsync(() => _coordinator.StartListeningAsync());
        }
    }

    private async void OnHotkeyReleased(object? sender, EventArgs args)
    {
        if (!_paused)
        {
            await ExecuteSafelyAsync(() => _coordinator.CompleteAsync());
        }
    }

    private async void OnHotkeyInterrupted(object? sender, EventArgs args)
    {
        await ExecuteSafelyAsync(() => _coordinator.CancelAsync());
    }

    private async Task ExecuteSafelyAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await _status.ReportUnexpectedAsync(exception).ConfigureAwait(true);
        }
    }

    private async Task ExecuteSafelyAsync(Func<Task<bool>> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await _status.ReportUnexpectedAsync(exception).ConfigureAwait(true);
        }
    }
}
