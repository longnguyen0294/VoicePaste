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
    private readonly OpenAiRealtimeSpeechToTextProvider _realtimeProvider;
    private readonly AppStatusController _status;
    private readonly TrayIconController _tray;
    private readonly StatusOverlayWindow _overlay;
    private readonly SettingsWindow _settings;
    private AppSettings _appSettings;
    private bool _paused;
    private bool _disposed;

    private AppHost(
        IGlobalHotkeyService hotkeyService,
        IDictationSessionCoordinator coordinator,
        WindowsTextInsertionService insertionService,
        OpenAiSpeechToTextProvider speechProvider,
        OpenAiRealtimeSpeechToTextProvider realtimeProvider,
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
        _realtimeProvider = realtimeProvider;
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
        SessionDiagnostics.Write("app.start", $"AppVersion={typeof(AppHost).Assembly.GetName().Version} CoreModule={typeof(DictationSessionCoordinator).Module.ModuleVersionId} OS={Environment.OSVersion.Version} Runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
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
        var realtimeProvider = new OpenAiRealtimeSpeechToTextProvider(
            credentialStore,
            OpenAiRealtimeProviderOptions.Default with
            {
                CredentialReference = providerOptions.CredentialReference,
            });
        var coordinator = new DictationSessionCoordinator(
            targetTracker,
            new WasapiAudioCaptureService(),
            provider,
            insertion,
            status,
            settings,
            realtimeProvider);
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
            settingsStore,
            settings,
            gesture =>
            {
                host?.SetRecordHotkey(gesture);
                return Task.CompletedTask;
            },
            mode =>
            {
                host?.SetDictationMode(mode);
                return Task.CompletedTask;
            },
            providerOptions.CredentialReference);
        status.Attach(overlay, tray, settingsWindow);
        host = new AppHost(
            hotkey,
            coordinator,
            insertion,
            provider,
            realtimeProvider,
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
            _realtimeProvider.Dispose();
            _insertionService.Dispose();
            _disposed = true;
            SessionDiagnostics.Write("app.stop");
            using var logTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await SessionDiagnostics.FlushAsync(logTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
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

    public void SetRecordHotkey(HotkeyGesture hotkey)
    {
        if (_appSettings.Hotkey == hotkey)
        {
            return;
        }

        _appSettings = _appSettings with { Hotkey = hotkey };
        if (!_paused)
        {
            _hotkeyService.Unregister();
            _hotkeyService.Start(_appSettings.Hotkey);
        }
    }

    public void SetDictationMode(DictationMode mode)
    {
        if (_appSettings.DictationMode == mode)
        {
            return;
        }

        _appSettings = _appSettings with { DictationMode = mode };
        if (_coordinator is DictationSessionCoordinator concreteCoordinator)
        {
            concreteCoordinator.UpdateSettings(_appSettings);
        }
    }

    private void TogglePause()
    {
        _paused = !_paused;
        if (_paused)
        {
            _hotkeyService.Unregister();
            _ = ExecuteSafelyAsync("hotkey.cancel", () => _coordinator.CancelAsync());
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
            await ExecuteSafelyAsync("hotkey.pressed", () => _coordinator.StartListeningAsync());
        }
    }

    private async void OnHotkeyReleased(object? sender, EventArgs args)
    {
        if (!_paused)
        {
            await ExecuteSafelyAsync("hotkey.released", () => _coordinator.CompleteAsync());
        }
    }

    private async void OnHotkeyInterrupted(object? sender, EventArgs args)
    {
        await ExecuteSafelyAsync("hotkey.cancel", () => _coordinator.CancelAsync());
    }

    private async Task ExecuteSafelyAsync(string context, Func<Task> action)
    {
        try
        {
            SessionDiagnostics.Write(context, $"State={_coordinator.State}");
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SessionDiagnostics.Write("app.action_exception", $"Action={context} State={_coordinator.State}", exception: exception);
            await _status.ReportUnexpectedAsync(exception).ConfigureAwait(true);
        }
    }

    private async Task ExecuteSafelyAsync(string context, Func<Task<bool>> action)
    {
        try
        {
            SessionDiagnostics.Write(context, $"State={_coordinator.State}");
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SessionDiagnostics.Write("app.action_exception", $"Action={context} State={_coordinator.State}", exception: exception);
            await _status.ReportUnexpectedAsync(exception).ConfigureAwait(true);
        }
    }
}
