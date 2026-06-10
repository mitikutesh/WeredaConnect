<!-- TEMPLATE: code review. Authored by Codex (Independent Reviewer). -->
<!-- Filled copy lives at .ai/runs/<run_id>/08-codex-code-review.md -->
<!-- (and 10-codex-review-validation.md for the validation pass). -->

# Codex Code Review — <task title>

- **Run:** <run_id>
- **Reviewer:** Codex (Independent Reviewer)
- **Reviewing:** `07-implementation.diff` against `05-final-plan.md`
- **Date:** <ISO date>

## Assessment

One paragraph: does the implementation match the plan, is it correct, and does
it respect every HARD rule (role visibility, platform pairs, AOT, offline)?

## Findings

<!-- Repeat per finding. Omit the section entirely if there are none. -->

### [F1] <short title>  (`file:line`)
- **Classification:** Critical | Important | Optional | Invalid
- **Description:** the defect / violation / gap
- **Reasoning:** why it matters — rule cited, failure mode, or broken caller
- **Recommended action:** the concrete fix
- **Risk level:** High | Medium | Low

### [F2] …

## Verdict

<!-- Exactly one of these — parsed by the orchestrator. -->
```
VERDICT: APPROVED | APPROVED_WITH_COMMENTS | CHANGES_REQUESTED
```

---

<!-- VALIDATION PASS (only when re-checking Claude's rejected findings) -->
## Validation of Rejected Findings

### [F<n>] — VALIDATION: CONCEDED | UPHELD
- **Reasoning:** accept Claude's fix/rebuttal, or restate why the finding stands.

```
UPHELD: <comma-separated ids, empty if none>
DISPUTE_STATUS: NONE | OPEN
```
