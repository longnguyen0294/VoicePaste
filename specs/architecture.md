# VoicePaste MVP Architecture

## 1. Purpose and scope

This document translates the VoicePaste SRS into an implementable architecture for the Phase 1 proof
of concept and Phase 2 MVP. It covers user-held push-to-talk dictation of unbounded elapsed duration
on 64-bit Windows 11 and the Windows 10 22H2 build 19045 compatibility baseline. It does not provide hands-free background transcription. Phase 3 and Phase 4
capabilities are excluded.

The design optimizes for:

- one reliable dictation session at a time;
- strict focus preservation and non-activating feedback;
- provider replaceability;
- deterministic cleanup of microphone, temporary audio, global input registration, and clipboard state;
- testability without requiring real Windows devices or a live provider in unit tests;
- privacy-safe defaults.

The implementation baseline is C# on .NET 10 LTS, targeting `net10.0-windows` with the SDK pinned by
`global.json`. WPF is intentionally selected for the tray-oriented desktop shell and mature Win32
interop. `VoicePaste.App` uses `System.Windows.Forms.NotifyIcon` behind an adapter for the MVP tray
icon; domain code remains independent of both WPF and Windows Forms. The first concrete provider is
the provisional `VoicePaste.Providers.OpenAI` adapter for `gpt-transcribe`; its selection remains
subject to the evidence gates in `provider-evaluation.md`.

## 2. Proposed solution structure

```text
VoicePaste.slnx
src/
├── VoicePaste.App/
│   ├── App.xaml(.cs)
│   ├── Composition/
│   └── UI/
├── VoicePaste.Core/
│   ├── Audio/
│   ├── Input/
│   ├── Insertion/
│   ├── State/
│   ├── Transcription/
│   └── Settings/
├── VoicePaste.Windows/
│   ├── Audio/
│   ├── Input/
│   ├── Insertion/
│   └── Infrastructure/
└── VoicePaste.Providers.OpenAI/
tests/
├── VoicePaste.Core.Tests/
├── VoicePaste.Windows.Tests/
└── VoicePaste.Providers.OpenAI.Tests/
```

Project boundaries are logical rather than mandatory file counts. Avoid splitting assemblies further
until a dependency or deployment boundary justifies it.

## 3. Dependency rules

```text
VoicePaste.App ───────────────┐
VoicePaste.Windows ───────────┼──> VoicePaste.Core
VoicePaste.Providers.* ───────┘
```

- `VoicePaste.Core` contains domain models, interfaces, state transitions, validation, and orchestration
  rules. It must not depend on WPF, Win32, a concrete audio library, or a concrete provider SDK.
- `VoicePaste.Windows` implements Windows-specific hotkey, foreground-window, audio, clipboard,
  simulated-input, credential, startup, and cleanup services.
- `VoicePaste.Providers.*` implements `ISpeechToTextProvider` and provider-specific error mapping.
- `VoicePaste.App` is the composition root and contains WPF views, tray integration, the non-activating
  overlay, and user-facing notifications.

## 4. Component responsibilities

| Component | Responsibility | Primary requirements |
|---|---|---|
| `ApplicationLifetimeCoordinator` | Start minimized, keep process alive after settings window closes, pause, and exit cleanly | FR-001–005 |
| `TrayIconController` | Open Settings, Pause, Exit, and expose current state | FR-003, FR-005 |
| `IGlobalHotkeyService` | Observe pass-through press/release through Raw Input, distinguish Right Ctrl, reconcile interruptions, and release unmanaged resources | FR-010–018, NFR-016 |
| `IForegroundWindowTracker` | Capture and validate the intended target window | FR-040, FR-048 |
| `IAudioCaptureService` | Enumerate microphones and stream user-held push-to-talk audio through bounded buffers to chunked temporary storage without an elapsed-time cutoff | FR-020–026, NFR-002, NFR-007, NFR-015 |
| `ISpeechToTextProvider` | Convert audio to Unicode text, including benchmarked Vietnamese-English code-switching, behind a replaceable abstraction | FR-030–039, NFR-003, NFR-006 |
| `ITextInsertionService` | Insert normalized text using clipboard plus simulated paste | FR-042–048 |
| `IClipboardService` | Snapshot clipboard, write transcript, detect ownership, and conditionally restore | FR-043–046, NFR-014 |
| `DictationSessionCoordinator` | Own the session lock, state machine, cancellation, cleanup, and recovery | FR-014, FR-050, NFR-011–013 |
| `StatusOverlayController` | Show listening/processing status without activation or Alt+Tab presence | FR-022, FR-041, FR-051–054 |
| `ISettingsStore` | Persist, validate, migrate, and reset non-secret settings | FR-060–065 |
| `ICredentialStore` | Store/read provider secrets and enumerate/delete VoicePaste-owned credential metadata with current-user Windows protection; list operations never return secret values | FR-062, NFR-024 |
| `ITemporaryAudioStore` | Create, lease, expire, and clean temporary recordings | FR-025, FR-036, NFR-012 |

## 5. Primary runtime flow

1. A hidden native message window receives Raw Input and the global hotkey service observes a valid
   physical press edge. It distinguishes extended Right Ctrl from Left Ctrl and does not suppress or
   synthesize the native Ctrl event seen by the foreground application.
2. The coordinator atomically acquires the single-session lease. If unavailable, the press is ignored
   or reported without starting another capture.
3. The foreground-window tracker captures the intended target before VoicePaste displays feedback.
4. The state changes from `Idle` to `Listening`; audio capture starts off the UI thread and the overlay
   displays without activation.
5. Capture continues while the accepted push-to-talk key remains held. Centrally pinned
   `NAudio.Wasapi` `3.0.1` provides WASAPI capture behind the Windows audio adapter. Audio callbacks
   feed a bounded channel and a single writer emits
   sequential chunks of at most five minutes into application-owned temporary storage rather than
   accumulating the recording in memory. A release edge stops capture; cancellation, the 300 ms
   minimum-duration rule, the 256 MiB free-space reserve, and device-error rules are evaluated.
   WASAPI shared-mode conversion normalizes capture to 16 kHz, 16-bit, mono PCM. Elapsed duration
   alone never stops or rejects the recording.
6. The state changes to `Transcribing`; the provisional OpenAI adapter retrieves the user's key from
   Windows Credential Manager and sends WAV audio to the `gpt-transcribe` transcription endpoint.
   It supplies `vi`/`en` hints for mixed mode, streams ordered segments capped at 24 MiB PCM (below
   the provider's 25 MiB file limit), carries a bounded prior-transcript tail as continuation context,
   and combines results in order. These request segments never become an application-level recording
   cutoff. Auth, quota, timeout, network, payload, response, and provider failures map to typed errors.
7. Empty or whitespace-only results are rejected. A valid result is normalized according to settings.
8. The state changes to `Pasting`; the insertion service revalidates the captured HWND, process ID,
   process start identity, integrity level, and current foreground window. If the original target is
   no longer valid and foreground, VoicePaste never activates it and instead retains the transcript
   for manual copy/retry.
9. When the target is eligible, the clipboard service safely materializes a supported-format snapshot
   no larger than 16 MiB, writes the transcript plus a private lease token, records clipboard sequence
   and owner identity, and sends the paste command. If a safe snapshot is impossible because content
   is delayed-rendered, unsupported, locked after bounded retries, or too large, automatic paste is
   skipped and the transcript remains available.
10. After a bounded paste-settle interval, clipboard restoration occurs only if the private token,
    sequence number, and owner identity still match. User or third-party clipboard changes always win.
11. Temporary audio and session-owned resources are released. On a retryable transcription failure,
    the audio lease may remain for at most 15 minutes; otherwise it is deleted. A transient `Success`,
    `Cancelled`, or `Error` state is exposed, then the coordinator returns to `Idle`.

## 6. State machine and concurrency

```text
Idle --press/acquire--> Listening --release--> Transcribing --valid text--> Pasting
 ^                          |                     |                         |
 |                          +--cancel/error-------+-----------error---------+
 |                                                                        |
 +---------- terminal cleanup <-- Success / Cancelled / Error <-----------+
```

Invariants:

- Exactly one session lease may exist.
- State transitions are serialized by the coordinator, not by individual UI controls.
- Rapid or repeated press/release events are debounced and cannot create a second capture or provider
  request.
- Every terminal path executes cleanup in a `finally`-equivalent boundary and returns to `Idle`.
- Pause prevents new sessions but does not abandon cleanup for an active session.
- Exit cancels active work, releases Raw Input registration and audio devices, and completes bounded cleanup.
- Windows lock, sleep, session change, raw-input device removal, or any event that makes the held-key
  state uncertain cancels capture and returns to `Idle`; the application never waits indefinitely for
  a release edge it can no longer trust.

## 7. Threading and cancellation

- WPF dispatcher work is limited to view-model and visual updates.
- Audio callbacks must not perform blocking disk, network, or UI operations.
- Provider calls, file cleanup, and clipboard retry delays are asynchronous and cancellable where the
  underlying platform allows it.
- Session cancellation is linked to cancel-hotkey, application exit, unrecoverable resource errors,
  and Windows session events; elapsed recording duration is not a cancellation source.
- Long captures use a bounded channel, sequential chunks of at most five minutes, and a single writer.
  A 256 MiB free-space reserve is checked before and during capture. Crossing it stops capture with a
  typed storage error and terminal cleanup; it is not an elapsed-time limit.
- Clipboard and Win32 calls that require a particular apartment/thread are isolated behind Windows
  adapters and marshalled deliberately.

## 8. Error model and recovery

Errors cross layer boundaries as typed categories rather than provider exception strings:

- `NoSpeech`
- `MicrophoneUnavailable`
- `MicrophonePermissionDenied`
- `NetworkUnavailable`
- `AuthenticationFailed`
- `QuotaExceeded`
- `TimedOut`
- `ProviderFailure`
- `ProviderPayloadTooLarge`
- `AudioEncodingFailed`
- `ClipboardBusy`
- `TargetUnavailable`
- `TargetPrivilegeMismatch`
- `StorageUnavailable`
- `StorageFull`
- `HotkeyInputUnavailable`
- `SessionInterrupted`
- `Cancelled`

The coordinator maps categories to concise notifications and recovery actions. Provider failure must
not terminate the process. Automatic paste failure retains the transcript for manual copy or retry.
Retryable transcription failure retains temporary audio for at most 15 minutes. Retry, cancel, expiry,
application exit, and next-start cleanup all release the lease deterministically.

## 9. Security and privacy

- Recording occurs only while an accepted push-to-talk session is active.
- Right Ctrl is the MVP default and must be distinguished from Left Ctrl. Raw Input observation is
  pass-through: VoicePaste does not suppress the physical key event delivered to the foreground app.
- Provider credentials are never stored in the settings JSON; only a credential reference may appear.
- The MVP uses a user-supplied provider credential stored in Windows Credential Manager for the
  current Windows user. A managed credential backend is outside MVP scope.
- Credential management enumerates only generic targets under `VoicePaste:*` and exposes reference
  plus last-written metadata. Settings supports case-insensitive search and confirmed deletion; it
  never displays or logs a credential blob.
- Default logs exclude raw audio, full transcripts, API keys, tokens, and clipboard payloads.
- Cloud providers must be disclosed in settings before audio is sent remotely.
- Analytics or crash reporting that could contain user content is disabled until explicitly opted in.
- Temporary audio is stored in an application-owned directory with restrictive user access and a
  deterministic expiry/cleanup policy.

## 10. Windows integration constraints

- The app targets `win-x64`. Windows 11 is primary; Windows 10 22H2 build 19045 is the explicit
  compatibility baseline and receives the same launch/capture/paste checks.
- The status overlay must use non-activating window styles, avoid task-switcher presence, remain usable
  at 100–200% scaling, and communicate state with more than color alone.
- VoicePaste cannot inject input into a higher-integrity target; it must detect/report that boundary.
- The clipboard is shared and contested. Access uses bounded retries, and restoration is conditional.
- Target-window identity is captured at recording start. Automatic paste is allowed only if that same
  identity remains valid and foreground; VoicePaste never calls focus/activation APIs to force it.
- Global press/release observation uses Raw Input on a hidden native message window. `RegisterHotKey`
  is unsuitable for hold/release semantics and side-aware Right Ctrl; a suppressing low-level hook is
  unnecessary for the accepted pass-through behavior.

## 11. Observability and performance

Record privacy-safe metrics for state durations, error categories, cleanup results, and resource use.
Do not record transcript or raw audio content. Verification targets are:

- listening feedback within 150 ms under normal load;
- for a 10-second recording, release-to-paste latency at most 5 seconds p50 and 8 seconds p95 under the
  recorded provider/region/network/hardware profile;
- Release-build private working set below 150 MB after warm-up and five minutes idle;
- no application-controlled WPF dispatcher operation longer than 100 ms during transcription;
- no more than 16 MiB capture-related memory overhead after stabilization during a synthetic 60-minute
  held-key run, with ordered frames and no elapsed-time stop;
- mixed-language quality at or above the gates in [provider-evaluation.md](provider-evaluation.md).

## 12. Open architecture decisions

OpenAI `gpt-transcribe` and remote processing are provisional implementation choices. Final provider
selection remains evidence-gated by [provider-evaluation.md](provider-evaluation.md), including live
mixed-language quality, privacy/retention, latency, long-pipeline, and cost checks. Installer/update
technology also remains unresolved. See [decision-log.md](decision-log.md).
