using System.Diagnostics;
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
        var sessionId = Guid.NewGuid();
        using var diagnostics = SessionDiagnostics.BeginSession(sessionId);
        var elapsed = Stopwatch.StartNew();
        var settings = _settings;
        var stage = "target.capture";
        CancellationTokenSource? cancellation = null;
        IStreamingSpeechToTextSession? streamingSession = null;
        SessionContext? createdSession = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state != DictationSessionState.Idle || _session is not null)
            {
                SessionDiagnostics.Write("session.start_rejected", $"State={_state} ActiveSession={_session?.Id:N}");
                return false;
            }

            SessionDiagnostics.Write("session.start", $"Mode={settings.DictationMode} Language={settings.LanguageMode} DefaultMicrophone={settings.MicrophoneId == "default"}");
            var targetResult = await _foregroundWindowTracker.CaptureAsync(cancellationToken)
                .ConfigureAwait(false);
            if (targetResult is OperationFailure<TargetWindow> targetFailure)
            {
                await PublishTerminalAndIdleAsync(DictationSessionState.Error, targetFailure.Error)
                    .ConfigureAwait(false);
                return false;
            }

            var target = ((OperationSuccess<TargetWindow>)targetResult).Value;
            cancellationToken.ThrowIfCancellationRequested();
            cancellation = new CancellationTokenSource();

            if (settings.DictationMode == DictationMode.Realtime && _streamingSpeechToTextProvider is not null)
            {
                stage = "realtime.start";
                SessionDiagnostics.Write("session.stage", $"Stage={stage} ElapsedMs={elapsed.ElapsedMilliseconds}");
                var options = new TranscriptionOptions(
                    settings.LanguageMode,
                    settings.LocaleHints,
                    settings.TrimTranscript);
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

            createdSession = new SessionContext(
                sessionId,
                target,
                cancellation,
                streamingSession,
                settings,
                OnStreamingFailure);
            _session = createdSession;
            _audioCaptureService.DataAvailable += OnAudioDataAvailable;

            stage = "audio.start";
            SessionDiagnostics.Write("session.stage", $"Stage={stage} ElapsedMs={elapsed.ElapsedMilliseconds}");
            var captureResult = await _audioCaptureService
                .StartAsync(settings.MicrophoneId, cancellationToken)
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

            cancellationToken.ThrowIfCancellationRequested();
            await TransitionAsync(DictationSessionState.Listening, null, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            SessionDiagnostics.Write("session.start_exception", $"Stage={stage} ElapsedMs={elapsed.ElapsedMilliseconds}", exception: exception);
            if (createdSession is not null)
            {
                createdSession.TryBeginTermination();
                await CleanupFailedStartAsync(createdSession).ConfigureAwait(false);
            }
            else
            {
                cancellation?.Cancel();
                cancellation?.Dispose();
                if (streamingSession is not null)
                {
                    streamingSession.InterimTranscriptReceived -= OnInterimTranscriptReceived;
                    await IgnoreCleanupFailureAsync("realtime.dispose_start", () => streamingSession.DisposeAsync().AsTask()).ConfigureAwait(false);
                }
            }

            var cancelled = exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
            await PublishTerminalAndIdleAsync(
                    cancelled ? DictationSessionState.Cancelled : DictationSessionState.Error,
                    new OperationError(
                        cancelled ? ErrorCategory.Cancelled : ErrorCategory.SessionInterrupted,
                        cancelled ? "session.cancelled" : "session.start_failed",
                        IsRetryable: !cancelled,
                        exception.GetType().Name))
                .ConfigureAwait(false);
            return false;
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

            using var transitionDiagnostics = SessionDiagnostics.BeginSession(session.Id);
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
        using var diagnostics = SessionDiagnostics.BeginSession(session.Id);
        var elapsed = Stopwatch.StartNew();
        var stage = "audio.stop";
        AudioRecording? recording = null;
        var terminalState = DictationSessionState.Error;
        OperationError? terminalError = null;

        try
        {
            SessionDiagnostics.Write("session.stage", $"Stage={stage}");
            var stopResult = await _audioCaptureService.StopAsync(linkedCancellation.Token)
                .ConfigureAwait(false);
            // Own the stopped recording before realtime drain can fail or cancel.
            if (stopResult is OperationSuccess<AudioRecording> stopped)
            {
                recording = stopped.Value;
                SessionDiagnostics.Write("audio.stopped", $"DurationMs={recording.Content.Duration.TotalMilliseconds:F0} Bytes={recording.Content.LengthBytes} SampleRate={recording.Content.Format.SampleRate} Channels={recording.Content.Format.Channels} BitsPerSample={recording.Content.Format.BitsPerSample}");
            }
            _audioCaptureService.DataAvailable -= OnAudioDataAvailable;
            stage = "realtime.drain";
            SessionDiagnostics.Write("session.stage", $"Stage={stage} ElapsedMs={elapsed.ElapsedMilliseconds}");
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
                session.Settings.LanguageMode,
                session.Settings.LocaleHints,
                session.Settings.TrimTranscript);

            stage = "transcription.complete";
            SessionDiagnostics.Write("session.stage", $"Stage={stage} ElapsedMs={elapsed.ElapsedMilliseconds}");
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
            stage = "transcript.normalize";
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
            stage = "text.insert";
            SessionDiagnostics.Write("session.stage", $"Stage={stage} ElapsedMs={elapsed.ElapsedMilliseconds}");
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
            SessionDiagnostics.Write("session.cancelled", $"Stage={stage} CallerCancelled={cancellationToken.IsCancellationRequested} SessionCancelled={session.Cancellation.IsCancellationRequested}");
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
            SessionDiagnostics.Write("session.complete_exception", $"Stage={stage} ElapsedMs={elapsed.ElapsedMilliseconds}", exception: exception);
            terminalError = new OperationError(
                ErrorCategory.SessionInterrupted,
                "session.unexpected_error",
                IsRetryable: true,
                exception.GetType().Name);
        }
        finally
        {
            SessionDiagnostics.Write("session.complete_result", $"Stage={stage} State={terminalState} ElapsedMs={elapsed.ElapsedMilliseconds}", terminalError);
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
        using var diagnostics = SessionDiagnostics.BeginSession(sessionId);
        SessionDiagnostics.Write("realtime.recovery", error: error);
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
        catch (Exception exception)
        {
            SessionDiagnostics.Write("realtime.recovery_failed", exception: exception);
            // Failure recovery is invoked from the realtime pump and must never become unobserved.
        }
    }

    private async Task CleanupFailedStartAsync(SessionContext session)
    {
        _audioCaptureService.DataAvailable -= OnAudioDataAvailable;
        session.RequestCancellation();
        session.StopAcceptingAudio();
        await IgnoreCleanupFailureAsync(
                "audio.cancel",
                () => _audioCaptureService.CancelAsync(CancellationToken.None))
            .ConfigureAwait(false);
        await IgnoreCleanupFailureAsync(
                "realtime.drain",
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
        using var diagnostics = SessionDiagnostics.BeginSession(session.Id);
        _audioCaptureService.DataAvailable -= OnAudioDataAvailable;
        session.RequestCancellation();
        session.StopAcceptingAudio();

        var cleanupFailed = false;
        cleanupFailed |= !await IgnoreCleanupFailureAsync(
                "audio.cancel",
                () => _audioCaptureService.CancelAsync(CancellationToken.None))
            .ConfigureAwait(false);
        cleanupFailed |= !await IgnoreCleanupFailureAsync(
                "realtime.drain",
                () => session.DrainAudioAsync(CleanupTimeout))
            .ConfigureAwait(false);
        cleanupFailed |= !await DisposeStreamingSessionSafelyAsync(session).ConfigureAwait(false);

        if (recording is not null)
        {
            cleanupFailed |= !await IgnoreCleanupFailureAsync(
                    "audio.dispose_content",
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
                "realtime.cancel",
                () => session.StreamingSession.CancelAsync(CancellationToken.None))
            .ConfigureAwait(false);
        var disposed = await IgnoreCleanupFailureAsync(
                "realtime.dispose",
                () => session.StreamingSession.DisposeAsync().AsTask())
            .ConfigureAwait(false);
        return cancelled && disposed;
    }

    private static async Task<bool> IgnoreCleanupFailureAsync(string step, Func<Task> cleanup)
    {
        var elapsed = Stopwatch.StartNew();
        try
        {
            await cleanup().WaitAsync(CleanupTimeout).ConfigureAwait(false);
            SessionDiagnostics.Write("session.cleanup", $"Step={step} Success=True ElapsedMs={elapsed.ElapsedMilliseconds}");
            return true;
        }
        catch (Exception exception)
        {
            SessionDiagnostics.Write("session.cleanup", $"Step={step} Success=False ElapsedMs={elapsed.ElapsedMilliseconds}", exception: exception);
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
        SessionDiagnostics.Write("session.state", $"Previous={previous} Next={next}", error);
        TryRaiseStateChanged(new SessionStateChangedEventArgs(previous, next, error));
        try
        {
            await _statusSink.PublishAsync(next, error, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SessionDiagnostics.Write("status.publish_failed", exception: exception);
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
            AppSettings settings,
            Action<Guid, OperationError> streamingFailure)
        {
            Id = id;
            Target = target;
            Cancellation = cancellation;
            StreamingSession = streamingSession;
            Settings = settings;
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

        public AppSettings Settings { get; }

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
                using var diagnostics = SessionDiagnostics.BeginSession(Id);
                SessionDiagnostics.Write("realtime.audio_send_failed", exception: exception);
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
