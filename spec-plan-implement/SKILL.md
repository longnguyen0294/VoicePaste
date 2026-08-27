---
name: spec-plan-implement
description: "Repository-scoped, spec-grounded plan-and-implement workflow with mandatory requirement-to-work-item-to-code-to-test traceability. Use in this repository for bug fixes, features, refactors, migrations, or other production-code changes that must keep specs, implementation evidence, and tests synchronized. Supports --plan-only, --impl-only, and --auto."
---

# Spec Plan Implement

<objective>
Plan → Implement → Update Evidence → Verify.

Start from `specs/`, implement against the documented intent, and finish only after the affected
FR/BR IDs retain direct links to implementation tasks, code/modules, and tests.

Flags:
- `--plan-only`: stop after the grounded plan.
- `--impl-only`: execute the already-approved plan in the current session.
- `--auto`: skip confirmation between planning and implementation.
</objective>

<repository_scope>
Treat `.agents/skills/spec-plan-implement/` as the canonical copy of this workflow for the current
repository. Run commands from the Git repository root so relative `specs/`, `tools/`, and Gradle
paths resolve consistently.

Treat the repository's `AGENTS.md`, `specs/traceability-policy.md`, and
`tools/traceability_guard.py` as authoritative. Update this checked-in skill when the workflow
changes; do not synchronize it with a user-level copy.
</repository_scope>

<traceability_contract>
Treat traceability as a release condition, not a documentation follow-up.

For every production-code change:
1. Identify the affected `FR-*` / `BR-*` IDs before editing code.
2. Link each ID directly in the implementation task, code mapping, and test spec.
3. Update the relevant row in `specs/business-rules-mapping.md`; a timestamp-only edit never counts.
4. Keep coverage and registry summaries consistent and non-regressing.
5. Do not report completion while the project traceability guard fails.

When `specs/traceability-policy.md` exists, read it completely and follow it. Project policy wins
over generic examples in this skill.
</traceability_contract>

<harness>
Prefer the project-owned deterministic harness when present:

```text
tools/traceability_guard.py
```

Before source edits, create a temporary baseline outside the repository:

```powershell
python tools/traceability_guard.py snapshot --output <temporary-baseline.json>
```

This baseline isolates changes made by the current workflow from a pre-existing dirty worktree.
Keep the path in session context and remove the temporary file after final verification.

If no project harness exists, record the initial Git diff/status, perform the same checks manually,
and include creation of a deterministic guard in the plan when the repository requires recurring
traceability enforcement.
</harness>

<process>

## Phase 1 — Spec-grounded planning

### 1. Establish baseline and discover specs

1. Resolve the Git repository root and run the workflow from it.
2. Inspect `git status` without modifying or discarding existing user changes.
3. Run the traceability snapshot command when the harness exists.
4. List `specs/features/` and match every affected feature.
5. Always read:
   - `AGENTS.md`, when present;
   - `specs/traceability-policy.md`, when present;
   - `specs/business-rules-mapping.md` rows for affected IDs/modules;
   - each affected `feature-spec.md`;
   - each affected `technical-design.md`;
   - each affected `implementation-tasks.md`;
   - each affected `test-spec.md`.
6. Read `specs/architecture.md`, `specs/data-model.md`, and `specs/index.md` when the change is
   cross-cutting or affects their claims/counts.
7. For a new product behavior with no matching feature, ask whether to create a feature spec or
   proceed ungrounded. For engineering-governance/tooling changes, use or create a cross-cutting
   policy spec instead of inventing a product feature.

### 2. Scan source with requirement context

1. Resolve exact source paths and symbols from technical design and mapping rows.
2. Read affected production and test files.
3. Identify the gap between current behavior, spec intent, and traceability evidence.
4. Preserve unrelated dirty-worktree changes.

### 3. Produce the plan

Include all sections below:

```markdown
## Plan: [Title]

### Root Cause / Goal
[Spec-grounded explanation]

### Relevant Specs
- [path + section + rule]

### Implementation Steps
| # | File | Change | Depends on |
|---|------|--------|------------|

### Traceability Change Set
| Requirement / Rule | Work Item | Code / Module | Test Evidence | Mapping Row |
|--------------------|-----------|---------------|---------------|-------------|

### Spec Updates Required
| Spec File | Section | Change Type |
|-----------|---------|-------------|

### Verification
- [ ] Focused tests
- [ ] Build / full relevant tests
- [ ] Traceability audit
- [ ] Traceability change-set guard
```

Every planned production file must appear in at least one Traceability Change Set row.

If `--plan-only`, stop after the plan. If `--auto`, continue. Otherwise request confirmation before
editing.

## Phase 2 — Implement and update evidence

### 4. Implement code and tests

1. Re-read each target before editing.
2. Apply scoped changes and add/update focused automated tests.
3. Run focused compilation/tests early enough to catch implementation errors.
4. Do not overwrite or revert unrelated user changes.

### 5. Update traceability in the same workflow

For each affected ID, update only relevant sections, in this order:

1. `feature-spec.md` when intent, FR/BR, or acceptance criteria changed.
2. `technical-design.md` for behavior, architecture, data flow, scheduling, or failure handling.
3. `implementation-tasks.md` with the exact FR/BR ID on the implemented work item.
4. `business-rules-mapping.md` with every changed production filename and current symbol.
5. `test-spec.md` with the same ID, test case IDs, implementation evidence, and result/status.
6. `architecture.md` / `data-model.md` for cross-cutting changes.
7. Mapping Coverage Summary and `specs/index.md` when registry counts change.

Preserve formatting and ID sequences. Use the next sequential test/task ID. Mark a task complete
only after implementation exists. Never satisfy the guard by changing only metadata, dates, broad
directory names, or unrelated registry rows.

### 6. Verify the complete change

Run, in order:

```powershell
python -m unittest discover -s tools/tests -p 'test_*.py' -v
python tools/traceability_guard.py audit
python tools/traceability_guard.py guard --baseline <temporary-baseline.json>
./gradlew :app:compileDebugKotlin
./gradlew :app:testDebugUnitTest
```

Adapt build/test commands to the project when it is not Android. If a command fails, diagnose,
fix, and rerun. Do not weaken or bypass the guard. Remove the temporary baseline after it passes.

### 7. Final report

Report:
- code files and behavioral changes;
- affected FR/BR IDs and their work/code/test links;
- spec and mapping rows updated;
- harness, build, and test results;
- manual verification still pending.

</process>

<notes>
- Identify every affected feature for cross-feature changes.
- A passing structural guard does not prove the mapping is semantically correct; review changed
  rows against the code diff.
- Do not use a broad waiver for legacy debt. Snapshot mode isolates pre-existing debt while still
  preventing the current task from making it worse.
</notes>
