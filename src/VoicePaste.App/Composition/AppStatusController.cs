using System.Windows.Threading;
using VoicePaste.App.UI;
using VoicePaste.Core;

namespace VoicePaste.App.Composition;

internal sealed class AppStatusController : IStatusSink, ITranscriptFallbackStore
{
    private readonly Dispatcher _dispatcher;
    private StatusOverlayWindow? _overlay;
    private TrayIconController? _tray;
    private SettingsWindow? _settings;
    private string? _retainedTranscript;

    public AppStatusController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Attach(
        StatusOverlayWindow overlay,
        TrayIconController tray,
        SettingsWindow settings)
    {
        _overlay = overlay;
        _tray = tray;
        _settings = settings;
    }

    public ValueTask PublishAsync(
        DictationSessionState state,
        OperationError? operationError,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _dispatcher.BeginInvoke(() =>
        {
            if (state == DictationSessionState.Listening)
            {
                _retainedTranscript = null;
                _settings?.ClearRetainedTranscript();
            }

            _overlay?.UpdateState(state, operationError);
            _tray?.UpdateState(state, operationError);
            _settings?.UpdateState(state, operationError);
        }, DispatcherPriority.Background);
        return ValueTask.CompletedTask;
    }

    public void Retain(string transcript, OperationError operationError)
    {
        _retainedTranscript = transcript;
        _dispatcher.BeginInvoke(() =>
        {
            _settings?.SetRetainedTranscript(transcript, operationError);
            _tray?.ShowRecoveryNotice("Automatic paste was skipped. Open Settings to copy the transcript.");
        });
    }

    public string? GetRetainedTranscript() => _retainedTranscript;

    public void Detach()
    {
        _overlay = null;
        _tray = null;
        _settings = null;
    }

    public async Task ReportUnexpectedAsync(Exception exception)
    {
        var error = new OperationError(
            ErrorCategory.SessionInterrupted,
            "app.unexpected_error",
            IsRetryable: true,
            exception.GetType().Name);
        await PublishAsync(DictationSessionState.Error, error, CancellationToken.None);
        await PublishAsync(DictationSessionState.Idle, null, CancellationToken.None);
    }
}
