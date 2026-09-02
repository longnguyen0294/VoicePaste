namespace VoicePaste.Core;

public sealed class AudioChunkAvailableEventArgs(ReadOnlyMemory<byte> data) : EventArgs
{
    public ReadOnlyMemory<byte> Data { get; } = data;
}

public interface IAudioCaptureService : IAsyncDisposable
{
    event EventHandler<AudioChunkAvailableEventArgs>? DataAvailable;

    Task<OperationResult<Unit>> StartAsync(string microphoneId, CancellationToken cancellationToken);

    Task<OperationResult<AudioRecording>> StopAsync(CancellationToken cancellationToken);

    Task CancelAsync(CancellationToken cancellationToken);
}

public interface IMicrophoneCatalog
{
    Task<IReadOnlyList<MicrophoneDevice>> GetAvailableAsync(CancellationToken cancellationToken);
}

public interface ISpeechToTextProvider
{
    string ProviderId { get; }

    SpeechProviderCapabilities Capabilities { get; }

    Task<OperationResult<TranscriptionOutput>> TranscribeAsync(
        AudioRecording audio,
        TranscriptionOptions options,
        CancellationToken cancellationToken);
}

public interface IStreamingSpeechToTextSession : IAsyncDisposable
{
    event EventHandler<string>? InterimTranscriptReceived;

    Task SendAudioChunkAsync(ReadOnlyMemory<byte> pcmChunk, CancellationToken cancellationToken);

    Task<OperationResult<TranscriptionOutput>> CompleteAsync(CancellationToken cancellationToken);

    Task CancelAsync(CancellationToken cancellationToken);
}

public interface IStreamingSpeechToTextProvider
{
    string ProviderId { get; }

    SpeechProviderCapabilities Capabilities { get; }

    Task<OperationResult<IStreamingSpeechToTextSession>> StartSessionAsync(
        TranscriptionOptions options,
        CancellationToken cancellationToken);
}

public interface ITextInsertionService
{
    Task<OperationResult<InsertionOutcome>> InsertAsync(
        string text,
        TargetWindow target,
        CancellationToken cancellationToken);
}

public interface ITranscriptFallbackStore
{
    void Retain(string transcript, OperationError operationError);
}

public interface IForegroundWindowTracker
{
    Task<OperationResult<TargetWindow>> CaptureAsync(CancellationToken cancellationToken);
}

public interface IGlobalHotkeyService : IDisposable
{
    event EventHandler? Pressed;

    event EventHandler? Released;

    event EventHandler? Interrupted;

    void Start(HotkeyGesture gesture);

    void Unregister();
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}

public interface ICredentialStore
{
    Task SaveAsync(string reference, string secret, CancellationToken cancellationToken);

    Task<string?> ReadAsync(string reference, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredCredentialDescriptor>> ListAsync(CancellationToken cancellationToken);

    Task DeleteAsync(string reference, CancellationToken cancellationToken);
}

public interface IStatusSink
{
    ValueTask PublishAsync(
        DictationSessionState state,
        OperationError? operationError,
        CancellationToken cancellationToken);

    ValueTask PublishInterimTranscriptAsync(
        string interimTranscript,
        CancellationToken cancellationToken);
}

public interface IDictationSessionCoordinator : IAsyncDisposable
{
    DictationSessionState State { get; }

    event EventHandler<SessionStateChangedEventArgs>? StateChanged;

    Task<bool> StartListeningAsync(CancellationToken cancellationToken = default);

    Task CompleteAsync(CancellationToken cancellationToken = default);

    Task CancelAsync(CancellationToken cancellationToken = default);
}
