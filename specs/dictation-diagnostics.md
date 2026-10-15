# Repeated dictation failures: investigation and diagnostics

## Goal and evidence (2026-10-02)

The reported tray message, "VoicePaste could not complete this dictation. Try again.",
is a shared fallback for multiple error paths. It does not establish which stage failed.
The running executable was the 2026-09-27 build under `artifacts/verify`; the local
`provider.log` had no entries for the reported day's failures. Those older entries cannot
establish the cause of this incident. The user confirmed the error appears after hotkey release
and reopening the app restores service. That makes persistent in-process state/resources a
useful lead, without identifying a specific component. A live reproduction with the new build remains pending.

Code inspection found a recording-ownership defect: `CompleteAsync` checked/drained the
realtime pump before assigning a successful audio-stop result to the recording owned by its
`finally` block. A realtime failure during that interval could skip content disposal. This is
a confirmed code defect, not a confirmed explanation of the reported repeated notification.
Target/provider exceptions before `SessionContext` creation also lacked the coordinator's
startup recovery boundary. Swallowed cleanup exceptions lacked any actionable evidence.

## Implementation plan and decisions

1. Assign successful stopped audio immediately so drain failure/cancellation still disposes it.
2. Cover target/provider startup exceptions with the same cleanup and terminal-state boundary;
   classify caller cancellation correctly. Snapshot settings for each session.
3. Correlate session start, stages, state transitions, terminal errors, audio metadata, and
   individual cleanup results with a session ID and PID. Capture hotkey action context and the
   runtime/OS/core module ID at app startup. Log typed provider failures and underlying exceptions.
4. Route provider diagnostics into the shared asynchronous writer at
   `%LOCALAPPDATA%\VoicePaste\logs\application.log`. Keep one previous file, rotating at 1 MiB.
   The 1,024-entry queue never waits in capture/UI callers; overflow is counted in the next record.
   App shutdown waits at most two seconds for queued logs. Unwritable logs cannot fail dictation.
5. Give unexpected/start/cleanup failures distinct recovery messages explaining tray Exit,
   reopening, and the availability of local logs.

Default diagnostics include exception type, HResult, up to four inner exception levels,
up to twelve method/line frames per exception, and HTTP/socket error enums when available.
They exclude exception messages, `Data`, file paths, provider payloads/close descriptions,
raw audio, transcript text, clipboard payloads, credentials, and free-form diagnostic codes.
Event metadata is explicitly supplied by trusted code; never pass user text to this API.
The audio driver callback retains the originating diagnostic session ID across its native thread.

## Requirement and verification mapping

| Requirement | Production evidence | Automated evidence | Manual evidence |
|---|---|---|---|
| FR-025, NFR-011–012 | `DictationSessionCoordinator.CompleteAsync/CleanupAndFinalizeSessionAsync` own stopped content before realtime drain | `UtState013RealtimeFailureAfterSuccessfulStopDisposesReturnedRecording` | File inspection during real-device realtime failure pending |
| FR-035, FR-050, NFR-010–013 | `DictationSessionCoordinator.StartListeningAsync/SessionContext`; session settings snapshot and startup recovery | `UtState014RepeatedUnexpectedStopFailuresDoNotPoisonNextSession`; `UtState015UnexpectedRealtimeStartupFailureRecoversBeforeSessionOwnership`; existing concurrency/recovery tests | Reported repeated incident reproduction pending |
| FR-026, FR-054, NFR-023 | `WasapiAudioCaptureService` capture/stop/reset/disposal diagnostics; `TemporaryAudioCleanupService`; `WasapiMicrophoneCatalog`; `AppHost.ExecuteSafelyAsync/CreateAsync/DisposeAsync`; `AppStatusController.PublishAsync`; `OperationErrorPresenter.ToUserMessage` | `UtStatus003RepeatedFailureExplainsRestartAndDiagnostics`; existing capture-stop/provider/category tests | Real microphone/device interruption and app startup/shutdown log review pending |
| FR-043?047, NFR-014, NFR-023 | `WindowsClipboardService.PlaceTranscriptAsync/InvokeWithRetryAsync/RestoreIfOwnedAsync`; `StaDispatcher.InvokeAsync/Run`; `WindowsTextInsertionService.InsertAsync/RetainAndFail/TryDirectUnicodeInsertionAsync` log snapshot/write/retry/restore, dispatcher health and paste results | Existing clipboard snapshot/native-input/Unicode tests; Release build | Real clipboard contention, dispatcher interruption, and paste compatibility pending |
| FR-035, NFR-023 | `Providers.OpenAI/DiagnosticLog`; `OpenAiSpeechToTextProvider.TranscribeAsync/TranscribeSegmentAsync`; `OpenAiRealtimeSpeechToTextProvider.StartSessionAsync`; `OpenAiRealtimeSession.CompleteAsync/ReceiveLoopAsync/CancelAsync/DisposeCoreAsync` | Existing REST HTTP/error and realtime sanitized error/transport tests | Live provider failure correlation pending |
| NFR-023 | `SessionDiagnostics.BeginSession/Write/FormatExceptionMetadata/WriteLoopAsync`; `DiagnosticFileWriter.Append`; Core `Properties/AssemblyInfo.cs` grants test access only | `UtPriv002ExceptionDiagnosticsKeepFramesAndExcludeSensitiveContent`; `UtDiag001SessionCorrelationFlowsToTasksAndRestoresAfterScope`; `UtDiag002LogRotationKeepsOnlyCurrentAndPreviousFile` | New-build logs and deployed recording/privacy inspection pending |

Automated results: focused Core tests passed (24); Release solution build passed with zero
warnings/errors; full suite passed (73: Core 24, App 13, providers 17, Windows 19).
Self-contained `win-x64` publish passed. Final changes are verified using:

```powershell
dotnet build VoicePaste.slnx -c Release --artifacts-path artifacts/troubleshoot
dotnet test VoicePaste.slnx -c Release --artifacts-path artifacts/troubleshoot --no-build --no-restore
dotnet publish src/VoicePaste.App/VoicePaste.App.csproj -c Release -r win-x64 --self-contained true --artifacts-path artifacts/troubleshoot -o artifacts/publish/troubleshoot-win-x64
```

## Next incident

Use the new executable after exiting the existing tray instance. Note the failure's local time,
whether it happened on press/during capture/after release, and whether reopening restores service.
Preserve both `application.log` and `application.previous.log` promptly. Match the error's PID/session
to `session.start`, `session.stage`, `session.complete_exception`/`session.start_exception`,
`session.cleanup`, and the final `Next=Idle`. A false/timed-out cleanup followed by a new capture
failure supports a resource-recovery investigation; a provider HTTP/transport failure supports a
network/provider investigation. Missing/truncated logs do not prove that a stage succeeded.

Older `provider.log` files are retained unchanged, are not used by the new writer, and may have
been created by versions with less strict logging. Do not include them in a default support bundle.

## Plan: clipboard deserialization recovery (2026-10-06)

### Goal / Root cause

Local session logs at 23:15:28, 23:15:41, 23:16:40, and 23:16:48 (+07:00) show successful
HTTP 200 transcription followed by `System.PlatformNotSupportedException` during
`WindowsClipboardService.CaptureSnapshot`, with WPF `BinaryFormatter.Deserialize` frames.
The same failure recurred after app restart. Snapshot reading only caught COM/external/invalid
operation failures, allowing unsupported object deserialization to escape and skip the existing
direct Unicode/manual-copy recovery. Another session at 23:15:04 failed target validation;
that foreground protection remains required.

### Requirements and decisions

- FR-043–047: preserve the entire clipboard when a snapshot is unreadable; attempt direct Unicode
  insertion after revalidating the captured target; retain the transcript if fallback fails.
- FR-054: use the existing typed clipboard/target recovery messages.
- NFR-014: keep the five-attempt, 40 ms retry policy for contested clipboard acquisition/write.
- NFR-023: log only sanitized exception metadata, never format names or clipboard/transcript content.
- Keep the current .NET 9/WPF stack. No unsafe deserialization switch or new product behavior.

### Implementation and traceability change set

| Step / requirement | Production evidence | Test evidence | Durable mapping |
|---|---|---|---|
| 1 — FR-043, FR-045, FR-047, FR-054, NFR-023 | `WindowsClipboardService.CaptureSnapshot(IDataObject?)` maps unsupported/serialization and format enumeration failures to `clipboard.snapshot_unavailable` before any write; sanitized `clipboard.snapshot_read_failed` event | `WindowsClipboardServiceTests.UtClip004UnsupportedDeserializationRejectsTheWholeSnapshot`, `UtClip004FormatEnumerationFailureReturnsTypedClipboardError`, `UtClip004SafeAndEmptySnapshotsStillSucceed` | This section and `requirements-traceability.md` |
| 2 — FR-043, FR-044, FR-046–047, NFR-014 | Internal clipboard reader constructor isolates adapter regression tests; existing `PlaceTranscriptAsync/InvokeWithRetryAsync` and `WindowsTextInsertionService.InsertAsync/TryDirectUnicodeInsertionAsync/RetainAndFail` retain retry, foreground, and fallback behavior | `UtClip004RepeatedReadFailuresDoNotWriteOrPoisonTheDispatcher`; `UtInsert003UnreadableClipboardRevalidatesTargetAndRetainsTranscript`; existing Unicode/snapshot tests | This section and `test-plan.md` |

### Verification

- Windows adapter regression suite passed: 27 tests, including 8 new regression cases.
- Release solution build passed with zero warnings/errors; full suite passed: 81 tests
  (Core 24, App 13, providers 17, Windows 27).
- Isolated self-contained `win-x64` publish passed at `artifacts/publish/clipboard-fix-win-x64`.
- Requirement/symbol/test mapping reviewed against the scoped change; no SRS intent changed.
- Scoped `dotnet format --verify-no-changes --no-restore` passed for the changed adapter and tests.
- Live dictation into the user's target with the offending clipboard and full Windows/app
  compatibility matrix: pending; deterministic tests do not establish these manual results.

Commands used with the pinned .NET SDK 9.0.301:

```powershell
dotnet test tests/VoicePaste.Windows.Tests/VoicePaste.Windows.Tests.csproj -c Release --artifacts-path artifacts/clipboard-fix
dotnet build VoicePaste.slnx -c Release --artifacts-path artifacts/clipboard-fix
dotnet test VoicePaste.slnx -c Release --artifacts-path artifacts/clipboard-fix --no-build --no-restore
dotnet publish src/VoicePaste.App/VoicePaste.App.csproj -c Release -r win-x64 --self-contained true --artifacts-path artifacts/clipboard-fix -o artifacts/publish/clipboard-fix-win-x64
dotnet format VoicePaste.slnx --include src/VoicePaste.Windows/Insertion/WindowsClipboardService.cs tests/VoicePaste.Windows.Tests/WindowsClipboardServiceTests.cs --verify-no-changes --no-restore
```
