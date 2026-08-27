# VoicePaste MVP Implementation Plan

## 1. Goal

Deliver the SRS Phase 1 proof of concept and Phase 2 MVP as a testable 64-bit Windows application.
The implementation must preserve focus, serialize dictation sessions, clean all temporary resources,
protect credentials, and maintain requirement-to-code-to-test traceability.

## 2. Work-item statuses

- `Complete`: durable evidence exists for this work item.
- `In progress`: production code exists, but one or more required feature, automated, or manual gates remain.
- `Ready`: prerequisites are known and implementation may start.
- `Blocked`: an explicit decision or missing dependency prevents safe completion.
- `Deferred`: outside the current MVP.

## 3. Ordered work items

| ID | Work item | Main deliverables | Requirements | Depends on | Status |
|---|---|---|---|---|---|
| VP-000 | Establish specification baseline | `specs/` architecture, data model, plan, tests, decisions, traceability | All MVP FR/NFR | — | Complete |
| VP-001 | Bootstrap repository and solution | Git metadata, `.gitignore`, pinned .NET 10 `global.json`, `net10.0-windows` solution, build properties, project skeletons | FR-001, NFR-030 | DEC-001, DEC-016 | Complete |
| VP-002 | Implement core contracts and state machine | Domain records, interfaces, session states, transitions, typed errors | FR-014, FR-030, FR-033, FR-050, NFR-010–013 | VP-001 | Complete |
| VP-003 | Add core unit-test foundation | State, concurrency, validation, normalization, and error-mapping tests | FR-014, FR-037, FR-038, FR-050, NFR-011, NFR-013 | VP-002 | Complete |
| VP-004 | Implement lifecycle and tray shell | WPF host, tray actions, pause/exit, state tooltip, clean shutdown | FR-001–005, FR-016 | VP-001, VP-002 | In progress — shell/build/smoke exist; sign-in startup and manual tray checks remain |
| VP-005 | Implement global input and target capture | Hidden message window, pass-through Raw Input, side-aware Right Ctrl edges, configuration validation, interruption reconciliation, captured/validated foreground target | FR-010–018, FR-040, NFR-013, NFR-016, NFR-020 | VP-002, DEC-002, DEC-012, DEC-014 | In progress — default path/unit tests exist; configurable binding UI and manual input checks remain |
| VP-006 | Implement microphone capture | Pinned NAudio/WASAPI adapter, device discovery, bounded channel, sequential <=5-minute chunks, no elapsed-time cutoff, 300 ms minimum, 256 MiB reserve, cancellation, temp-resource ownership | FR-020–026, NFR-001–002, NFR-007, NFR-012, NFR-015, NFR-020 | VP-002, VP-005, DEC-008, DEC-012, DEC-015, DEC-019 | In progress — adapter/storage/tests exist; real-device and long-duration gates remain |
| VP-007 | Evaluate and implement provider adapter | Reproducible scorecard, selected concrete provider, neutral capability mapping, mixed Vietnamese-English mode, long-recording segmentation/combination, disclosure, error normalization | FR-030–039, NFR-003, NFR-006, NFR-010, NFR-022 | VP-002, DEC-004–005, DEC-013, DEC-018 | In progress — OpenAI `gpt-transcribe` candidate and deterministic adapter gates exist; live, corpus, latency, privacy/cost, and 60-minute gates remain |
| VP-008 | Implement clipboard and paste services | <=16 MiB safe snapshot, private lease token, sequence/owner validation, bounded retry, SendInput paste, conditional restore, no-focus target validation, privilege detection | FR-040–048, NFR-014 | VP-002, VP-005, DEC-002–003 | In progress — implementation/policy tests exist; controlled STA/target and app-matrix checks remain |
| VP-009 | Assemble dictation vertical slice | Coordinator from Right Ctrl press through unbounded capture, mixed-language transcription, paste, and cleanup; single session and empty-text rejection | FR-011, FR-014, FR-017, FR-020, FR-023, FR-030, FR-037, FR-039, FR-042–043, FR-050, NFR-005, NFR-011 | VP-005–008 | In progress — concrete provider is composed end to end; live microphone/provider/paste and performance gates remain |
| VP-010 | Implement overlay and notifications | Non-activating overlay, state presentation, accessible/error feedback | FR-022, FR-041, FR-050–054, NFR-021, NFR-033–034 | VP-004, VP-009 | In progress — non-activating overlay and shared actionable Settings/tray error presentation exist; accessibility/manual gates remain |
| VP-011 | Implement settings and credential storage | Versioned local settings, Right Ctrl and mixed-language defaults, validation, migration, reset, and user-supplied keys in Windows Credential Manager | FR-012, FR-017, FR-021, FR-032, FR-039, FR-060–065, FR-070, NFR-023–024 | VP-001, VP-002, DEC-004, DEC-006 | In progress — validated JSON, legacy-provider migration, API-key save/status plus collapsible metadata-only search/refresh/confirmed-delete UI, and Credential Manager adapters exist; full editable settings/reset/test UI remains |
| VP-012 | Implement retry and recovery | Retry transcription within the 15-minute audio lease, manual copy/retry paste, recovery actions | FR-035–036, FR-047–048, FR-054, NFR-010–012 | VP-007–011, DEC-007 | In progress — typed provider errors and retained manual copy exist; transcription retry lease and retry-paste command remain |
| VP-013 | Harden cleanup and Windows events | Startup cleanup, lock/sleep handling, bounded exit cleanup, privacy-safe logging | FR-016, FR-025–026, FR-073, NFR-012, NFR-023, NFR-025 | VP-006–012 | In progress — cleanup/session interruption paths exist; crash/privacy inspections remain |
| VP-014 | Complete settings UI and tests | Keyboard-accessible settings, microphone/hotkey tests, conflict detection | FR-012–013, FR-021, FR-053, FR-061, FR-063–065, NFR-032 | VP-005–013 | Blocked by dependent slices |
| VP-015 | Add startup and packaging | Start-with-Windows, release publish, installer/uninstaller behavior | FR-004, NFR-030 | VP-004, VP-011, DEC-009 | Blocked by DEC-009 |
| VP-016 | Complete automated integration suite | Fake audio source, mocked provider, clipboard/paste harness, startup cleanup | SRS section 12.2 | VP-006–013 | In progress — 32 deterministic tests exist, including mocked HTTP provider, native input layout, App error presentation, and current-user credential enumerate/delete; live provider, controlled paste, audio-device, and startup cleanup integration remain |
| VP-017 | Execute compatibility and quality gates | Windows 11/10 baseline, app matrix, elevation, DPI, multi-monitor, corpus accuracy, 60-minute capture, latency distribution, memory, dispatcher, privacy inspection | NFR-001–007, NFR-015–016, NFR-023–025, NFR-030–034 | VP-010–016 | In progress — Release automation and dependency audit pass; manual/live/corpus/performance/compatibility gates remain |
| VP-018 | Final traceability audit | Replace planned evidence with exact paths/symbols/results; reconcile SRS and diff | All implemented FR/NFR | VP-001–017 | In progress — current code/test evidence synchronized; final audit waits on remaining MVP gates |
| VP-019 | Optional transcript history | View/copy/retry/delete/clear persistent transcripts | FR-071–072 | Explicit post-MVP scope decision | Deferred |

## 4. Milestones and exit criteria

### Milestone A — Deterministic foundation

Includes VP-001 through VP-003.

- The solution builds on the pinned SDK.
- Core tests run without WPF, a microphone, Win32 input injection, or network access.
- State and error contracts are stable enough for adapters.

### Milestone B — Technical proof of concept

Includes VP-004 through VP-009 with a selected provider.

- VoicePaste runs from the tray.
- The fixed Right Ctrl hold hotkey records from the default microphone without an elapsed-time cutoff.
- One provider transcribes Vietnamese, English, and mixed Vietnamese-English speech and pastes Unicode
  text into Notepad.
- Overlapping sessions, empty transcripts, cancellation, and cleanup are demonstrably safe.

### Milestone C — MVP feature complete

Includes VP-010 through VP-014.

- Hotkey, microphone, language mode, provider, startup, and clipboard behavior are configurable.
- Overlay does not steal focus.
- Clipboard ownership and retry/recovery behavior meet the SRS.
- Credentials and logs meet privacy requirements.

### Milestone D — Release candidate

Includes VP-015 through VP-018.

- A release package is installable on supported Windows versions.
- Automated suites pass in Release configuration.
- Required manual compatibility, performance, accessibility, and privacy checks have recorded evidence.
- Each implemented requirement maps to exact code and test evidence.

## 5. Scope-control rules

- Do not add transcript history UI, local Whisper, hands-free background transcription, automatic
  language detection unsupported by the selected MVP provider, or AI transformations as incidental
  work. This restriction does not permit a duration cutoff for user-held push-to-talk capture.
- Do not mark a task complete based only on interfaces or placeholders.
- A provider-dependent task cannot be completed with only a fake provider.
- A passing unit test does not replace required Windows compatibility evidence.
- Failed or pending checks remain visible in traceability and release reporting.
