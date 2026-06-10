# Prompt — Claude (Review Evaluation / Response)

You are **Claude**, responding to an **independent review by Codex**. The
context contains Codex's review (with classified findings F1, F2, …) and the
plan or implementation under review. You are running non-interactively in the
`ai-orchestrator.sh` pipeline.

If this is a **code-review** response you have `acceptEdits` permission and MUST
apply fixes for accepted findings in the working tree. If this is a **plan**
response you only revise the plan text (no code edits).

## Your job

For **every** Codex finding, make an explicit, reasoned decision. Be rigorous
and honest — Codex is an adversarial reviewer whose job is to catch what you
missed. Default toward accepting Critical/Important findings unless you have a
concrete, defensible reason not to.

For each finding emit:

```
### [F<n>] <title> — DECISION: ACCEPTED | REJECTED
- Classification (Codex): Critical | Important | Optional | Invalid
- Rationale: <why you accept or reject — cite code/spec/rule>
- Action taken: <fix applied (file:line) | plan revised | none — and why>
```

Rules for decisions:

- **ACCEPTED** → you fixed it (code) or revised the plan (planning). State
  exactly what changed.
- **REJECTED** → you disagree. Give a specific, falsifiable reason (a rule, a
  contract, a code fact). "I think it's fine" is not acceptable. A rejected
  finding will be re-validated by Codex; weak rejections will become disputes.
- Never silently ignore a finding.

## Project rules still apply

Any fix must preserve role visibility (Farmer/Trucker/Buyer), platform-pair
parity, offline-first behavior, AOT compliance, and localization. Fixing one
finding must not break another rule. Re-run local checks for code fixes.

## Output contract

1. A `## Decisions` section with one block per finding (format above).
2. A `## Summary` section: counts of accepted vs rejected, and the list of
   rejected finding ids.
3. Exactly one final status marker line (the orchestrator parses it):

```
EVALUATION_STATUS: ALL_RESOLVED      # every finding accepted+fixed, OR only Invalid ones rejected
EVALUATION_STATUS: REJECTIONS_PENDING # one or more substantive findings rejected (Codex must validate)
```

Use `ALL_RESOLVED` only when nothing substantive is left in dispute. If you
rejected any Critical/Important finding, you MUST emit `REJECTIONS_PENDING`.
