# VoicePaste Specifications

## Status

- Product stage: MVP foundation implementation in progress
- Source SRS: [`Requirement/VoicePaste_Requirements.md`](../Requirement/VoicePaste_Requirements.md)
- SRS version: 1.5, Draft for MVP
- Target: 64-bit Windows 11; Windows 10 22H2 build 19045 compatibility baseline
- Adopted stack: C# / .NET 6.0.36 / WPF (`net6.0-windows`, self-contained `win-x64`)

Production source now exists under `src/`, with deterministic suites under `tests/`. The .NET 6.0.36
solution, Core coordinator, Windows adapters, tray shell, OpenAI `gpt-transcribe` candidate, and
self-contained publish have build/test evidence. The provider adapter exists, but final provider
selection, live speech quality, frozen mixed-language corpus, and Windows compatibility/manual gates
remain pending. A document must not say an implementation or test has passed until corresponding
evidence exists and has been executed.

## Document map

| Document | Purpose |
|---|---|
| [architecture.md](architecture.md) | System boundaries, components, runtime flow, state ownership, and platform constraints |
| [data-model.md](data-model.md) | Domain records, settings, errors, retention, and data invariants |
| [implementation-plan.md](implementation-plan.md) | Ordered work items, dependencies, scope, and completion gates |
| [test-plan.md](test-plan.md) | Automated, integration, performance, security, and manual verification strategy |
| [provider-evaluation.md](provider-evaluation.md) | Evidence gate and scorecard for the MVP Vietnamese-English speech provider |
| [requirements-traceability.md](requirements-traceability.md) | One-row-per-requirement mapping to planned code and test evidence |
| [decision-log.md](decision-log.md) | Adopted defaults, provisional choices, and unresolved product decisions |

## Authority and synchronization

The SRS defines product intent, priorities, MVP boundaries, and acceptance criteria. Accepted entries
in `decision-log.md` refine implementation details. The remaining documents are derived specifications
and must stay synchronized with both.

When a change introduces a conflict:

1. Make the conflict explicit instead of silently choosing an interpretation.
2. Resolve or record the decision in `decision-log.md`.
3. Update the SRS if product intent, scope, priority, or acceptance criteria changed.
4. Update architecture, implementation tasks, tests, and traceability in the same change.

## MVP boundaries

The current plan includes the SRS Phase 1 proof of concept and Phase 2 MVP. Phase 3 enhanced features
and Phase 4 AI-assisted writing remain outside MVP unless the SRS is explicitly revised.

In particular, transcript history UI (`FR-071`, `FR-072`), local Whisper, translation, rewriting,
summarization, hands-free background transcription, and account/cloud-sync features are not part of
the current implementation baseline. Unbounded user-held push-to-talk capture and mixed
Vietnamese-English recognition are mandatory MVP behaviors.

## Required change discipline

- Identify affected `FR-*` and `NFR-*` IDs before production edits.
- Map each changed production file and test to those IDs.
- Keep manual checks pending until they are actually exercised on Windows.
- Never use status-only or timestamp-only documentation edits as traceability evidence.
- Review the final mapping semantically against the source diff.
