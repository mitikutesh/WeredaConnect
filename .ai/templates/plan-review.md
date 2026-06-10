<!-- TEMPLATE: plan review. Authored by Codex (Independent Reviewer). -->
<!-- Filled copy lives at .ai/runs/<run_id>/02-codex-plan-review.md -->
<!-- (and 04-codex-plan-validation.md for the validation pass). -->

# Codex Plan Review — <task title>

- **Run:** <run_id>
- **Reviewer:** Codex (Independent Reviewer)
- **Reviewing:** `01-draft-plan.md`
- **Date:** <ISO date>

## Assessment

One paragraph: is the plan sound, complete, and rule-compliant? Headline risks.

## Findings

<!-- Repeat per finding. Omit the section entirely if there are none. -->

### [F1] <short title>
- **Classification:** Critical | Important | Optional | Invalid
- **Description:** what is wrong / missing / risky
- **Reasoning:** why it matters — cite a rule, contract, or failure mode
- **Recommended action:** what the plan should do instead
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
- **Reasoning:** accept Claude's rebuttal, or restate why the finding stands.

```
UPHELD: <comma-separated ids, empty if none>
DISPUTE_STATUS: NONE | OPEN
```
