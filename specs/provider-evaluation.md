# VoicePaste MVP Speech Provider Evaluation

## 1. Decision rule

No speech-to-text provider is selected for the MVP until its complete VoicePaste pipeline passes every
hard gate in this document. Published claims, a provider demo, or monolingual accuracy alone are not
sufficient evidence. Results apply to the exact model/API version, region, request configuration,
segmentation algorithm, and transcript combiner tested.

This document supplies the evidence gate for `FR-031`, `FR-034`, `FR-039`, `NFR-003`, `NFR-006`, and
DEC-004/005/018. OpenAI `gpt-transcribe` is the implemented provisional candidate; this does not make
it the selected winner before the measurements below exist.

## 2. Versioned acceptance corpus

Store only licensed, consented, or project-created non-sensitive fixtures. Each corpus release has an
immutable manifest containing a corpus version, audio checksum, reference transcript, speaker code,
scenario tags, English keyword annotations, and Vietnamese/English segment boundaries. Raw fixtures
must not contain real credentials, customer content, or private conversations.

The first acceptance corpus shall contain at least 30 mixed Vietnamese-English utterances and:

- at least six speakers with varied gender, pitch, and regional Vietnamese accents where available;
- clean-room and ordinary office-noise samples;
- conversational code-switches in both Vietnamese-to-English and English-to-Vietnamese directions;
- developer terms, product names, acronyms, numbers, and punctuation intent;
- short, medium, and multi-minute samples, including switches near application-created segment
  boundaries;
- a fixed training/development subset, if tuning is required, separate from the scored acceptance
  subset.

The corpus manifest shall pin the text normalizer and tokenizer version. Normalization may standardize
Unicode, case, and documented punctuation for scoring, but must not erase diacritics or translate words.
All providers are scored against the same frozen acceptance subset.

## 3. Hard gates

| Gate | Required result | Evidence |
|---|---|---|
| Mixed-language accuracy | Normalized WER <= 20% over the scored corpus | Per-utterance and aggregate WER report with normalizer/tokenizer version |
| English keyword preservation | Recall >= 90% for manifest-annotated English keywords | True-positive/false-negative keyword report |
| Segment completeness | Zero complete reference-language segments omitted | Segment-boundary comparison and reviewed failures |
| Long held-key input | A synthetic 60-minute recording is accepted directly or through ordered application segmentation; no segment is missing or reordered | Segment manifest, request log without content, combined-output assertions |
| Latency | For a 10-second input, release-to-paste pipeline p50 <= 5 s and p95 <= 8 s | At least 30 Release-build runs under the recorded benchmark profile |
| Error contract | Auth, quota, timeout, network, no-speech, payload-size, and provider failures map to typed VoicePaste categories without process termination | Automated adapter tests and fault-injection report |
| Privacy disclosure | Remote/local processing, provider retention/training policy, region, and deletion controls can be disclosed accurately in product UI | Reviewed provider documentation and disclosure copy |
| Credential safety | The adapter works with a user-supplied key retrieved from Windows Credential Manager and never logs it | Credential integration test and artifact scan |

A result exactly on a threshold passes. Missing, unrepeatable, or provider-version-ambiguous evidence
fails the gate. Any provider or model-version change reruns all affected gates.

## 4. Comparative criteria after hard gates

Among candidates that pass every hard gate, choose the lowest-risk total fit using this weighting:

| Criterion | Weight | Notes |
|---|---:|---|
| Mixed-language quality above the minimum | 30% | Compare aggregate and worst-decile utterance quality, not aggregate alone |
| Latency and reliability | 20% | p50/p95, rate limits, retry behavior, and regional availability |
| Privacy and retention controls | 20% | Training opt-out/default, retention duration, deletion, and data region |
| Long-input/segmentation fit | 15% | Request limits, timestamps, context support, ordering, and overlap behavior |
| Cost predictability | 10% | Cost per audio hour, minimum charges, free-tier dependence, and quota visibility |
| SDK/API operational risk | 5% | Versioning, support horizon, license, and adapter complexity |

Cost is measured for the expected MVP usage profile and is never allowed to override a failed privacy
or quality hard gate.

## 5. Evaluation procedure

1. Pin the provider/model/API version and document region, account tier, pricing date, request limits,
   retention settings, and whether audio leaves the device.
2. Implement only the thin provider adapter plus any required segmentation/combination behind the core
   contract. Do not add provider concepts to WPF or domain state.
3. Run deterministic mocked error/segmentation tests, then the frozen corpus and latency benchmark.
4. Record raw metric artifacts without secrets or full production-user content. Corpus transcripts are
   test assets and remain access-controlled according to their license/consent.
5. Review failures by scenario and speaker; aggregate scores cannot hide a complete language-segment
   omission or a systematic code-switch boundary defect.
6. Complete the scorecard below, update DEC-004 and DEC-005 with the selected candidate and rationale,
   then replace provider-dependent `Pending decision` traceability rows with planned concrete evidence.

## 6. Candidate scorecard

Do not enter estimated values as measured evidence.

| Candidate / model / region | Hard gates | Weighted score | Processing mode | Retention summary | Cost profile | Evidence location | Status |
|---|---|---:|---|---|---|---|---|
| OpenAI `gpt-transcribe` / API default / account region | Mocked error, credential, PCM/WAV, mixed-hint, segmentation, ordering, and combination gates passed; all live/quality/privacy gates not run | - | Remote cloud API | Documentation review pending | Measurement pending | `tests/VoicePaste.Providers.OpenAI.Tests`; current 2026-08-27 test record in `test-plan.md` | Provisional candidate; not selected |

## 7. Required test identifiers

- `UT-STT-004`: ordered mixed-language segment combination.
- `IT-STT-002`: selected-provider live Vietnamese, English, and mixed-language smoke.
- `IT-STT-003`: frozen-corpus quality gate and reproducible metric artifact.
- `IT-STT-004`: synthetic 60-minute provider/segmentation pipeline.
- `PERF-002`: 10-second release-to-paste latency distribution.

Passing mocks alone cannot satisfy provider selection or any live-provider gate.
