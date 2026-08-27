# VoicePaste — Software Requirements Specification

**Document version:** 1.3  
**Status:** Draft for MVP  
**Target platform:** 64-bit Windows 11; Windows 10 22H2 (build 19045) compatibility target  
**Suggested stack:** C# / .NET 10 LTS / WPF (`net10.0-windows`)

## 1. Product overview

VoicePaste is a lightweight Windows desktop application that runs in the background. The user places the cursor in any editable text field, holds a configurable global hotkey (the MVP default is **Right Ctrl**), speaks, and releases the hotkey. The application converts the recorded speech into text and automatically inserts it at the current cursor position.

The initial version focuses on user-controlled push-to-talk dictation rather than hands-free background meeting transcription. VoicePaste does not impose a fixed recording-duration limit while the push-to-talk key remains held.

## 2. Goals

- Allow users to enter text by voice in most Windows applications.
- Minimize interaction: hold to record, release to transcribe and paste.
- Support Vietnamese, English, and mixed Vietnamese-English dictation within one recording.
- Provide clear feedback without interrupting the active application.
- Protect user privacy and avoid retaining audio unnecessarily.
- Provide a foundation for local and cloud speech-to-text engines.

## 3. Non-goals for MVP

- Hands-free continuous or background meeting transcription that does not require holding the push-to-talk key.
- Speaker identification.
- Translation between languages.
- Voice-controlled computer automation.
- Full document editing through voice commands.
- Mobile or macOS support.
- User accounts, subscription, or cloud synchronization.
- Generative-AI rewriting, summarization, or grammar correction.

## 4. Target users

- Users who frequently type messages, emails, notes, or documents.
- Developers who want to dictate technical messages and documentation.
- Vietnamese-English bilingual users.
- Users who want faster text entry or reduced keyboard usage.

## 5. Primary user flow

1. The user starts VoicePaste.
2. VoicePaste minimizes to the system tray.
3. The user focuses an editable field in another application.
4. The user presses and holds the configured global hotkey.
5. VoicePaste begins recording and displays a non-focus-stealing listening indicator.
6. The user speaks.
7. The user releases the hotkey.
8. VoicePaste stops recording and transcribes the audio.
9. If the target captured at recording start is still valid and foreground, VoicePaste places the transcript on the clipboard and sends a paste command to that target; otherwise it keeps the transcript for manual copy or retry-paste.
10. VoicePaste restores the previous clipboard content when safe to do so.

## 6. Functional requirements

### 6.1 Application lifecycle

| ID | Requirement | Priority |
|---|---|---|
| FR-001 | The application shall run as a 64-bit application on Windows 11 and on the Windows 10 22H2 compatibility baseline (build 19045). | Must |
| FR-002 | The application shall continue running when its main window is closed, unless the user selects **Exit**. | Must |
| FR-003 | The application shall display a system-tray icon with **Open Settings**, **Pause**, and **Exit** actions. | Must |
| FR-004 | The application shall optionally start automatically when the user signs in to Windows. | Should |
| FR-005 | The application shall expose its current state through the tray icon or tooltip. | Should |

### 6.2 Global push-to-talk hotkey

| ID | Requirement | Priority |
|---|---|---|
| FR-010 | The application shall detect the configured hotkey while another application is active. | Must |
| FR-011 | Pressing the hotkey shall start recording; releasing it shall stop recording. | Must |
| FR-012 | The user shall be able to configure the hotkey. | Must |
| FR-013 | The application shall reject or warn about unsafe bindings such as a common letter key without modifiers. | Should |
| FR-014 | The application shall prevent multiple overlapping recording sessions. | Must |
| FR-015 | The application shall provide a configurable cancel shortcut that discards the current recording. | Should |
| FR-016 | The application shall unregister global input and release all other unmanaged resources when it exits. | Must |
| FR-017 | The MVP default push-to-talk binding shall be the **Right Ctrl** key, distinguishable from **Left Ctrl**; the user may replace it through hotkey configuration. | Must |
| FR-018 | VoicePaste shall observe the push-to-talk key without suppressing its native key events; the foreground application shall continue receiving Right Ctrl press/release events. | Must |

### 6.3 Audio capture

| ID | Requirement | Priority |
|---|---|---|
| FR-020 | The application shall record audio from the selected microphone while the hotkey is held. | Must |
| FR-021 | The user shall be able to select an available microphone. | Must |
| FR-022 | The application shall show a clear listening state while recording. | Must |
| FR-023 | The application shall continue capturing audio for as long as the push-to-talk key remains held, without an application-imposed maximum duration. Capture shall stop only when the key is released, the user cancels, the application exits, or an unrecoverable audio/system error occurs; the transcription pipeline shall not reject a recording solely because of its duration. | Must |
| FR-024 | Recordings shorter than the minimum usable duration shall be ignored with a non-blocking notification. | Should |
| FR-025 | Temporary audio shall be deleted after transcription or cancellation unless diagnostic retention is explicitly enabled. | Must |
| FR-026 | The application shall handle microphone disconnection and permission errors without crashing. | Must |

### 6.4 Speech-to-text

| ID | Requirement | Priority |
|---|---|---|
| FR-030 | The application shall convert recorded speech into Unicode text through a speech-to-text provider. | Must |
| FR-031 | The application shall support Vietnamese and English recognition. | Must |
| FR-032 | The user shall be able to select Vietnamese, English, or automatic language detection when supported by the provider. | Should |
| FR-033 | Speech-to-text providers shall be accessed through an abstraction so the engine can be replaced without changing the UI or input pipeline. | Must |
| FR-034 | The MVP shall implement at least one speech-to-text provider. | Must |
| FR-035 | The application shall distinguish no-speech, network, authentication, quota, timeout, and provider errors where possible. | Must |
| FR-036 | If transcription fails, the application shall allow the user to retry without rerecording while temporary audio remains available. | Should |
| FR-037 | The application shall not paste an empty transcript. | Must |
| FR-038 | The application shall optionally remove leading/trailing whitespace before insertion. | Should |
| FR-039 | The application shall recognize Vietnamese-English code-switching within a single recording without requiring the user to change the language setting between Vietnamese and English segments, and the selected MVP provider pipeline shall pass the mixed-language acceptance gate in `specs/provider-evaluation.md`. | Must |

### 6.5 Text insertion

| ID | Requirement | Priority |
|---|---|---|
| FR-040 | The application shall capture the intended target window and process identity at recording start, before displaying any UI that might change focus. | Must |
| FR-041 | The listening and processing indicators shall not take keyboard focus. | Must |
| FR-042 | After successful transcription, the application shall insert the result only when the target captured at recording start is still valid and foreground. VoicePaste shall never activate or steal focus from the target to force automatic paste. | Must |
| FR-043 | The primary insertion mechanism shall use the Windows clipboard and a simulated paste command. | Must |
| FR-044 | The application shall preserve Unicode characters and Vietnamese diacritics. | Must |
| FR-045 | The application shall attempt to restore a safely materialized previous clipboard snapshot after paste completion. | Should |
| FR-046 | Clipboard restoration shall occur only while VoicePaste still owns the write, verified by a private clipboard token, sequence number, and owner identity; it shall never overwrite a later external clipboard change. | Must |
| FR-047 | If the target is no longer foreground, clipboard content cannot be safely snapshotted, or automatic paste otherwise fails, VoicePaste shall not force insertion and the transcript shall remain available for manual copy and retry-paste. | Must |
| FR-048 | The application shall notify the user when it cannot inject input into a higher-privilege target application. | Must |

### 6.6 Status and notifications

| ID | Requirement | Priority |
|---|---|---|
| FR-050 | The application shall expose the states **Idle**, **Listening**, **Transcribing**, **Pasting**, **Success**, **Cancelled**, and **Error**. | Must |
| FR-051 | The application shall display a compact overlay near a screen edge during recording and processing. | Should |
| FR-052 | The overlay shall not block interaction with the active application or appear in Alt+Tab. | Should |
| FR-053 | The user shall be able to disable visual or audio feedback independently. | Could |
| FR-054 | Error notifications shall contain a concise reason and a recovery action when one is available. | Must |

### 6.7 Settings

| ID | Requirement | Priority |
|---|---|---|
| FR-060 | The application shall persist settings locally between sessions. | Must |
| FR-061 | Settings shall include hotkey, microphone, language mode, speech provider, startup behavior, and clipboard restoration. | Must |
| FR-062 | Provider credentials shall be stored through Windows-protected credential storage and shall not be stored as plaintext in configuration files. Settings shall let the user find and delete VoicePaste-owned credential entries without displaying their secret values. | Must |
| FR-063 | The settings screen shall provide a microphone test. | Should |
| FR-064 | The settings screen shall provide a hotkey test and detect conflicts where possible. | Should |
| FR-065 | The user shall be able to reset settings to defaults. | Should |

### 6.8 Optional local history

| ID | Requirement | Priority |
|---|---|---|
| FR-070 | Transcript history shall be disabled by default in the MVP. | Must |
| FR-071 | If enabled, the user shall be able to view, copy, retry-paste, and delete recent transcripts. | Could |
| FR-072 | The user shall be able to clear all transcript history. | Could |
| FR-073 | Audio shall not be retained as part of transcript history by default. | Must |

## 7. Non-functional requirements

### 7.1 Performance

| ID | Requirement |
|---|---|
| NFR-001 | Recording feedback should appear within 150 ms of hotkey press under normal system load. |
| NFR-002 | Audio capture shall not block the UI thread. |
| NFR-003 | For a 10-second recording, elapsed time from hotkey release to completed paste shall be at most 5 seconds at p50 and 8 seconds at p95 under the documented release-build provider, region, hardware, and broadband benchmark profile. |
| NFR-004 | After warm-up and five minutes idle, the Release build private working set shall be less than 150 MB on the documented benchmark machine. |
| NFR-005 | The application shall remain responsive while transcription is in progress; no application-controlled operation shall block the WPF dispatcher for more than 100 ms. |
| NFR-006 | On the versioned mixed Vietnamese-English acceptance corpus, the selected provider pipeline shall achieve normalized word error rate at most 20%, English keyword recall at least 90%, and omit no complete reference-language segment. |
| NFR-007 | During a synthetic 60-minute held-key capture, capture-related memory overhead after stabilization shall remain within 16 MiB, frames shall remain ordered without duration-triggered loss, and elapsed duration alone shall not stop capture. |

### 7.2 Reliability

| ID | Requirement |
|---|---|
| NFR-010 | A provider failure shall not terminate the application. |
| NFR-011 | The state machine shall return to **Idle** after success, cancellation, timeout, or error. |
| NFR-012 | Temporary files shall be cleaned after normal completion and on the next startup following an unexpected termination. |
| NFR-013 | Rapid hotkey presses shall not create concurrent capture or transcription races. |
| NFR-014 | Clipboard access shall use bounded retries because it may be temporarily locked by another process. |
| NFR-015 | Audio shall be written incrementally to bounded chunks. If free temporary-storage space falls below a 256 MiB reserve, capture shall stop safely with a typed storage error, preserve process stability, and execute cleanup; this resource failure is not a duration cutoff. |
| NFR-016 | If a key-release event is lost because of lock, sleep, session change, device removal, or equivalent interruption, VoicePaste shall stop/cancel capture safely and return to `Idle` rather than record indefinitely. |

### 7.3 Security and privacy

| ID | Requirement |
|---|---|
| NFR-020 | The application shall record only while the configured push-to-talk hotkey is actively held. |
| NFR-021 | The listening state shall always be visibly identifiable unless the user explicitly disables visual feedback. |
| NFR-022 | The application shall disclose whether recognition occurs locally or audio is sent to a remote provider. |
| NFR-023 | Logs shall not contain raw audio, full transcripts, API keys, or access tokens by default. |
| NFR-024 | Provider credentials shall be encrypted at rest for the current Windows user. |
| NFR-025 | Analytics and crash reporting shall be opt-in if they may include user content. |

### 7.4 Compatibility and accessibility

| ID | Requirement |
|---|---|
| NFR-030 | Windows 11 x64 is the primary supported platform. Windows 10 22H2 x64 build 19045 is a compatibility target and shall receive the same MVP launch, capture, and paste verification, while OS servicing availability remains Microsoft's responsibility. |
| NFR-031 | Core dictation shall be tested in Notepad, Microsoft Word, Microsoft Teams, major Chromium browsers, and Visual Studio or VS Code. |
| NFR-032 | All UI controls shall be keyboard accessible. |
| NFR-033 | Status must not be communicated by color alone. |
| NFR-034 | The application shall support Windows display scaling from 100% to 200%. |

## 8. Proposed architecture

```text
VoicePaste.App
├── UI
│   ├── System tray
│   ├── Settings window
│   └── Non-activating status overlay
├── Input
│   ├── Pass-through Raw Input hotkey
│   └── Foreground-window tracking
├── Audio
│   ├── Microphone discovery
│   └── Audio capture
├── Transcription
│   ├── ISpeechToTextProvider
│   ├── SelectedSpeechProviderAdapter
│   └── Ordered segment/combination policy
├── Insertion
│   ├── Clipboard manager
│   └── SendInput paste service
├── State
│   └── Dictation session state machine
└── Infrastructure
    ├── Settings
    ├── Credential protection
    ├── Logging
    └── Temporary-file cleanup
```

### 8.1 Core interfaces

```csharp
public interface IAudioCaptureService
{
    Task StartAsync(CancellationToken cancellationToken);
    Task<AudioRecording> StopAsync(CancellationToken cancellationToken);
    Task CancelAsync();
}

public interface ISpeechToTextProvider
{
    Task<OperationResult<TranscriptionOutput>> TranscribeAsync(
        AudioRecording audio,
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
```

### 8.2 Session state model

```text
Idle -> Listening -> Transcribing -> Pasting -> Idle
  ^          |              |            |
  |          +-> Cancelled -+            |
  +---------------- Error <--------------+
```

Only one dictation session may be active at a time. Every terminal path must release audio resources and return the application to `Idle`.

## 9. Data model

### 9.1 Application settings

```json
{
  "hotkey": "RightControl",
  "cancelHotkey": "Escape",
  "microphoneId": "default",
  "language": "vi-en-mixed",
  "provider": "configured-provider",
  "restoreClipboard": true,
  "startWithWindows": false,
  "showOverlay": true,
  "playSounds": false,
  "historyEnabled": false
}
```

API keys or tokens must not be stored in this settings document.

### 9.2 Transcription result

```csharp
public sealed record TranscriptionOutput(
    string Text,
    string? DetectedLanguage,
    TimeSpan ProcessingTime);

public abstract record OperationResult<T>;
public sealed record OperationSuccess<T>(T Value) : OperationResult<T>;
public sealed record OperationFailure<T>(OperationError Error) : OperationResult<T>;
```

## 10. Edge cases

- The hotkey is pressed while VoicePaste is already transcribing.
- The user taps instead of holding the hotkey.
- The user holds the push-to-talk key for a long period and available temporary-storage space becomes low.
- The microphone is unavailable, muted, removed, or used exclusively by another application.
- No speech is detected.
- The user changes the focused window during transcription.
- The target textbox no longer exists when transcription finishes.
- The target application runs as Administrator while VoicePaste does not.
- The clipboard is locked.
- The clipboard contains an image, file list, rich text, or a large object before insertion.
- The user copies something else while VoicePaste is transcribing or pasting.
- The network disconnects or the provider times out.
- The provider returns an empty or whitespace-only transcript.
- Vietnamese characters are pasted into an application with limited Unicode support.
- Windows locks or sleeps during a recording.
- VoicePaste exits or crashes while the microphone is active.

## 11. MVP acceptance criteria

The MVP is considered complete when all the following are true:

1. VoicePaste launches and remains available from the Windows system tray.
2. A user can choose a microphone, recognition language, and global hotkey.
3. Holding **Right Ctrl** by default starts recording, recording continues without a fixed application timeout while the key remains held, and releasing it stops recording.
4. The status indicator never steals focus from the target application.
5. Vietnamese, English, and mixed Vietnamese-English speech within one recording can be transcribed through at least one provider without manually switching language between segments, and that pipeline passes `NFR-006` on the versioned corpus.
6. The resulting Unicode text is pasted at the active cursor position in Notepad, Word, Teams, Chrome/Edge, and Visual Studio or VS Code only while the target captured at recording start remains foreground; otherwise a manual copy/retry path is provided without focus theft.
7. Empty or failed transcriptions are not pasted.
8. The application recovers from microphone, network, provider, and clipboard errors without crashing.
9. Temporary audio is deleted after completion, cancellation, or expiry.
10. API credentials are not stored in plaintext or written to logs.
11. The app prevents overlapping dictation sessions.
12. When automatic paste fails, the user can manually copy or retry the transcript.
13. A synthetic 60-minute capture and a manual 10-minute microphone hold satisfy `NFR-007` without an elapsed-time stop.
14. Unsafe clipboard snapshots and external clipboard mutations preserve the user's clipboard and fall back without data loss.

## 12. Testing requirements

### 12.1 Unit tests

- Dictation-session state transitions.
- Raw Input press/release debouncing, Right/Left Ctrl identity, pass-through behavior, and interrupted held-key reconciliation.
- Minimum-duration validation and verification that recording is not stopped by a fixed elapsed-time limit.
- Speech-provider error mapping.
- Empty transcript handling.
- Text normalization.
- Clipboard safe-snapshot gate and private token/sequence/owner restoration check.
- Settings validation and migration.

### 12.2 Integration tests

- Audio capture using a test audio source, including ordered synthetic 60-minute input, bounded memory/chunks, and storage reserve failure.
- Speech provider using mocked HTTP responses and a provider test environment, including the frozen mixed Vietnamese-English corpus and long-input segmentation.
- Clipboard write and conditional restoration.
- Paste into a controlled test window.
- Startup cleanup of expired temporary audio.

### 12.3 Manual compatibility tests

- Notepad.
- Microsoft Word.
- Microsoft Teams.
- Chrome and Edge text fields.
- Visual Studio and VS Code editors.
- An application running with elevated privileges.
- Multiple monitors and Windows display scaling.
- Vietnamese and English keyboard layouts.
- Mixed Vietnamese-English utterances within the same recording.
- A 10-minute real-microphone Right Ctrl hold and interrupted release through lock/sleep/session change.
- Windows 11 x64 and Windows 10 22H2 x64 build 19045 compatibility baselines.

## 13. Delivery phases

### Phase 1 — Technical proof of concept

- System-tray application.
- Fixed **Right Ctrl** push-to-talk hotkey with no application-imposed recording-duration limit.
- Microphone capture.
- One speech-to-text provider.
- Clipboard paste into Notepad.

### Phase 2 — MVP

- Configurable hotkey and microphone.
- Vietnamese, English, and mixed Vietnamese-English recognition.
- Non-activating status overlay.
- Robust clipboard restoration.
- Error recovery and retry.
- Secure credential storage.
- Installer and start-with-Windows option.

### Phase 3 — Enhanced version

- Local Whisper provider.
- Automatic language detection.
- Optional transcript history.
- Custom vocabulary for names and technical terms.
- Spoken punctuation and commands such as “new line”.
- Streaming or lower-latency transcription.

### Phase 4 — AI-assisted writing

- Optional grammar cleanup before insertion.
- Tone and formatting presets.
- Technical dictation mode for code and developer terminology.
- Translation and summarization modes.

These AI-assisted transformations must be optional and visually distinguishable from literal transcription.

## 14. Remaining implementation decisions

Before the provider and release-package slices begin, confirm:

1. Which speech-to-text provider will be used for the MVP. Selection is evidence-based and must pass `specs/provider-evaluation.md`, including mixed-language quality, long-recording segmentation, latency, privacy, retention, and cost gates.
2. Whether the initial product prioritizes cloud accuracy or offline privacy; this must align with the selected provider and disclosure requirements.
3. Which installer/update technology will be used for release packaging.

The MVP uses user-supplied provider credentials protected by Windows Credential Manager, binds paste to the window active at recording start without reactivating it, and restores the clipboard only when lease ownership can be verified.

## 15. Suggested MVP defaults

| Setting | Default |
|---|---|
| Hotkey | `Right Ctrl` |
| Cancel key | `Escape` |
| Language | Mixed Vietnamese-English |
| Minimum usable recording | 300 ms |
| Maximum recording | No application-imposed limit while push-to-talk remains held |
| Long-capture storage reserve | 256 MiB free temporary storage |
| Target window | Window active at recording start; auto-paste only if still foreground |
| Clipboard restoration | Enabled only for a safe snapshot with token/sequence/owner validation |
| Failed-transcription retry audio | Retain for at most 15 minutes, then delete |
| Provider credential delivery | User-supplied key in Windows Credential Manager |
| Transcript history | Disabled |
| Audio retention | Disabled |
| Start with Windows | Disabled |
| Status overlay | Enabled |
