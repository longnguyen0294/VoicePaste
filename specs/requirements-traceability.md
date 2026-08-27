# VoicePaste MVP Requirements Traceability

## Usage

This is the durable requirement-to-work-to-code-to-test map. Every SRS `FR-*` and `NFR-*` has one
row. Production paths and dated results below describe the current implementation; catalog IDs without
an exact test symbol remain planned evidence, not proof of completion.

Status meanings:

- `Planned`: included in the current MVP plan; implementation evidence does not yet exist.
- `Pending decision`: planned behavior cannot be finalized until the referenced decision is resolved.
- `Deferred`: explicitly outside the current MVP.
- `Implemented`: code exists but required verification is incomplete.
- `Verified`: mapped code exists and all required automated/manual evidence has passed.

Replace planned evidence with exact paths and symbols as code lands. Never set `Verified` from a
document-only change.

## Functional requirements

| Requirement | Behavior / work item | Planned production evidence | Planned automated test evidence | Manual evidence | Status |
|---|---|---|---|---|---|
| FR-001 | VP-001/004/015 — .NET 10 x64 runtime on Windows 11 and Windows 10 22H2 build 19045 | `global.json`; `src/VoicePaste.App/VoicePaste.App.csproj`; `app.manifest`; self-contained `win-x64` publish | Release build and self-contained publish passed 2026-08-27 | MAN-WIN-001 pending | Implemented |
| FR-002 | VP-004 — Keep process alive after settings window closes | `src/VoicePaste.App/App.xaml(.cs)`; `Composition/AppHost.cs::Start/ShowSettings`; `UI/SettingsWindow.xaml.cs::OnClosing`; opt-in `--show-settings` debug launch | Release smoke passed 2026-08-26; Debug process/window smoke passed 2026-08-27 | MAN-APP-001 pending | Implemented |
| FR-003 | VP-004 — Tray actions Open Settings, Pause, Exit | `src/VoicePaste.App/UI/TrayIconController.cs`; `Composition/AppHost.cs`; `App.xaml.cs` debug launch argument | Release build passed; Debug Settings window smoke passed 2026-08-27; IT-LIFE-001 pending | MAN-APP-001 pending | Implemented |
| FR-004 | VP-015 — Optional start with Windows | `WindowsAutoStartService`; startup setting binding | Settings integration plus installer test | MAN-APP-001 after sign-in | Pending decision DEC-009 |
| FR-005 | VP-004 — Expose current state through tray | `src/VoicePaste.App/UI/TrayIconController.cs::UpdateState`; `Composition/AppStatusController.cs` | `DictationSessionCoordinatorTests.UtState001SuccessPathTransitionsAndReturnsToIdle` passed | MAN-APP-001 pending | Implemented |
| FR-010 | VP-005 — Detect hotkey outside VoicePaste | `src/VoicePaste.Windows/Input/RawInputHotkeyService.cs`; hidden message window with `RIDEV_INPUTSINK` | `RawInputKeyMapperTests.UtHotkey001DebouncesRepeatAndReleaseEdges` passed; input integration pending | MAN-FOCUS-001 pending | Implemented |
| FR-011 | VP-005/009 — Physical press starts and release stops capture | `RawInputHotkeyService`; `src/VoicePaste.Core/DictationSessionCoordinator.cs`; `AppHost` event wiring | Core state and Raw Input edge tests passed; IT-AUDIO-001 pending | MAN-NOTEPAD-001 pending | Implemented |
| FR-012 | VP-005/011/014 — Configure main hotkey | `HotkeyGesture`; settings view model; registration service | UT-HOTKEY-002; UT-SET-001 | MAN-A11Y-001 | Planned |
| FR-013 | VP-005/014 — Warn about unsafe bindings | `HotkeyBindingValidator` | UT-HOTKEY-002 | Settings exploratory check | Planned |
| FR-014 | VP-002/005/009 — Prevent overlapping sessions | `src/VoicePaste.Core/DictationSessionCoordinator.cs::_gate/_session` | `DictationSessionCoordinatorTests.UtState002RejectsOverlappingSession` passed | MAN-SESSION-001 pending | Implemented |
| FR-015 | VP-005/014 — Configurable cancel shortcut | `HotkeyGesture`; coordinator cancellation path | UT-HOTKEY-002; UT-STATE-001 | MAN-SESSION-001 | Planned |
| FR-016 | VP-004/005/013 — Release Raw Input registration and unmanaged resources | `AppHost.DisposeAsync`; `RawInputHotkeyService.Dispose`; `WasapiAudioCaptureService.DisposeAsync` | Build/smoke shutdown passed; IT-LIFE-001 pending | MAN-SESSION-001 pending | Implemented |
| FR-017 | VP-005/011 — Right Ctrl default distinct from Left Ctrl | `Models.cs::AppSettings.Default`; `RawInputKeyMapper` | `PolicyTests.UtHotkey003DefaultBindingIsExtendedRightControl`; `RawInputKeyMapperTests.UtHotkey003DistinguishesRightControlFromLeftControl` passed | MAN-PTT-001 pending | Implemented |
| FR-018 | VP-005 — Observe push-to-talk without suppressing native Right Ctrl | `RawInputHotkeyService` observes Raw Input only and has no suppression path | Raw Input mapping tests passed; pass-through integration pending | MAN-PTT-001 pending | Implemented |
| FR-020 | VP-006/009 — Capture selected microphone while held | `src/VoicePaste.Windows/Audio/WasapiAudioCaptureService.cs` (WASAPI to 16 kHz/16-bit/mono PCM); `src/VoicePaste.Core/DictationSessionCoordinator.cs` | Release build passed; IT-AUDIO-001 pending | MAN-NOTEPAD-001; MAN-DEVICE-001 pending | Implemented |
| FR-021 | VP-006/011/014 — Select available microphone | `src/VoicePaste.Windows/Audio/WasapiAudioCaptureService.cs::WasapiMicrophoneCatalog`; read-only list in `UI/SettingsWindow.xaml.cs` | No selection-persistence test; IT-AUDIO-001 pending | MAN-DEVICE-001 pending | Planned |
| FR-022 | VP-006/010 — Show listening state | `src/VoicePaste.App/UI/StatusOverlayWindow.xaml(.cs)`; `Composition/AppStatusController.cs` | Coordinator state test passed; UI integration pending | MAN-FOCUS-001 pending | Implemented |
| FR-023 | VP-006/009 — Continue capture while held with no duration cutoff; handle resource/provider limits safely | `WasapiAudioCaptureService`; bounded `ChunkedPcmSink`; `ChunkedAudioContent` | `PolicyTests.UtAudio001UsesThreeHundredMillisecondMinimumWithoutMaximum`; both `ChunkedAudioStorageTests` passed; IT-STT-004 pending | MAN-PTT-001; PERF-005 pending | Implemented |
| FR-024 | VP-006 — Ignore recordings below 300 ms | `src/VoicePaste.Core/Policies.cs::RecordingDurationPolicy`; coordinator status mapping | `PolicyTests.UtAudio001UsesThreeHundredMillisecondMinimumWithoutMaximum` passed | Short-tap behavior pending | Implemented |
| FR-025 | VP-006/013 — Delete temporary audio after terminal use or 15-minute retry expiry | `ChunkedAudioContent.DisposeAsync`; coordinator terminal cleanup; `TemporaryAudioCleanupService` | Coordinator cleanup tests passed; retry-lease/IT-CLEAN-001 pending | File-system cleanup inspection pending | Implemented |
| FR-026 | VP-006/013 — Recover from microphone errors | `WasapiAudioCaptureService` typed error mapping; coordinator terminal recovery | Coordinator cancel/terminal tests passed; IT-AUDIO-001 pending | MAN-DEVICE-001 pending | Implemented |
| FR-030 | VP-002/007/009 — Convert audio to Unicode text | `ISpeechToTextProvider`; `src/VoicePaste.Providers.OpenAI/OpenAiSpeechToTextProvider.cs`; `AppHost.CreateAsync` composition | OpenAI adapter's mocked success/segmentation test passed; IT-STT-002 live pending | MAN-NOTEPAD-001 pending | Implemented |
| FR-031 | VP-007/011 — Vietnamese and English recognition | OpenAI provider capabilities and `GetLanguageHints`; `AppSettings` language model | `OpenAiSpeechToTextProviderTests.UtStt004SegmentsWavInOrderAndCombinesMixedTranscript` passed; IT-STT-002 pending | MAN-NOTEPAD-001 with both languages pending | Implemented |
| FR-032 | VP-007/011 — Select neutral vi/en/mixed/automatic intent where supported | `LanguageMode`; locale hints; OpenAI `GetLanguageHints` capability mapping | provider mixed-hint test passed; editable selector/IT-STT-001 pending | Settings/provider check pending | Implemented |
| FR-033 | VP-002/007 — Replaceable provider abstraction | `src/VoicePaste.Core/Contracts.cs::ISpeechToTextProvider`; separate `VoicePaste.Providers.OpenAI` assembly and App composition boundary | Core/Windows builds do not depend on the provider assembly; Release solution build passed | Not required | Implemented |
| FR-034 | VP-007 — Implement at least one provider | `src/VoicePaste.Providers.OpenAI/OpenAiSpeechToTextProvider.cs` using OpenAI `gpt-transcribe` REST transcription | 9 mocked provider cases passed; IT-STT-002 live pending | Live provider smoke pending | Implemented |
| FR-035 | VP-002/007/012 — Distinguish provider failure categories | `ErrorCategory`; `OpenAiSpeechToTextProvider.MapHttpError`; coordinator/AppHost exception boundaries | auth/payload/quota/5xx/network provider cases passed; timeout/live fault injection pending | Recovery notification check pending | Implemented |
| FR-036 | VP-006/012 — Retry without rerecording within a 15-minute lease | `TemporaryAudioStore`; transcription retry command and expiry | UT-STT-003; IT-CLEAN-001 | Retry after simulated failure | Planned |
| FR-037 | VP-002/009 — Never paste empty transcript | `src/VoicePaste.Core/Policies.cs::TranscriptNormalizer`; coordinator insertion gate | `DictationSessionCoordinatorTests.UtStt002EmptyTranscriptNeverReachesInsertion`; `PolicyTests.UtStt002RejectsWhitespaceAndPreservesUnicode` passed | Empty/no-speech live check pending | Implemented |
| FR-038 | VP-002/011 — Optional trim | `TranscriptNormalizer`; `Models.cs::AppSettings.TrimTranscript` | `PolicyTests.UtStt002RejectsWhitespaceAndPreservesUnicode`; settings round-trip test passed | Settings behavior check pending | Implemented |
| FR-039 | VP-007/009/011 — Mixed Vietnamese-English recognition in one recording and pass evidence gate | `LanguageMode.VietnameseEnglishMixed`; `OpenAiSpeechToTextProvider` mixed hints and ordered segment combiner | `UtStt004SegmentsWavInOrderAndCombinesMixedTranscript` passed; IT-STT-002/003 pending | MAN-MIX-001; PERF-006 pending | Implemented |
| FR-040 | VP-005/008 — Capture target process identity at recording start | `WindowsForegroundWindowTracker.CaptureAsync`; `Models.cs::TargetWindow`; VoicePaste-owned windows are rejected as insertion targets | actionable self-target regression test passed; UT-TARGET-001/IT-PASTE-001 pending | MAN-FOCUS-001 pending | Implemented |
| FR-041 | VP-010 — Indicators never take focus | `src/VoicePaste.App/UI/StatusOverlayWindow.xaml.cs` applies non-activating/no-activate styles | Release smoke passed; UI/window-style integration pending | MAN-FOCUS-001 pending | Implemented |
| FR-042 | VP-008/009 — Insert only if captured target remains valid and foreground; never activate it | `WindowsTextInsertionService`; `WindowsForegroundWindowTracker.ValidateForPaste`; coordinator | Release build passed; UT-TARGET-001/IT-PASTE-001 pending | MAN-FOCUS-001 and app matrix pending | Implemented |
| FR-043 | VP-008 — Clipboard plus simulated paste | `src/VoicePaste.Windows/Insertion/WindowsClipboardService.cs`; `WindowsTextInsertionService.SendPasteShortcut` | `NativeInputLayoutTests.UtInsert001UsesNativeInputSizeForCurrentArchitecture` passed; IT-CLIP-001/IT-PASTE-001 pending | MAN-NOTEPAD-001 pending | Implemented |
| FR-044 | VP-008/009 — Preserve Unicode/diacritics | Unicode `string` transcript/insertion pipeline | `PolicyTests.UtStt002RejectsWhitespaceAndPreservesUnicode` passed; IT-PASTE-001 pending | MAN-NOTEPAD-001; MAN-IDE-001 pending | Implemented |
| FR-045 | VP-008 — Restore only a safe <=16 MiB materialized clipboard snapshot | `ClipboardSnapshotPolicy`; `WindowsClipboardService` conditional restore | Both `ClipboardSnapshotPolicyTests` passed; IT-CLIP-001 pending | MAN-CLIP-001 pending | Implemented |
| FR-046 | VP-008 — Do not overwrite later clipboard changes | `Models.cs::ClipboardLease`; `WindowsClipboardService.RestoreAsync` token/sequence/owner checks | Release build passed; UT-CLIP-001/IT-CLIP-001 pending | MAN-CLIP-001 pending | Implemented |
| FR-047 | VP-008/012 — Manual copy/retry when target or safe clipboard preconditions fail | `AppStatusController.Retain`; `SettingsWindow.SetRetainedTranscript` and Copy action; retry-paste command absent | Clipboard snapshot tests passed; UT-INSERT-001/IT-PASTE-001 pending | MAN-FOCUS-001; MAN-CLIP-001 pending | Planned |
| FR-048 | VP-005/008/012 — Report elevated target mismatch | `WindowsForegroundWindowTracker` integrity comparison; insertion/status error mapping | Release build passed; UT-TARGET-001/IT-PASTE-001 pending | MAN-ELEV-001 pending | Implemented |
| FR-050 | VP-002/009/010 — Expose all required states | `Models.cs::DictationSessionState`; coordinator; `AppStatusController` | all four `DictationSessionCoordinatorTests` passed | MAN-APP-001; MAN-SESSION-001 pending | Implemented |
| FR-051 | VP-010 — Compact edge overlay | `src/VoicePaste.App/UI/StatusOverlayWindow.xaml(.cs)` | Release smoke passed; UI placement integration pending | MAN-FOCUS-001; MAN-DPI-001 pending | Implemented |
| FR-052 | VP-010 — Overlay non-blocking and absent from Alt+Tab | `StatusOverlayWindow.xaml.cs` transparent hit-test and tool-window/no-activate styles | Release build passed; UI/window integration pending | MAN-FOCUS-001 pending | Implemented |
| FR-053 | VP-011/014 — Independent visual/audio feedback settings | feedback settings and controllers | UT-SET-001 | MAN-A11Y-001 | Planned |
| FR-054 | VP-002/010/012 — Reason plus recovery action | `AppStatusController` preserves typed terminal errors through `Idle`; `OperationErrorPresenter` supplies shared Settings/tray recovery copy without internal codes; retained-transcript copy action | all 4 `OperationErrorPresenterTests` passed; provider/coordinator error tests passed | MAN-DEVICE-001; MAN-ELEV-001 pending | Implemented |
| FR-060 | VP-011 — Persist settings locally | `src/VoicePaste.Windows/Infrastructure/JsonSettingsStore.cs`; atomic versioned JSON save/load | `JsonSettingsStoreTests.UtSet001RoundTripsValidatedSettingsWithoutSecretMaterial` passed; migration/reset tests pending | Restart persistence check pending | Implemented |
| FR-061 | VP-011/014 — Required settings fields without a maximum-duration setting | `AppSettings`; settings view model | UT-SET-001; IT-SET-001 | Settings UI review | Planned |
| FR-062 | VP-011 — Protect user-supplied keys and find/delete VoicePaste-owned entries without displaying secrets | `WindowsCredentialStore.SaveAsync/ReadAsync/ListAsync/DeleteAsync`; `StoredCredentialDescriptor`; `SettingsWindow` masked save plus default-collapsed metadata search/refresh/confirmed-delete `Expander`; only the active reference is in `AppSettings` | `CredentialListFilterTests.UtCred001FiltersNamesAndMarksTheActiveReferenceWithoutSecrets` and `WindowsCredentialStoreTests.ItCred001EnumeratesMetadataAndDeletesOnlyTheSelectedVoicePasteCredential` passed; broader log inspection pending | Published list/search and collapsed→expanded→collapsed UI Automation smoke passed 2026-08-27; confirmed deletion of the user's real key and cross-user check intentionally not exercised | Implemented |
| FR-063 | VP-006/014 — Microphone test | microphone test command and level/status view | IT-AUDIO-001 | Settings microphone check | Planned |
| FR-064 | VP-005/014 — Hotkey test and conflict detection | registration probe; binding validator | UT-HOTKEY-002 | Settings hotkey check | Planned |
| FR-065 | VP-011/014 — Reset settings | settings defaults and reset command | UT-SET-002; IT-SET-001 | Reset confirmation check | Planned |
| FR-070 | VP-011 — History disabled by default | `Models.cs::AppSettings.Default` sets false; no history service registered in `AppHost` | Settings round-trip/default tests passed; IT-SET-001 pending | Settings/default inspection pending | Implemented |
| FR-071 | VP-019 — Optional history operations | No MVP production evidence | No MVP test evidence | Post-MVP | Deferred DEC-010 |
| FR-072 | VP-019 — Clear transcript history | No MVP production evidence | No MVP test evidence | Post-MVP | Deferred DEC-010 |
| FR-073 | VP-006/013 — Never retain audio as history by default | `ChunkedAudioContent.DisposeAsync`; coordinator cleanup; no history audio model/service | Coordinator cleanup and chunk tests passed; IT-CLEAN-001 pending | File-system inspection pending | Implemented |

## Non-functional requirements

| Requirement | Behavior / work item | Planned production evidence | Planned automated test evidence | Manual evidence | Status |
|---|---|---|---|---|---|
| NFR-001 | VP-006/010/017 — Listening feedback latency | timestamp instrumentation around press/state/overlay | PERF-001 | Recorded performance run | Planned |
| NFR-002 | VP-006 — Audio capture never blocks UI and remains incrementally buffered | `WasapiAudioCaptureService` callback enqueues into bounded `ChunkedPcmSink`; one background writer | both `ChunkedAudioStorageTests` passed; IT-AUDIO-001/PERF-005 pending | MAN-PTT-001 pending | Implemented |
| NFR-003 | VP-007/009/017 — 10-second release-to-paste p50/p95 latency | provider duration plus planned end-to-end stage instrumentation | PERF-002 | Recorded pinned provider/region/network/hardware run | Planned |
| NFR-004 | VP-017 — Release private working set below 150 MB after warm-up/five-minute idle | release configuration and resource lifetime controls | PERF-003 | Recorded working-set run | Planned |
| NFR-005 | VP-009/017 — No application-controlled dispatcher block above 100 ms | async coordinator/provider and dispatcher instrumentation | PERF-004 | Responsiveness observation | Planned |
| NFR-006 | VP-007/017 — Mixed corpus WER/keyword/segment-completeness gate | OpenAI candidate adapter exists; frozen corpus manifest/runner absent | IT-STT-003; PERF-006 | Reviewed metric artifact | Planned |
| NFR-007 | VP-006/007/017 — 60-minute ordered capture with <=16 MiB stabilized overhead | bounded `ChunkedPcmSink`, ordered `ChunkedAudioContent`, and streaming OpenAI segment combiner | chunked audio tests and OpenAI ordered-segmentation test passed; IT-AUDIO-001/IT-STT-004/PERF-005 pending | MAN-PTT-001 pending | Implemented |
| NFR-010 | VP-002/007/012 — Provider failure cannot terminate app | typed OpenAI error mapping; coordinator and `AppHost.ExecuteSafelyAsync` exception boundaries | mocked auth/quota/payload/5xx/network cases passed; IT-STT-001 pending | Simulated/live provider outage pending | Implemented |
| NFR-011 | VP-002/009 — Terminal paths return to Idle | `DictationSessionCoordinator` terminal transition and `finally` cleanup | `UtState001SuccessPathTransitionsAndReturnsToIdle`; `UtState001CancelReleasesCaptureAndReturnsToIdle` passed | MAN-SESSION-001 pending | Implemented |
| NFR-012 | VP-006/012/013 — Cleanup normal, expired 15-minute retry, and stale temp files | coordinator/content cleanup; `TemporaryAudioCleanupService` startup sweep; retry lease absent | Coordinator cleanup tests passed; UT-STT-003/IT-CLEAN-001 pending | File-system inspection pending | Implemented |
| NFR-013 | VP-002/005/009 — No rapid-hotkey races | coordinator `SemaphoreSlim` session lease; `RawInputKeyMapper` debounce | `UtState002RejectsOverlappingSession`; `UtHotkey001DebouncesRepeatAndReleaseEdges` passed | MAN-SESSION-001 pending | Implemented |
| NFR-014 | VP-008 — Bounded clipboard retries | `WindowsClipboardService` five-attempt, 40 ms retry policy | Release build passed; UT-CLIP-002/IT-CLIP-001 pending | MAN-CLIP-001 pending | Implemented |
| NFR-015 | VP-006 — Chunked capture and 256 MiB free-space reserve | `AudioChunkPolicy`; `ChunkedPcmSink`; typed `StorageFull` cleanup boundary | both `ChunkedAudioStorageTests` passed; IT-AUDIO-001 pending | Storage-pressure inspection pending | Implemented |
| NFR-016 | VP-005/013 — Reconcile lost key release/session interruption | `RawInputHotkeyService` session/device/power hooks; coordinator cancellation wiring | Raw Input edge tests passed; input integration pending | MAN-SESSION-001 pending | Implemented |
| NFR-020 | VP-005/006/009 — Record only while push-to-talk held | Raw Input accepted press/release wired through `AppHost` to coordinator start/stop | state and Raw Input edge tests passed; IT-AUDIO-001 pending | MAN-SESSION-001 pending | Implemented |
| NFR-021 | VP-010/014 — Listening state visibly identifiable | text-bearing `StatusOverlayWindow`; `AppStatusController` state mapping | Coordinator state tests passed; UI/view-model test pending | MAN-FOCUS-001; MAN-A11Y-001 pending | Implemented |
| NFR-022 | VP-007/011/014 — Disclose local versus remote recognition | provider capability `SendsAudioOffDevice=true`; Settings remote-audio disclosure | Release build passed; settings view-model test pending | Full retention/region/deletion disclosure review pending | Implemented |
| NFR-023 | VP-011/013 — Privacy-safe logs | logging policy, redaction, safe structured fields | UT-PRIV-001; IT-CRED-001 | Log/artifact inspection | Planned |
| NFR-024 | VP-011 — Protect user-supplied provider credentials for current user | `WindowsCredentialStore` uses `CRED_TYPE_GENERIC`, current-user vault, zeroed temporary secret bytes, and metadata-only `VoicePaste:*` enumeration | current-user save/read/list/delete integration test passed without descriptor secret leakage; cross-user test pending | Cross-user protection and Windows vault inspection pending | Implemented |
| NFR-025 | VP-013 — Content-bearing analytics opt-in | telemetry gate/default-off configuration | UT-PRIV-001 | Configuration/artifact inspection | Planned |
| NFR-030 | VP-001/015/017 — Windows 11 x64 primary and Windows 10 22H2 build 19045 compatibility | `net10.0-windows10.0.19041.0`; `win-x64`; supported-OS manifest; self-contained publish | Release build and self-contained publish passed 2026-08-27 | MAN-WIN-001 pending | Implemented |
| NFR-031 | VP-016/017 — Compatibility app matrix | insertion abstractions plus release build | IT-PASTE-001 | MAN-NOTEPAD-001, MAN-WORD-001, MAN-TEAMS-001, MAN-BROWSER-001, MAN-IDE-001 | Planned |
| NFR-032 | VP-010/014/017 — Keyboard-accessible controls | WPF controls, labels, focus order, automation properties; key manager uses standard `Expander` with `ExpandCollapsePattern` | published key-manager Expand/Collapse UI Automation smoke passed; broader UI accessibility tests pending | MAN-A11Y-001 pending | Planned |
| NFR-033 | VP-010/017 — State not communicated by color alone | icon/text/sound-capable status presentation | UI/view-model test | MAN-A11Y-001 | Planned |
| NFR-034 | VP-010/017 — 100–200% display scaling | DPI-aware overlay placement and WPF layout | UI/window integration test | MAN-DPI-001 | Planned |

## Review gates

Before changing a row to `Implemented` or `Verified`, confirm:

1. The production path and symbol exist.
2. The mapped automated test exists and asserts the claimed behavior.
3. The recorded test result comes from the current relevant build.
4. Required manual evidence has actually been exercised on the stated Windows/app environment.
5. The mapping matches the final source diff and has not expanded Phase 3/4 scope.
