# VoicePaste MVP Data Model

## 1. Modeling rules

- Domain models live in `VoicePaste.Core` and are independent of WPF, Win32, audio libraries, and
  provider SDKs.
- Records crossing asynchronous boundaries are immutable where practical.
- Secrets, raw clipboard payloads, and full transcripts are never included in default diagnostic
  serialization.
- Persisted settings carry a schema version and migrate forward without silently discarding valid
  user choices.

## 2. Session state

```csharp
public enum DictationSessionState
{
    Idle,
    Listening,
    Transcribing,
    Pasting,
    Success,
    Cancelled,
    Error
}
```

Only the session coordinator changes state. `Success`, `Cancelled`, and `Error` are observable terminal
states followed by deterministic cleanup and transition to `Idle`.

## 3. Application settings

```csharp
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
    string? CredentialReference);
```

MVP defaults:

| Field | Default |
|---|---|
| `SchemaVersion` | First implemented schema version |
| `Hotkey` | `Right Ctrl` |
| `CancelHotkey` | `Escape` |
| `MicrophoneId` | System default device |
| `LanguageMode` | `VietnameseEnglishMixed` |
| `LocaleHints` | `vi-VN`, `en-US` |
| `RestoreClipboard` | `true`, ownership check required |
| `StartWithWindows` | `false` |
| `ShowOverlay` | `true` |
| `PlaySounds` | `false` |
| `HistoryEnabled` | `false` |

There is no maximum-recording-duration setting: capture continues while push-to-talk remains held.
The minimum usable duration is 300 ms and is an application policy, not a persisted user duration
limit. `ProviderId` identifies the selected adapter and `TrimTranscript` defaults to `true`.
`CredentialReference` identifies an entry in Windows Credential Manager; it is never the credential
itself. Provider-specific locale values are derived inside the provider adapter from the neutral
`LanguageMode` and ordered locale hints.

Credential management uses metadata-only descriptors:

```csharp
public sealed record StoredCredentialDescriptor(
    string Reference,
    DateTimeOffset? LastWritten);
```

Enumeration is restricted to generic Windows credentials whose target begins with `VoicePaste:`.
The descriptor and Settings list never contain the credential blob, username, or secret preview.

## 4. Input and target models

```csharp
public enum KeySide
{
    None,
    Left,
    Right
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
```

Hotkey validation rejects empty bindings, reserved/unsupported combinations, collisions between main
and cancel gestures, and unsafe unmodified common character keys. Scan code, extended-key flag, and
side are retained so Raw Input maps default Right Ctrl independently of Left Ctrl. The accepted key is
pass-through and is never suppressed by VoicePaste.

`TargetWindow` is captured at recording start. Handle, process ID/start identity, integrity level, and
foreground ownership must all be revalidated before paste. A stale, non-foreground, or
higher-integrity target produces a typed failure and manual-copy/retry fallback; the application does
not activate the target.

## 5. Audio models

```csharp
public sealed record MicrophoneDevice(
    string Id,
    string DisplayName,
    bool IsDefault,
    bool IsAvailable);

public interface IAudioContent
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
```

`AudioRecording` represents an application-owned temporary resource, not permanent history. Files,
chunk manifests, and paths remain implementation details of `VoicePaste.Windows` and must not cross
the core/provider boundary or be logged. A recording has one active lease; cleanup occurs after
success, cancellation, terminal failure/expiry, or startup recovery after an abnormal exit.
Capture writes sequential chunks of at most five minutes through a bounded channel, keeps at least
256 MiB of free temporary storage, and has no duration-derived expiry while its session is active.
`ExpiresAt` applies only after capture ends; a retryable transcription failure sets it to no later than
15 minutes after failure.

## 6. Transcription models

```csharp
public enum LanguageMode
{
    Vietnamese,
    English,
    VietnameseEnglishMixed,
    Automatic
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

public abstract record OperationResult<T>;

public sealed record OperationSuccess<T>(T Value) : OperationResult<T>;

public sealed record OperationFailure<T>(OperationError Error) : OperationResult<T>;
```

Invariants:

- An operation is exactly one of `OperationSuccess<T>` or `OperationFailure<T>`; invalid boolean/null
  combinations are not representable.
- A successful but empty/whitespace-only `Text` is converted to an `OperationFailure` with `NoSpeech`
  before insertion.
- `Text` remains Unicode and preserves Vietnamese diacritics.
- `LanguageMode.VietnameseEnglishMixed` expresses provider-neutral user intent and accepts
  code-switching within one recording without changing options
  between segments. Provider-side or application-side segmentation must preserve segment order and
  enough context to avoid dropping language transitions.
- Provider-native codes may be retained in a privacy-safe diagnostic field, but application behavior
  is driven by the normalized error category.

## 7. Insertion and clipboard models

```csharp
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
```

Insertion returns `OperationResult<InsertionOutcome>`. The clipboard service may retain an in-memory
snapshot only for safely materialized supported formats up to 16 MiB. Delayed-rendered, unsupported,
larger, or persistently locked content is never overwritten; the insertion service may instead send
direct Unicode input to the already validated foreground target. If direct input is unavailable or
fails, the transcript is retained for manual copy/retry. The snapshot is session-scoped, never logged,
and released after restore or expiry.
Restoration is allowed only when the private token, current sequence number, and owner window all
match the lease and prove that no later user or application write occurred.

## 8. Error model

```csharp
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
    Cancelled
}

public sealed record OperationError(
    ErrorCategory Category,
    string UserMessageKey,
    bool IsRetryable,
    string? DiagnosticCode);
```

User-facing messages are localized/presentational concerns. `DiagnosticCode` must not contain secrets,
transcripts, raw audio, or remote response bodies containing user content.

## 9. Optional history

Transcript history is disabled for MVP by default. `FR-071` and `FR-072` are deferred, so no persistent
history entity or database is part of the initial model. Audio is never part of transcript history by
default (`FR-073`). Adding history later requires a separate retention, encryption, deletion, and
migration decision.
