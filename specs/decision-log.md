# VoicePaste Decision Log

## Status definitions

- **Accepted**: authoritative for implementation until superseded.
- **Provisional**: safe planning default taken from the SRS; confirm before the dependent production
  slice is finalized.
- **Pending**: no choice has been made; dependent production work is blocked at the stated gate.
- **Deferred**: intentionally outside the current MVP.

## Decisions

| ID | Topic | Status | Current direction | Gate / consequence |
|---|---|---|---|---|
| DEC-001 | Application platform and stack | Accepted | C#, .NET 10 LTS, WPF, `net10.0-windows`, `win-x64`, SDK `10.0.302` pinned in `global.json`; `NotifyIcon` behind an App adapter | WPF gives the tray utility mature Win32 interop and a stable LTS runtime without leaking UI dependencies into Core |
| DEC-002 | Intended target-window policy | Accepted | Bind to the target captured at recording start; auto-paste only if its process identity remains valid and it is still foreground; never reactivate it | A changed target falls back to retained transcript/manual copy/retry, preventing focus theft or injection into an unintended app |
| DEC-003 | Clipboard restoration policy | Accepted | Snapshot only safely materialized supported formats up to 16 MiB; restore only when private token, clipboard sequence, and owner still match | Unsafe snapshots skip auto-paste; any external clipboard write wins and is never overwritten |
| DEC-004 | MVP speech-to-text provider | Provisional | Implement OpenAI `gpt-transcribe` as the first concrete candidate through `VoicePaste.Providers.OpenAI`; use the REST transcription endpoint, mixed `vi`/`en` hints, ordered 24 MiB PCM segmentation, continuation context, and typed error mapping | Mocked adapter gates pass, but selection is not Accepted until live Vietnamese/English/mixed smoke, frozen corpus, latency, 60-minute pipeline, privacy/retention, and cost evidence pass `provider-evaluation.md` |
| DEC-005 | Cloud/offline product priority | Provisional | Use the remote OpenAI candidate for the MVP proof of concept and disclose that recorded audio leaves the device; keep provider interfaces neutral | Cloud processing is not the verified product winner until DEC-004's privacy and quality gates pass; a local provider remains outside current implementation, not prohibited by Core |
| DEC-006 | Provider credential delivery | Accepted | MVP users supply their provider key; store it for the current user in Windows Credential Manager and persist only a reference. Settings may enumerate reference/last-written metadata for `VoicePaste:*` generic credentials and delete a confirmed selection, but never display secret values | Avoids an unplanned account/backend/security boundary while satisfying protected-secret and credential-management requirements |
| DEC-007 | Failed-transcription audio retention | Accepted | Retain audio only for a retryable transcription failure and for at most 15 minutes; delete on success, cancel, non-retryable failure, expiry, exit, or startup recovery | Reconciles FR-025 cleanup with FR-036 retry without creating history |
| DEC-008 | Minimum usable recording duration | Accepted | Ignore captures shorter than 300 ms with a non-blocking notification | Short taps avoid unnecessary encoding/provider calls; threshold is not a maximum duration |
| DEC-009 | Installer and auto-start technology | Pending | No installer mechanism selected | Does not block proof of concept; blocks release packaging and FR-004 completion |
| DEC-010 | Transcript history | Deferred | Disabled by default; no persistent history model in MVP | FR-070 and FR-073 remain enforced; FR-071 and FR-072 stay deferred |
| DEC-011 | Phase 3/4 capabilities | Deferred | Local Whisper, hands-free/streaming enhancements, and AI writing are excluded; this does not limit elapsed duration of user-held push-to-talk capture | Requires explicit SRS scope change before implementation |
| DEC-012 | Push-to-talk default and duration | Accepted | Right Ctrl is the MVP default and is distinct from Left Ctrl; capture has no application-imposed duration limit while held | Supersedes the former Ctrl+Space and 60-second defaults; requires incremental storage and long-session tests |
| DEC-013 | Mixed Vietnamese-English recognition | Accepted | Code-switching within one recording is a Must requirement and cannot require a manual language change between segments | Provider selection and acceptance tests must demonstrate mixed-language output |
| DEC-014 | Global push-to-talk input mechanism | Accepted | Receive Raw Input on a hidden native message window, distinguish scan code/extended Right Ctrl, and leave physical key events unsuppressed | `RegisterHotKey` cannot model held-key release/side reliably; a suppressing low-level hook conflicts with pass-through FR-018 |
| DEC-015 | Long-capture resource policy | Accepted | Bounded channel, one sequential writer, chunks no longer than five minutes, 256 MiB free-space reserve, and no elapsed-time cancellation | Synthetic 60-minute capture must add no more than 16 MiB after stabilization; resource failure returns a typed error and cleanup |
| DEC-016 | Windows support baseline | Accepted | Windows 11 x64 is primary; Windows 10 22H2 x64 build 19045 is the compatibility baseline | Both receive MVP launch/capture/paste verification; OS servicing status does not change VoicePaste's compatibility requirement |
| DEC-017 | Performance acceptance | Accepted | Release-to-paste p50 <= 5 s and p95 <= 8 s for 10-second input; idle private working set < 150 MB; dispatcher block <= 100 ms | Measurements must record build, hardware, Windows, provider/region, network profile, sample count, and distribution |
| DEC-018 | Mixed-language provider gate | Accepted | Versioned corpus with at least 30 mixed utterances; normalized WER <= 20%, English keyword recall >= 90%, and no complete reference-language segment omitted | A provider cannot be selected for MVP from marketing claims or a single anecdotal sample |
| DEC-019 | Windows audio adapter | Accepted | Use WASAPI through centrally pinned `NAudio.Wasapi` `3.0.1` and its modern `WasapiRecorderBuilder` API behind `VoicePaste.Windows` interfaces | NAudio types, device handles, file paths, and encoding details cannot cross into Core or provider contracts; adapter tests protect replacement |

## Decision update procedure

When a pending or provisional item is resolved:

1. Change its status and record the chosen direction and rationale.
2. Update the SRS if the decision changes product intent or its stated defaults.
3. Update architecture/data model sections affected by the choice.
4. Unblock and refine the corresponding implementation tasks.
5. Replace planned traceability paths and test IDs with actual evidence as implementation lands.
