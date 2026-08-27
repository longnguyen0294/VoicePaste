---
name: spec-plan-implement
description: Plan and implement VoicePaste changes from the project SRS while keeping FR/NFR requirements, code, tests, and traceability evidence synchronized. Use for project bootstrap, features, bug fixes, refactors, architecture changes, and migrations in this repository; supports --plan-only, --impl-only, and --auto.
---

# VoicePaste Spec Plan Implement

Ground changes in the repository's documented product intent, implement the smallest complete
solution, update durable traceability evidence, and verify the result.

## Select the operating mode

- `--plan-only`: inspect the repository and return a grounded plan without changing files.
- `--impl-only`: implement an existing plan supplied by the user or available in the task context;
  first verify that it still matches the current repository and SRS.
- `--auto`: plan, implement, update evidence, and verify in one run.
- With no flag, follow the user's requested outcome: planning requests stop after the plan, while
  build, fix, refactor, or implementation requests continue through verification.

Do not require an extra confirmation between planning and implementation when the user already
asked for the change. Ask only when an unresolved product decision would materially alter the
result and cannot be inferred safely.

## Use VoicePaste's sources of truth

Work from the repository root. Read applicable `AGENTS.md` files before editing anything.

The current product source of truth is `Requirement/VoicePaste_Requirements.md`. Read it fully
before a substantial feature or architectural change. For a narrow change, inspect at least the
product flow, every affected `FR-*` and `NFR-*` row, related architecture/data-model sections,
edge cases, acceptance criteria, testing requirements, and confirmed defaults.

Also inspect, when present:

- the solution and project files (`*.sln`, `*.slnx`, `*.csproj`);
- affected production code and tests;
- `specs/` design, implementation-plan, test, and traceability documents;
- repository-owned validation scripts.

Treat a more specific, current project decision as refining the SRS. If it conflicts with the SRS,
do not silently choose one: make the conflict visible and update the affected documentation as part
of an authorized implementation. Do not turn aspirational Phase 3 or Phase 4 items into MVP scope.

The SRS section "Key implementation decisions to confirm" contains genuine decision points. Use a
documented choice when one exists. Recommended defaults may be used where the SRS provides them;
otherwise ask only when the current task crosses that decision boundary.

## Establish the baseline

1. Inventory the repository and determine whether it is a bootstrap-only, partial, or established
   implementation.
2. If this is a Git worktree, inspect `git status` and relevant diffs. Preserve all unrelated user
   changes; never reset, discard, or overwrite them.
3. Discover the actual build and test entry points instead of assuming a project name.
4. Identify affected requirements before editing production code. VoicePaste currently uses
   `FR-*` and `NFR-*`; preserve any additional identifier scheme the project later adopts.
5. If requested behavior has no requirement, distinguish a product change from internal
   engineering work. Update the SRS for an authorized product change. For tooling or refactoring,
   link the work to the requirements or quality attributes it protects without inventing a product
   requirement.

If a deterministic traceability tool exists, run its baseline/snapshot mode before edits and retain
the baseline outside the repository until final verification. Otherwise record the initial state and
perform the checks described below manually; do not invent a mandatory harness for a one-off task.

## Produce a spec-grounded plan

Use this shape, omitting empty sections rather than filling them with speculation:

```markdown
## Plan: [title]

### Goal / Root Cause
[Current behavior, desired behavior, and why they differ]

### Relevant Requirements
- [FR/NFR ID — SRS section — applicable acceptance criterion or edge case]

### Decisions and Assumptions
- [Only decisions that affect implementation]

### Implementation Steps
| # | File or component | Change | Depends on |
|---|---|---|---|

### Traceability Change Set
| Requirement | Work item/change | Code/module | Test evidence | Durable mapping |
|---|---|---|---|---|

### Verification
- [ ] Focused tests
- [ ] Build and relevant test suite
- [ ] Traceability review
- [ ] Required Windows/manual checks
```

Every planned production component must occur in at least one traceability row. Plans must identify
failure handling and cleanup for resource-owning or asynchronous paths. In `--plan-only`, stop after
returning the plan and explicitly list unresolved decisions.

## Implement the change

Re-read each target before editing. Make scoped changes, add or update focused tests, and preserve
existing project conventions. Use the SRS's suggested .NET 8+/WPF architecture until the repository
or user establishes another choice; do not replace the stack merely because an alternative is
available.

Apply these VoicePaste invariants whenever relevant:

- permit only one dictation session and return every success, cancellation, timeout, and error path
  to `Idle` after releasing owned resources;
- keep audio capture and transcription off the UI thread and propagate cancellation deliberately;
- never let listening or processing UI steal focus from the target application;
- preserve the intended target-window policy across recording and transcription;
- paste only non-empty Unicode text and restore the clipboard only after verifying VoicePaste still
  owns the clipboard content it placed there;
- bound retries for contested Windows resources such as the clipboard;
- delete temporary audio after completion or cancellation and clean stale files after abnormal exit;
- protect provider credentials with Windows user-scoped protection and exclude credentials, raw
  audio, and full transcripts from default logs;
- distinguish recoverable microphone, provider, network, authentication, quota, timeout, clipboard,
  and privilege failures where the relevant layer can do so.

Do not implement unrelated requirements merely because they are adjacent in the SRS.

## Maintain durable traceability

For every changed behavior, keep the same requirement IDs visible in the relevant durable evidence:

1. Update the SRS when intent, scope, priority, acceptance criteria, defaults, or architecture
   decisions change.
2. Update an existing feature design or implementation plan when its behavior or work items change.
3. Map each affected ID to concrete production files and preferably symbols; broad directory-only
   mappings are insufficient.
4. Map each affected ID to automated test cases and any required manual compatibility checks.
5. Record test status only after running the test. Keep an unexecuted manual check marked pending.

Follow an existing repository traceability format when present. When the first production slice is
being added and no durable mapping exists, create `specs/requirements-traceability.md` with one row
per requirement/change and these columns:

```markdown
| Requirement | Behavior / work item | Production evidence | Automated test evidence | Manual evidence | Status |
```

Do not create that mapping during `--plan-only`, and do not satisfy traceability with timestamp-only,
status-only, or unrelated documentation edits. Review mappings semantically against the final diff.

## Verify proportionally

Discover exact project paths first. For a typical .NET solution, run the narrowest relevant tests
early, then the full applicable sequence, adapting arguments to the repository:

```powershell
dotnet build <solution-or-project>
dotnet test <solution-or-test-project>
```

Use `dotnet restore` when dependencies are not already restored, and use the project's pinned SDK,
configuration, analyzers, formatting, coverage, packaging, or publish commands when present. For
installer or runtime-delivery changes, include the relevant `win-x64` publish/package verification.

Run repository traceability audit/guard commands when they exist. Otherwise verify manually that:

- every changed production component appears in the mapping;
- every mapped requirement exists in the SRS;
- test names/paths in the mapping exist and cover the claimed behavior;
- statuses match commands actually run;
- the change did not silently expand MVP scope.

Automate what can be tested deterministically. Report Windows integration checks—global hotkeys,
microphone behavior, focus preservation, clipboard ownership, paste compatibility, elevation,
multi-monitor behavior, and display scaling—as passed only when they were actually exercised.

## Report the outcome

Lead with the implemented behavior or completed plan. Include changed code and specification files,
affected requirement IDs, build/test/audit commands and results, and any manual verification or
product decision still pending. Never report the task complete while a required check is failing.
