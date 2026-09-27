using System.Threading.Channels;

namespace VoicePaste.Core;

public sealed class DictationSessionCoordinator : IDictationSessionCoordinator
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(3);
    private readonly IForegroundWindowTracker _foregroundWindowTracker;
    private readonly IAudioCaptureService _audioCaptureService;
    private readonly ISpeechToTextProvider _speechToTextProvider;
    private readonly IStreamingSpeechToTextProvider? _streamingSpeechToTextProvider;
    private readonly ITextInsertionService _textInsertionService;
    private readonly IStatusSink _statusSink;
    private volatile AppSettings _settings;  // M1: volatile for cross-thread visibility from UI thread updates
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SessionContext? _session;
    private volatile DictationSessionState _state = DictationSessionState.Idle;
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

            var createdSession = new SessionContext(
                Guid.NewGuid(),
                target,
                cancellation,
                streamingSession,
                OnStreamingFailure);
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
                    createdSession.TryBeginTermination();
                    await CleanupFailedStartAsync(createdSession).ConfigureAwait(false);
                    await PublishTerminalAndIdleAsync(DictationSessionState.Error, captureFailure.Error)
                        .ConfigureAwait(false);
                    return false;
                }

                await TransitionAsync(DictationSessionState.Listening, null, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch (Exception exception)
            {
                _audioCaptureService.DataAvailable -= OnAudioDataAvailable;
                createdSession.TryBeginTermination();
                await CleanupFailedStartAsync(createdSession).ConfigureAwait(false);
                await PublishTerminalAndIdleAsync(
                        DictationSessionState.Error,
                        new OperationError(
                            ErrorCategory.SessionInterrupted,
                            "session.start_failed",
                            IsRetryable: true,
                            exception.GetType().Name))
                    .ConfigureAwait(false);
                return false;
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
            if (!session.TryBeginTermination())
            {
                return;
            }

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
            if (session.StreamingError is not null)
            {
                terminalError = session.StreamingError;
                return;
            }

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
            if (session.StreamingError is not null)
            {
                terminalError = session.StreamingError;
            }
            else
            {
                terminalState = DictationSessionState.Cancelled;
                terminalError = new OperationError(
                    ErrorCategory.Cancelled,
                    "session.cancelled",
                    IsRetryable: false);
            }
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
            await CleanupAndFinalizeSessionAsync(
                    session,
                    recording,
                    terminalState,
                    terminalError)
                .ConfigureAwait(false);
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SessionContext session;
        bool ownsTermination;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentSession = _session;
            if (currentSession is null)
            {
                return;
            }

            session = currentSession;
            session.RequestCancellation();
            ownsTermination = session.TryBeginTermination();
        }
        finally
        {
            _gate.Release();
        }

        if (!ownsTermination)
        {
            await session.Terminated.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await CleanupAndFinalizeSessionAsync(
                session,
                recording: null,
                DictationSessionState.Cancelled,
                new OperationError(
                    ErrorCategory.Cancelled,
                    "session.cancelled",
                    IsRetryable: false))
            .ConfigureAwait(false);
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

    private void OnStreamingFailure(Guid sessionId, OperationError error)
    {
        _ = RecoverFromStreamingFailureAsync(sessionId, error);
    }

    private void OnInterimTranscriptReceived(object? sender, string interimText)
    {
        if (_session is not null)
        {
            TryRaiseStateChanged(new SessionStateChangedEventArgs(
                _state,
                _state,
                error: null,
                interimTranscript: interimText));
            _ = PublishInterimSafelyAsync(interimText);
        }
    }

    private async Task RecoverFromStreamingFailureAsync(Guid sessionId, OperationError error)
    {
        SessionContext? session = null;
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_session?.Id == sessionId && _session.TryBeginTermination())
                {
                    session = _session;
                }
            }
            finally
            {
                _gate.Release();
            }

            if (session is not null)
            {
                await CleanupAndFinalizeSessionAsync(
                        session,
                        recording: null,
                        DictationSessionState.Error,
                        error)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // Failure recovery is invoked from the realtime pump and must never become unobserved.
        }
    }

    private async Task CleanupFailedStartAsync(SessionContext session)
    {
        _audioCaptureService.DataAvailable -= OnAudioDataAvailable;
        session.RequestCancellation();
        session.StopAcceptingAudio();
        await IgnoreCleanupFailureAsync(
                () => _audioCaptureService.CancelAsync(CancellationToken.None))
            .ConfigureAwait(false);
        await IgnoreCleanupFailureAsync(
                () => session.DrainAudioAsync(CleanupTimeout))
            .ConfigureAwait(false);
        await DisposeStreamingSessionSafelyAsync(session).ConfigureAwait(false);
        session.DisposeCancellation();
        session.SignalTerminated();
        if (_session?.Id == session.Id)
        {
            _session = null;
        }
    }

    private async Task CleanupAndFinalizeSessionAsync(
        SessionContext session,
        AudioRecording? recording,
        DictationSessionState terminalState,
        OperationError? terminalError)
    {
        _audioCaptureService.DataAvailable -= OnAudioDataAvailable;
        session.RequestCancellation();
        session.StopAcceptingAudio();

        var cleanupFailed = false;
        cleanupFailed |= !await IgnoreCleanupFailureAsync(
                () => _audioCaptureService.CancelAsync(CancellationToken.None))
            .ConfigureAwait(false);
        cleanupFailed |= !await IgnoreCleanupFailureAsync(
                () => session.DrainAudioAsync(CleanupTimeout))
            .ConfigureAwait(false);
        cleanupFailed |= !await DisposeStreamingSessionSafelyAsync(session).ConfigureAwait(false);

        if (recording is not null)
        {
            cleanupFailed |= !await IgnoreCleanupFailureAsync(
                    () => recording.Content.DisposeAsync().AsTask())
                .ConfigureAwait(false);
        }

        if (cleanupFailed && terminalError is null)
        {
            terminalState = DictationSessionState.Error;
            terminalError = new OperationError(
                ErrorCategory.SessionInterrupted,
                "session.cleanup_failed",
                IsRetryable: true,
                "terminal_cleanup_failed");
        }

        try
        {
            await FinalizeSessionAsync(session.Id, terminalState, terminalError).ConfigureAwait(false);
        }
        finally
        {
            session.DisposeCancellation();
            session.SignalTerminated();
        }
    }

    private async Task<bool> DisposeStreamingSessionSafelyAsync(SessionContext session)
    {
        if (session.StreamingSession is null)
        {
            return true;
        }

        session.StreamingSession.InterimTranscriptReceived -= OnInterimTranscriptReceived;
        var cancelled = await IgnoreCleanupFailureAsync(
                () => session.StreamingSession.CancelAsync(CancellationToken.None))
            .ConfigureAwait(false);
        var disposed = await IgnoreCleanupFailureAsync(
                () => session.StreamingSession.DisposeAsync().AsTask())
            .ConfigureAwait(false);
        return cancelled && disposed;
    }

    private static async Task<bool> IgnoreCleanupFailureAsync(Func<Task> cleanup)
    {
        try
        {
            await cleanup().WaitAsync(CleanupTimeout).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task PublishInterimSafelyAsync(string interimText)
    {
        try
        {
            await _statusSink
                .PublishInterimTranscriptAsync(interimText, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Status rendering cannot own or terminate a dictation session.
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
        TryRaiseStateChanged(new SessionStateChangedEventArgs(previous, next, error));
        try
        {
            await _statusSink.PublishAsync(next, error, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // UI/status failures must not strand session resources or corrupt the state machine.
        }
    }

    private void TryRaiseStateChanged(SessionStateChangedEventArgs args)
    {
        try
        {
            StateChanged?.Invoke(this, args);
        }
        catch
        {
            // Observers cannot own the session lifecycle.
        }
    }

    private sealed class SessionContext
    {
        // Roughly 6.4 seconds at the current 50 ms WASAPI callback cadence (~300 KiB PCM).
        // This absorbs short network stalls while staying far below the 16 MiB capture budget.
        private const int StreamingQueueCapacity = 128;
        private readonly Channel<ReadOnlyMemory<byte>>? _audioQueue;
        private readonly Action<Guid, OperationError> _streamingFailure;
        private readonly TaskCompletionSource _terminated = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task? _audioSendTask;
        private OperationError? _streamingError;
        private int _acceptsAudio = 1;
        private int _terminationStarted;
        private int _cancellationDisposed;

        public SessionContext(
            Guid id,
            TargetWindow target,
            CancellationTokenSource cancellation,
            IStreamingSpeechToTextSession? streamingSession,
            Action<Guid, OperationError> streamingFailure)
        {
            Id = id;
            Target = target;
            Cancellation = cancellation;
            StreamingSession = streamingSession;
            _streamingFailure = streamingFailure;
            _audioQueue = streamingSession is null
                ? null
                : Channel.CreateBounded<ReadOnlyMemory<byte>>(
                    new BoundedChannelOptions(StreamingQueueCapacity)
                    {
                        SingleReader = true,
                        SingleWriter = false,
                        FullMode = BoundedChannelFullMode.Wait,
                    });
            if (_audioQueue is not null)
            {
                _audioSendTask = RunAudioPumpAsync(_audioQueue.Reader);
            }
        }

        public Guid Id { get; }

        public TargetWindow Target { get; }

        public CancellationTokenSource Cancellation { get; }

        public IStreamingSpeechToTextSession? StreamingSession { get; }

        public OperationError? StreamingError => Volatile.Read(ref _streamingError);

        public Task Terminated => _terminated.Task;

        public bool TryBeginTermination() =>
            Interlocked.CompareExchange(ref _terminationStarted, 1, 0) == 0;

        public void RequestCancellation()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void EnqueueAudioChunk(ReadOnlyMemory<byte> data)
        {
            if (_audioQueue is null || data.IsEmpty || Volatile.Read(ref _acceptsAudio) == 0)
            {
                return;
            }

            if (!_audioQueue.Writer.TryWrite(data) && Volatile.Read(ref _acceptsAudio) != 0)
            {
                FailStreaming(new OperationError(
                    ErrorCategory.ProviderFailure,
                    "provider.audio_stream_backpressure",
                    IsRetryable: true,
                    "realtime_audio_queue_full"));
            }
        }

        public async Task StopAcceptingAudioAndDrainAsync(CancellationToken cancellationToken)
        {
            StopAcceptingAudio();
            if (_audioSendTask is not null)
            {
                await _audioSendTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public void StopAcceptingAudio()
        {
            if (Interlocked.Exchange(ref _acceptsAudio, 0) != 0)
            {
                _audioQueue?.Writer.TryComplete();
            }
        }

        public async Task DrainAudioAsync(TimeSpan timeout)
        {
            StopAcceptingAudio();
            if (_audioSendTask is not null)
            {
                await _audioSendTask.WaitAsync(timeout).ConfigureAwait(false);
            }
        }

        public void DisposeCancellation()
        {
            if (Interlocked.Exchange(ref _cancellationDisposed, 1) == 0)
            {
                Cancellation.Dispose();
            }
        }

        public void SignalTerminated() => _terminated.TrySetResult();

        private async Task RunAudioPumpAsync(ChannelReader<ReadOnlyMemory<byte>> reader)
        {
            try
            {
                await foreach (var data in reader.ReadAllAsync(Cancellation.Token).ConfigureAwait(false))
                {
                    await StreamingSession!
                        .SendAudioChunkAsync(data, Cancellation.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (Cancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                FailStreaming(new OperationError(
                    ErrorCategory.ProviderFailure,
                    "provider.audio_stream_failed",
                    IsRetryable: true,
                    exception.GetType().Name));
            }
        }

        private void FailStreaming(OperationError error)
        {
            if (Interlocked.CompareExchange(ref _streamingError, error, null) is not null)
            {
                return;
            }

            RequestCancellation();
            StopAcceptingAudio();
            _streamingFailure(Id, error);
        }
    }
}
