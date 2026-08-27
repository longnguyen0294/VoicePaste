using System.Collections.ObjectModel;

namespace VoicePaste.Core;

public enum DictationSessionState
{
    Idle,
    Listening,
    Transcribing,
    Pasting,
    Success,
    Cancelled,
    Error,
}

[Flags]
public enum KeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Windows = 8,
}

public enum KeySide
{
    None,
    Left,
    Right,
}

public enum VirtualKey : ushort
{
    Escape = 0x1B,
    Control = 0x11,
    A = 0x41,
    Z = 0x5A,
}

public sealed record PhysicalKey(
    VirtualKey VirtualKey,
    ushort ScanCode,
    bool IsExtended,
    KeySide Side);

public sealed record HotkeyGesture(
    PhysicalKey Trigger,
    KeyModifiers RequiredModifiers);

public sealed record TargetWindow(
    nint Handle,
    uint ProcessId,
    uint ThreadId,
    string? ProcessName,
    DateTimeOffset? ProcessStartedAt,
    int IntegrityLevel,
    DateTimeOffset CapturedAt);

public sealed record MicrophoneDevice(
    string Id,
    string DisplayName,
    bool IsDefault,
    bool IsAvailable);

public sealed record AudioFormat(
    int SampleRate,
    int Channels,
    int BitsPerSample,
    string Encoding)
{
    public int BytesPerSecond => checked(SampleRate * Channels * BitsPerSample / 8);
}

public interface IAudioContent : IAsyncDisposable
{
    AudioFormat Format { get; }

    TimeSpan Duration { get; }

    long? LengthBytes { get; }

    ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken);
}

public sealed record AudioRecording(
    string RecordingId,
    IAudioContent Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt);

public enum LanguageMode
{
    Vietnamese,
    English,
    VietnameseEnglishMixed,
    Automatic,
}

public sealed record TranscriptionOptions(
    LanguageMode LanguageMode,
    IReadOnlyList<string> LocaleHints,
    bool TrimWhitespace);

public sealed record SpeechProviderCapabilities(
    IReadOnlySet<LanguageMode> LanguageModes,
    IReadOnlySet<string> LocaleHints,
    TimeSpan? MaximumRequestDuration,
    long? MaximumRequestBytes,
    bool SupportsStreaming,
    bool SendsAudioOffDevice);

public sealed record TranscriptionOutput(
    string Text,
    string? DetectedLanguage,
    TimeSpan ProcessingTime);

public abstract record OperationResult<T>
{
    public bool IsSuccess => this is OperationSuccess<T>;
}

public sealed record OperationSuccess<T>(T Value) : OperationResult<T>;

public sealed record OperationFailure<T>(OperationError Error) : OperationResult<T>;

public static class OperationResult
{
    public static OperationResult<T> Success<T>(T value) => new OperationSuccess<T>(value);

    public static OperationResult<T> Failure<T>(OperationError operationError) =>
        new OperationFailure<T>(operationError);
}

public readonly record struct Unit
{
    public static Unit Value => default;
}

public sealed record InsertionOutcome(
    bool WasAutomaticallyPasted,
    bool IsAvailableForManualCopy);

public sealed record ClipboardLease(
    string LeaseId,
    Guid LeaseToken,
    string PrivateClipboardFormat,
    uint SequenceNumberAfterWrite,
    nint OwnerWindow,
    long SnapshotSizeBytes,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt);

public enum ErrorCategory
{
    NoSpeech,
    MicrophoneUnavailable,
    MicrophonePermissionDenied,
    NetworkUnavailable,
    AuthenticationFailed,
    QuotaExceeded,
    TimedOut,
    ProviderFailure,
    ProviderPayloadTooLarge,
    AudioEncodingFailed,
    ClipboardBusy,
    TargetUnavailable,
    TargetPrivilegeMismatch,
    StorageUnavailable,
    StorageFull,
    HotkeyInputUnavailable,
    SessionInterrupted,
    Cancelled,
}

public sealed record OperationError(
    ErrorCategory Category,
    string UserMessageKey,
    bool IsRetryable,
    string? DiagnosticCode = null);

public sealed record StoredCredentialDescriptor(
    string Reference,
    DateTimeOffset? LastWritten);

public sealed record AppSettings(
    int SchemaVersion,
    HotkeyGesture Hotkey,
    HotkeyGesture CancelHotkey,
    string MicrophoneId,
    LanguageMode LanguageMode,
    IReadOnlyList<string> LocaleHints,
    string ProviderId,
    bool TrimTranscript,
    bool RestoreClipboard,
    bool StartWithWindows,
    bool ShowOverlay,
    bool PlaySounds,
    bool HistoryEnabled,
    string? CredentialReference)
{
    public const int CurrentSchemaVersion = 1;

    public static AppSettings Default { get; } = new(
        CurrentSchemaVersion,
        new HotkeyGesture(
            new PhysicalKey(VirtualKey.Control, 0x1D, IsExtended: true, KeySide.Right),
            KeyModifiers.None),
        new HotkeyGesture(
            new PhysicalKey(VirtualKey.Escape, 0x01, IsExtended: false, KeySide.None),
            KeyModifiers.None),
        "default",
        LanguageMode.VietnameseEnglishMixed,
        new ReadOnlyCollection<string>(["vi-VN", "en-US"]),
        "openai-gpt-transcribe",
        TrimTranscript: true,
        RestoreClipboard: true,
        StartWithWindows: false,
        ShowOverlay: true,
        PlaySounds: false,
        HistoryEnabled: false,
        CredentialReference: "openai-api-key");
}

public sealed class SessionStateChangedEventArgs(
    DictationSessionState previous,
    DictationSessionState current,
    OperationError? error = null) : EventArgs
{
    public DictationSessionState Previous { get; } = previous;

    public DictationSessionState Current { get; } = current;

    public OperationError? Error { get; } = error;
}
