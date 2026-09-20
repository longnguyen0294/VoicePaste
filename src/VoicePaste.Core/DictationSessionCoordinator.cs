namespace VoicePaste.Core;

public sealed class DictationSessionCoordinator : IDictationSessionCoordinator
{
    private readonly IForegroundWindowTracker _foregroundWindowTracker;
    private readonly IAudioCaptureService _audioCaptureService;
    private readonly ISpeechToTextProvider _speechToTextProvider;
    private readonly IStreamingSpeechToTextProvider? _streamingSpeechToTextProvider;
    private readonly ITextInsertionService _textInsertionService;
    private readonly IStatusSink _statusSink;
    private volatile AppSettings _settings;  // M1: volatile for cross-thread visibility from UI thread updates
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
        AppSettings settings,
        IStreamingSpeechToTextProvider? streamingSpeechToTextProvider = null)
    {
        _foregroundWindowTracker = foregroundWindowTracker;
        _audioCaptureService = audioCaptureService;
        _speechToTextProvider = speechToTextProvider;
        _streamingSpeechToTextProvider = streamingSpeechToTextProvider;
        _textInsertionService = textInsertionService;
        _statusSink = statusSink;
        _settings = settings;
    }

    public DictationSessionState State => _state;

    public event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    public void UpdateSettings(AppSettings settings) => _settings = settings;

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

            var target = ((OperationSuccess<TargetWindow>)targetResult).Value;
            var cancellation = new CancellationTokenSource();
            IStreamingSpeechToTextSession? streamingSession = null;

            if (_settings.DictationMode == DictationMode.Realtime && _streamingSpeechToTextProvider is not null)
            {
                var options = new TranscriptionOptions(
                    _settings.LanguageMode,
                    _settings.LocaleHints,
                    _settings.TrimTranscript);
                var streamSessionResult = await _streamingSpeechToTextProvider
                    .StartSessionAsync(options, cancellationToken)
                    .ConfigureAwait(false);

                if (streamSessionResult is OperationSuccess<IStreamingSpeechToTextSession> success)
                {
                    streamingSession = success.Value;
                    streamingSession.InterimTranscriptReceived += OnInterimTranscriptReceived;
                }
                else if (streamSessionResult is OperationFailure<IStreamingSpeechToTextSession> failure)
                {
                    cancellation.Cancel();
                    cancellation.Dispose();
                    await PublishTerminalAndIdleAsync(DictationSessionState.Error, failure.Error)
                        .ConfigureAwait(false);
                    return false;
                }
            }

            var createdSession = new SessionContext(Guid.NewGuid(), target, cancellation, streamingSession);
            _session = createdSession;
            _audioCaptureService.DataAvailable += OnAudioDataAvailable;

            try
            {
                var captureResult = await _audioCaptureService
                    .StartAsync(_settings.MicrophoneId, cancellationToken)
                    .ConfigureAwait(false);
                if (captureResult is OperationFailure<Unit> captureFailure)
                {
                    _audioCaptureService.DataAvailable -= OnAudioDataAvailable;
                    if (streamingSession is not null)
                    {
                        streamingSession.InterimTranscriptReceived -= OnInterimTranscriptReceived;
                        await streamingSession.DisposeAsync().ConfigureAwait(false);
                    }

                    cancellation.Cancel();
                    cancellation.Dispose();
                    _session = null;
                    await PublishTerminalAndIdleAsync(DictationSessionState.Error, captureFailure.Error)
                        .ConfigureAwait(false);
                    return false;
                }

                await TransitionAsync(DictationSessionState.Listening, null, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch
            {
                _audioCaptureService.DataAvailable -= OnAudioDataAvailable;
                if (streamingSession is not null)
                {
                    streamingSession.InterimTranscriptReceived -= OnInterimTranscriptReceived;
                    await streamingSession.DisposeAsync().ConfigureAwait(false);
                }

                cancellation.Cancel();
                cancellation.Dispose();
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
            _audioCaptureService.DataAvailable -= OnAudioDataAvailable;
            await session
                .StopAcceptingAudioAndDrainAsync(linkedCancellation.Token)
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

            OperationResult<TranscriptionOutput> transcriptionResult;
            if (session.StreamingSession is not null)
            {
                transcriptionResult = await session.StreamingSession
                    .CompleteAsync(linkedCancellation.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                transcriptionResult = await _speechToTextProvider
                    .TranscribeAsync(recording, options, linkedCancellation.Token)
                    .ConfigureAwait(false);
            }

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
            _audioCaptureService.DataAvailable -= OnAudioDataAvailable;
            session.Cancellation.Cancel();
            await session
                .StopAcceptingAudioAndDrainAsync(CancellationToken.None)
                .ConfigureAwait(false);
            if (session.StreamingSession is not null)
            {
                session.StreamingSession.InterimTranscriptReceived -= OnInterimTranscriptReceived;
                await session.StreamingSession.DisposeAsync().ConfigureAwait(false);
            }

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
            _audioCaptureService.DataAvailable -= OnAudioDataAvailable;
            await session
                .StopAcceptingAudioAndDrainAsync(CancellationToken.None)
                .ConfigureAwait(false);
            if (session.StreamingSession is not null)
            {
                session.StreamingSession.InterimTranscriptReceived -= OnInterimTranscriptReceived;
                await session.StreamingSession.CancelAsync(cancellationToken).ConfigureAwait(false);
                await session.StreamingSession.DisposeAsync().ConfigureAwait(false);
            }

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

    private void OnAudioDataAvailable(object? sender, AudioChunkAvailableEventArgs args)
    {
        var session = _session;
        if (session?.StreamingSession is null || session.Cancellation.IsCancellationRequested)
        {
            return;
        }

        session.EnqueueAudioChunk(args.Data);
    }

    private static async Task SendAudioChunkSafelyAsync(
        IStreamingSpeechToTextSession streamingSession,
        ReadOnlyMemory<byte> data,
        CancellationTokenSource cancellation)
    {
        try
        {
            await streamingSession.SendAudioChunkAsync(data, cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            cancellation.Cancel();
        }
    }

    private void OnInterimTranscriptReceived(object? sender, string interimText)
    {
        if (_session is not null)
        {
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs(
                _state,
                _state,
                error: null,
                interimTranscript: interimText));
            _ = _statusSink.PublishInterimTranscriptAsync(interimText, CancellationToken.None).AsTask();
        }
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

    private sealed class SessionContext(
        Guid id,
        TargetWindow target,
        CancellationTokenSource cancellation,
        IStreamingSpeechToTextSession? streamingSession = null)
    {
        private readonly object _audioSendGate = new();
        private Task _audioSendTail = Task.CompletedTask;
        private bool _acceptsAudio = true;

        public Guid Id { get; } = id;

        public TargetWindow Target { get; } = target;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public IStreamingSpeechToTextSession? StreamingSession { get; } = streamingSession;

        public void EnqueueAudioChunk(ReadOnlyMemory<byte> data)
        {
            if (StreamingSession is null || data.IsEmpty)
            {
                return;
            }

            lock (_audioSendGate)
            {
                if (!_acceptsAudio)
                {
                    return;
                }

                _audioSendTail = _audioSendTail
                    .ContinueWith(
                        _ => SendAudioChunkSafelyAsync(StreamingSession, data, Cancellation),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default)
                    .Unwrap();
            }
        }

        public Task StopAcceptingAudioAndDrainAsync(CancellationToken cancellationToken)
        {
            Task audioSendTail;
            lock (_audioSendGate)
            {
                _acceptsAudio = false;
                audioSendTail = _audioSendTail;
            }

            return audioSendTail.WaitAsync(cancellationToken);
        }
    }
}
