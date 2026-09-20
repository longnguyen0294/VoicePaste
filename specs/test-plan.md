# VoicePaste MVP Test Plan

## 1. Strategy

Testing is layered so deterministic domain behavior runs without Windows devices or network access,
while platform integration is exercised on Windows with controlled adapters. A test result is recorded
only after execution. Catalog entries remain planned unless they are named in the current evidence
record below or have exact executed evidence in `requirements-traceability.md`.

### 1.1 Current evidence — 2026-08-27

- Release solution build: passed with 0 warnings and 0 errors.
- Microsoft Testing Platform suite: 32/32 passed (8 Core, 10 Windows adapter/integration tests, 9
  mocked OpenAI adapter cases, and 5 App policy/error-presentation cases).
- OpenAI adapter tests passed for credential gating, PCM/WAV validation, mixed `vi`/`en` hints,
  ordered segmentation/combination, continuation context, HTTP error mapping, and credential-safe
  network failure mapping. These are mocked HTTP results, not live speech-quality evidence.
- Credential management integration passed for an application-owned random credential: save/read,
  metadata-only enumerate, selected delete, post-delete absence, and `finally` cleanup. The published
  Settings UI listed the existing active reference and case-insensitive search passed; the user's real
  key was not read or deleted.
- The published credential manager was collapsed by default, exposed the standard UI Automation
  Expand/Collapse pattern, displayed the active credential after expansion, and collapsed again.
- NuGet vulnerability audit with transitive dependencies: no known vulnerable packages reported by
  the configured `nuget.org` source.
- Self-contained `win-x64` publish: passed; the published process opened a real Settings window in the
  2026-08-27 smoke check and remains running for interactive configuration.
- No live provider transcription result, real microphone capture result, real clipboard/focus target,
  Windows 10/11 compatibility,
  accessibility, performance, or long-duration manual result has been recorded.

### 1.2 Clipboard fallback change evidence — 2026-08-31

- The insertion slice compiled successfully in an equivalent `net9.0-windows10.0.19041.0`
  Windows Desktop compile-check project, with 0 warnings and 0 errors.
- A standalone Unicode event smoke check passed for the Vietnamese sample and verified exact
  UTF-16 key-down/key-up pairs.
- The standard solution build/test remains pending because this workstation has SDK `9.0.301`
  while the solution targets .NET 10; no MSTest result is claimed for the new test until SDK 10 is available.

### 1.3 Application icon evidence — 2026-08-31

- `VoicePaste.ico` was visually inspected and loaded successfully through `System.Drawing.Icon`.
- The ICO contains explicit `16x16`, `24x24`, `32x32`, `48x48`, `64x64`, and `256x256` entries.
- Executable/tray project wiring was inspected; final Release build verification remains blocked by the
  installed SDK mismatch recorded above.

## 2. Unit test catalog

| ID | Test area | Planned assertions | Requirements |
|---|---|---|---|
| UT-STATE-001 | State transitions | Valid primary and terminal transitions; terminal cleanup returns to `Idle` | FR-050, NFR-011 |
| UT-STATE-002 | Session concurrency | Atomic single-session lease rejects overlapping and rapid hotkey sessions | FR-014, NFR-013 |
| UT-LIFE-001 | Process concurrency | A Windows session can hold only one live VoicePaste application lease; a duplicate launch is rejected and a later launch succeeds after disposal | FR-014, NFR-013 |
| UT-HOTKEY-001 | Press/release debounce | One press starts once; release stops once; repeats do not duplicate work | FR-010–011, NFR-013, NFR-020 |
| UT-HOTKEY-002 | Binding validation | Unsafe, conflicting, empty, and valid gestures are classified correctly | FR-012–013, FR-015 |
| UT-HOTKEY-003 | Right Ctrl identity | Default binding fires on Right Ctrl press/release and not Left Ctrl | FR-017 |
| UT-HOTKEY-004 | Pass-through and interruption | Accepted Raw Input edges are not suppressed; lock/sleep/session/device interruption cancels uncertain held state and returns to `Idle` | FR-018, NFR-011, NFR-016 |
| UT-AUDIO-001 | Duration policy | Captures below 300 ms are ignored and elapsed duration alone never stops an active held-key capture | FR-023–024 |
| UT-AUDIO-004 | Short-tap and stop recovery | A sub-300-ms tap returns to `Idle` and permits the next session; missing WASAPI stop/flush completion is bounded, while cancellation remains distinct from timeout | FR-024, FR-026, NFR-011, NFR-013 |
| UT-AUDIO-002 | Cleanup ownership | Completion, cancellation, timeout, and expiry release the audio lease | FR-025, FR-073, NFR-012 |
| UT-AUDIO-003 | Bounded capture policy | Bounded channel preserves frame order; chunks stay at most five minutes; a breached 256 MiB reserve returns `StorageFull` and cleanup | FR-023, NFR-007, NFR-015 |
| UT-STT-001 | Error normalization | No-speech, network, auth, quota, timeout, and provider errors map correctly | FR-035, NFR-010 |
| UT-STT-002 | Transcript validation | Empty text is rejected; trimming is optional; Unicode is preserved | FR-037–038, FR-044 |
| UT-STT-003 | Retry policy | Retryable failures retain audio for no more than 15 minutes; terminal/expiry paths delete it | FR-025, FR-036, NFR-012 |
| UT-TARGET-001 | Target policy | Process identity and foreground state are revalidated; stale, changed, background, or elevated targets fall back without activation | FR-040, FR-042, FR-047–048 |
| UT-CLIP-001 | Clipboard ownership | Restore only when private token, sequence, and owner still match the lease | FR-045–046 |
| UT-CLIP-003 | Safe snapshot gate | Unsupported, delayed-rendered, locked, or over-16-MiB clipboard content is not overwritten; direct Unicode fallback is attempted and manual fallback remains available | FR-045–047 |
| UT-CLIP-002 | Bounded retry | Busy clipboard retries are bounded and return typed failure | FR-047, NFR-014 |
| UT-INSERT-001 | Paste decision | Only valid non-empty text reaches insertion; failure retains manual copy | FR-037, FR-042–044, FR-047 |
| UT-INSERT-002 | Unsafe clipboard insertion | Direct Unicode input emits exact UTF-16 key-down/key-up pairs without adding or changing clipboard formats | FR-043–047 |
| UT-STATUS-001 | Actionable error presentation | Internal message/diagnostic codes are hidden; target, network, privilege, and clipboard errors include a recovery action | FR-048, FR-054 |
| UT-CRED-001 | Credential list filtering | Search credential metadata case-insensitively, mark the active reference, and never add a secret preview | FR-062, NFR-024 |
| UT-STT-004 | Mixed-language orchestration | Ordered Vietnamese-English segments are sent/combined without manual language changes or lost transitions | FR-031, FR-039 |
| UT-STT-005 | Realtime completion ordering | Queued audio sends drain before the realtime buffer is committed, final transcription completes, and insertion begins | FR-030, FR-042, NFR-011, NFR-013 |
| UT-SET-001 | Settings validation | Right Ctrl and mixed-language defaults plus hotkey, microphone, language, and provider validation | FR-017, FR-039, FR-060–061 |
| UT-SET-002 | Settings migration/reset | Older schema migration and reset preserve valid invariants | FR-060, FR-065 |
| UT-PRIV-001 | Diagnostic redaction | Secrets, full transcript, paths, and clipboard payloads are not emitted | NFR-023–025 |

## 3. Integration test catalog

| ID | Test area | Environment and evidence | Requirements |
|---|---|---|---|
| IT-LIFE-001 | Application lifecycle | WPF harness verifies close-to-tray, pause, explicit exit, and Raw Input registration disposal | FR-002–005, FR-016 |
| IT-ICON-001 | Application icon packaging | Icon asset loads, contains the required multi-resolution entries, and is referenced by the executable/tray project | FR-003, FR-005, NFR-034 |
| IT-AUDIO-001 | Audio capture | Test audio source and Windows device adapter verify ordered incremental capture, bounded buffers/chunks, 300 ms minimum, release/cancel stop conditions, 256 MiB storage reserve, and device errors | FR-020–026, NFR-002, NFR-007, NFR-015 |
| IT-STT-001 | Provider | Mocked HTTP and WebSocket responses verify session acknowledgement, audio append/commit, language hints, mixed-language segmentation, transcript events, timeouts, and typed errors | FR-030–039, NFR-010 |
| IT-STT-002 | Provider live smoke | Explicit test environment verifies Vietnamese, English, mixed Vietnamese-English, long-recording handling, and provider disclosure | FR-031, FR-034, FR-039, NFR-022 |
| IT-STT-003 | Provider corpus gate | Frozen-corpus runner emits per-utterance/aggregate WER, English keyword recall, and complete-segment omission evidence for the pinned provider/model | FR-031, FR-034, FR-039, NFR-006 |
| IT-STT-004 | Long provider pipeline | Synthetic 60-minute content is segmented when required, submitted, and recombined with no missing or reordered segment | FR-023, FR-030, NFR-007 |
| IT-CLIP-001 | Clipboard | Controlled STA harness verifies <=16 MiB supported snapshot, private token/sequence/owner checks, external mutation, restore, unsafe-snapshot direct-Unicode fallback, and lock retries | FR-043–047, NFR-014 |
| IT-PASTE-001 | Controlled target | Test window verifies clipboard or direct-Unicode insertion only while captured identity remains foreground; closure, focus change, stale handle, and elevation use manual fallback without activation | FR-040–048 |
| IT-CLEAN-001 | Startup cleanup | Seeded expired recording is removed; valid retry lease is preserved | FR-025, FR-036, NFR-012 |
| IT-CRED-001 | Credential protection and management | Current-user round trip, VoicePaste-scoped metadata enumeration, selected delete, and cleanup succeed; plaintext settings/logs and list descriptors contain no secret | FR-062, NFR-023–024 |
| IT-SET-001 | Settings persistence | Save/reload/migrate/reset round trips across app restarts | FR-060–065, FR-070 |

Live-provider tests must never run by default in a developer unit-test command and must use dedicated
credentials and non-sensitive audio fixtures.

## 4. Manual compatibility catalog

| ID | Scenario | Expected result | Requirements |
|---|---|---|---|
| MAN-APP-001 | Launch, close settings, tray actions, and exit | App remains in tray until Exit and reports state | FR-001–005 |
| MAN-FOCUS-001 | Record, then retain or change foreground app before paste | Overlay never activates; auto-paste occurs only while the captured target remains foreground; changed focus produces manual fallback | FR-040–042, FR-047, FR-051–052 |
| MAN-NOTEPAD-001 | Vietnamese/English dictation in Notepad | Correct Unicode insertion through clipboard or direct-Unicode fallback and conditional clipboard restore | FR-031, FR-042–046 |
| MAN-PTT-001 | Hold Right Ctrl for 10 minutes on a real microphone | Capture remains active until release, Right Ctrl events remain available to the foreground app, then audio transcribes; Left Ctrl does not trigger | FR-017–018, FR-023, NFR-016 |
| MAN-MIX-001 | Mixed Vietnamese-English utterance in one recording | Both languages are recognized in sequence without changing language settings; result is consistent with the corpus gate rather than used as its substitute | FR-039, NFR-006 |
| MAN-WORD-001 | Dictation in Microsoft Word | Correct insertion and no focus theft | NFR-031 |
| MAN-TEAMS-001 | Dictation in Microsoft Teams | Correct insertion and recovery behavior | NFR-031 |
| MAN-BROWSER-001 | Chrome and Edge text fields | Correct insertion in simple and rich text controls | NFR-031 |
| MAN-IDE-001 | Visual Studio and VS Code | Correct Unicode insertion into editors | NFR-031 |
| MAN-ELEV-001 | Target runs elevated | No injection attempt succeeds silently; actionable notice appears | FR-048 |
| MAN-DEVICE-001 | Microphone removed/denied/exclusive | App remains alive, releases state, and gives recovery action | FR-026, FR-054 |
| MAN-CLIP-001 | Text/image/file/rich/delayed clipboard plus external copy race | User data is preserved; unsafe clipboard is not overwritten, direct-Unicode fallback inserts when accepted, and external writes are never overwritten | FR-045–047 |
| MAN-SESSION-001 | Rapid presses, cancel, lock, sleep, session change, input-device removal, and app exit | No overlap/leak or indefinite capture; every terminal path returns to `Idle` | FR-014–016, NFR-011–013, NFR-016 |
| MAN-WIN-001 | Launch/capture/paste on Windows 11 x64 and Windows 10 22H2 x64 build 19045 | Core MVP flow succeeds on both the primary and compatibility baseline | FR-001, NFR-030 |
| MAN-DPI-001 | Multiple monitors at 100%, 150%, and 200% | Overlay remains visible, non-blocking, and correctly scaled | NFR-034 |
| MAN-A11Y-001 | Keyboard-only settings and non-color state cues | All controls usable; state is not conveyed by color alone | NFR-032–033 |

## 5. Performance and resource checks

| ID | Metric | Target | Requirements |
|---|---|---|---|
| PERF-001 | Hotkey press to visible listening feedback | At most 150 ms under normal load | NFR-001 |
| PERF-002 | Ten-second recording, release to completed paste | p50 <= 5 seconds and p95 <= 8 seconds over at least 30 Release-build runs under the pinned profile | NFR-003 |
| PERF-003 | Idle private working set | Below 150 MB after warm-up and five minutes idle in Release | NFR-004 |
| PERF-004 | UI responsiveness during transcription | No application-controlled WPF dispatcher operation exceeds 100 ms | NFR-005 |
| PERF-005 | Synthetic 60-minute held-key capture | Capture overhead after stabilization <= 16 MiB, frames remain ordered, and elapsed duration never stops capture | FR-023, NFR-002, NFR-007 |
| PERF-006 | Frozen mixed-language corpus | Normalized WER <= 20%, English keyword recall >= 90%, and zero complete reference-language segments omitted | FR-039, NFR-006 |

Measurements must record build configuration, Windows version, hardware, provider, network assumptions,
sample count, and observed distribution rather than a single anecdotal value.

## 6. Security and privacy checks

- Inspect settings, logs, crash artifacts, and test output for plaintext credentials, raw audio, full
  transcripts, and clipboard payloads.
- Verify protected credentials cannot be decrypted by a different Windows user.
- Verify remote/local provider disclosure before first use and after provider changes.
- Verify analytics/crash reporting remains disabled unless explicitly opted in.
- Verify temporary audio permissions, expiry, normal cleanup, cancellation cleanup, and startup cleanup.

## 7. Standard commands

Use the repository-local pinned SDK and solution:

```powershell
.\.dotnet\dotnet.exe restore VoicePaste.slnx
.\.dotnet\dotnet.exe build VoicePaste.slnx -c Release --no-restore
.\.dotnet\dotnet.exe test --solution VoicePaste.slnx -c Release --no-build --no-restore
.\.dotnet\dotnet.exe restore src\VoicePaste.App\VoicePaste.App.csproj -r win-x64 -p:SelfContained=true
.\.dotnet\dotnet.exe publish src\VoicePaste.App\VoicePaste.App.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts\publish\win-x64
```

Focused tests run before the full suite. Manual and live-provider checks remain separately gated and
must not be reported as passed based on mocks.
