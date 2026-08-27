namespace VoicePaste.Core;

public sealed class DictationSessionCoordinator : IDictationSessionCoordinator
{
    private readonly IForegroundWindowTracker _foregroundWindowTracker;
    private readonly IAudioCaptureService _audioCaptureService;
    private readonly ISpeechToTextProvider _speechToTextProvider;
    private readonly ITextInsertionService _textInsertionService;
    private readonly IStatusSink _statusSink;
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SessionContext? _session;
    private DictationSessionState _state = DictationSessionState.Idle;
    private bool _disposed;

    public DictationSessionCoordinator(
        IForegroundWindowTracker foregroundWindowTracker,
        IAudioCaptureService audioCaptureService,
        ISpeechToTextProvider speechToTextProvider,
        ITextInsertionService textInsertionService,
        IStatusSink statusSink,
        AppSettings settings)
    {
        _foregroundWindowTracker = foregroundWindowTracker;
        _audioCaptureService = audioCaptureService;
        _speechToTextProvider = speechToTextProvider;
        _textInsertionService = textInsertionService;
        _statusSink = statusSink;
        _settings = settings;
    }

    public DictationSessionState State => _state;

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    public async Task<bool> StartListeningAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state != DictationSessionState.Idle || _session is not null)
            {
                return false;
            }

            var targetResult = await _foregroundWindowTracker.CaptureAsync(cancellationToken)
                .ConfigureAwait(false);
            if (targetResult is OperationFailure<TargetWindow> targetFailure)
            {
                await PublishTerminalAndIdleAsync(DictationSessionState.Error, targetFailure.Error)
                    .ConfigureAwait(false);
                return false;
            }

            var captureResult = await _audioCaptureService
                .StartAsync(_settings.MicrophoneId, cancellationToken)
                .ConfigureAwait(false);
            if (captureResult is OperationFailure<Unit> captureFailure)
            {
                await PublishTerminalAndIdleAsync(DictationSessionState.Error, captureFailure.Error)
                    .ConfigureAwait(false);
                return false;
            }

            var target = ((OperationSuccess<TargetWindow>)targetResult).Value;
            var createdSession = new SessionContext(Guid.NewGuid(), target, new CancellationTokenSource());
            _session = createdSession;
            try
            {
                await TransitionAsync(DictationSessionState.Listening, null, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch
            {
                createdSession.Cancellation.Cancel();
                createdSession.Cancellation.Dispose();
                _session = null;
                _state = DictationSessionState.Idle;
                await _audioCaptureService.CancelAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SessionContext session;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state != DictationSessionState.Listening || _session is null)
            {
                return;
            }

            session = _session;
            await TransitionAsync(DictationSessionState.Transcribing, null, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            session.Cancellation.Token);
        AudioRecording? recording = null;
        var terminalState = DictationSessionState.Error;
        OperationError? terminalError = null;

        try
        {
            var stopResult = await _audioCaptureService.StopAsync(linkedCancellation.Token)
                .ConfigureAwait(false);
            if (stopResult is OperationFailure<AudioRecording> stopFailure)
            {
                terminalError = stopFailure.Error;
                return;
            }

            recording = ((OperationSuccess<AudioRecording>)stopResult).Value;
            if (!RecordingDurationPolicy.IsUsable(recording.Content.Duration))
            {
                terminalError = new OperationError(
                    ErrorCategory.NoSpeech,
                    "recording.too_short",
                    IsRetryable: false,
                    "below_300_ms");
                return;
            }

            var options = new TranscriptionOptions(
                _settings.LanguageMode,
                _settings.LocaleHints,
                _settings.TrimTranscript);
            var transcriptionResult = await _speechToTextProvider
                .TranscribeAsync(recording, options, linkedCancellation.Token)
                .ConfigureAwait(false);
            if (transcriptionResult is OperationFailure<TranscriptionOutput> transcriptionFailure)
            {
                terminalError = transcriptionFailure.Error;
                return;
            }

            var transcript = ((OperationSuccess<TranscriptionOutput>)transcriptionResult).Value;
            var normalizedResult = TranscriptNormalizer.Normalize(transcript.Text, options.TrimWhitespace);
            if (normalizedResult is OperationFailure<string> normalizationFailure)
            {
                terminalError = normalizationFailure.Error;
                return;
            }

            await TransitionForSessionAsync(
                    session.Id,
                    DictationSessionState.Pasting,
                    null,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
            var insertionResult = await _textInsertionService
                .InsertAsync(
                    ((OperationSuccess<string>)normalizedResult).Value,
                    session.Target,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
            if (insertionResult is OperationFailure<InsertionOutcome> insertionFailure)
            {
                terminalError = insertionFailure.Error;
                return;
            }

            terminalState = DictationSessionState.Success;
        }
        catch (OperationCanceledException)
        {
            terminalState = DictationSessionState.Cancelled;
            terminalError = new OperationError(
                ErrorCategory.Cancelled,
                "session.cancelled",
                IsRetryable: false);
        }
        catch (Exception exception)
        {
            terminalError = new OperationError(
                ErrorCategory.SessionInterrupted,
                "session.unexpected_error",
                IsRetryable: true,
                exception.GetType().Name);
        }
        finally
        {
            if (recording is not null)
            {
                await recording.Content.DisposeAsync().ConfigureAwait(false);
            }

            await FinalizeSessionAsync(session.Id, terminalState, terminalError).ConfigureAwait(false);
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SessionContext session;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentSession = _session;
            if (currentSession is null)
            {
                return;
            }

            session = currentSession;
            session.Cancellation.Cancel();
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await _audioCaptureService.CancelAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await FinalizeSessionAsync(
                    session.Id,
                    DictationSessionState.Cancelled,
                    new OperationError(
                        ErrorCategory.Cancelled,
                        "session.cancelled",
                        IsRetryable: false))
                .ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_session is not null)
        {
            await CancelAsync().ConfigureAwait(false);
        }

        _disposed = true;
        _gate.Dispose();
        await _audioCaptureService.DisposeAsync().ConfigureAwait(false);
    }

    private async Task FinalizeSessionAsync(
        Guid sessionId,
        DictationSessionState terminalState,
        OperationError? error)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session?.Id != sessionId)
            {
                return;
            }

            _session.Cancellation.Dispose();
            _session = null;
            await PublishTerminalAndIdleAsync(terminalState, error).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task TransitionForSessionAsync(
        Guid sessionId,
        DictationSessionState state,
        OperationError? error,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session?.Id == sessionId)
            {
                await TransitionAsync(state, error, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PublishTerminalAndIdleAsync(
        DictationSessionState terminalState,
        OperationError? error)
    {
        await TransitionAsync(terminalState, error, CancellationToken.None).ConfigureAwait(false);
        await TransitionAsync(DictationSessionState.Idle, null, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task TransitionAsync(
        DictationSessionState next,
        OperationError? error,
        CancellationToken cancellationToken)
    {
        var previous = _state;
        _state = next;
        StateChanged?.Invoke(this, new SessionStateChangedEventArgs(previous, next, error));
        await _statusSink.PublishAsync(next, error, cancellationToken).ConfigureAwait(false);
    }

    private sealed record SessionContext(
        Guid Id,
        TargetWindow Target,
        CancellationTokenSource Cancellation);
}
