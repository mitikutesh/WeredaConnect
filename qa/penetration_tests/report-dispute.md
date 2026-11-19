# Adversarial personas: dev stack, 2026-11-19 (run `frd17-11191403`)

FRD-17. Script `scripts/e2e_fraud_personas.py`, API `https://vhhm8gh9c1.execute-api.eu-north-1.amazonaws.com`, started 2026-11-19 14:03:18Z, 55 HTTP calls. Expectations come from `specs/architecture/fraud-detection-architecture.md` (sections 2, 14 to 18) and the server code on `main` at the time of the run.

Observed signals are the rows `GET /admin/fraud-signals` returned for each persona account, read for both statuses (`pending` and `reviewed`). The score and tier are the server's, from the same responses' `users[]` (FRD-07); a replica of section 14.2 recomputes them from the rows as a cross-check, and a disagreement is flagged (a server without `users[]` leaves the replica's numbers). The gates the persona met cross-check the tier: a payment with a fresh password passes at LOW and MEDIUM, meets `AUTH_041` at HIGH and `FRAUD_ACCOUNT_HELD` at CRITICAL; a create meets `FRAUD_ACCOUNT_HELD` only at CRITICAL.

## Summary

| Persona | Accounts | Expected signals | Observed signals | Tier exp / obs | Challenge (expected) | Hold (expected) | Verdict |
|---|---|---|---|---|---|---|---|
| Confirm-then-dispute buyer | dispute-buyer | DISPUTE_RATE | DISPUTE_RATE | LOW / LOW (10, server) | AUTH_040 only when the password is stale; disputes under 10,000 ETB need no step-up | none (LOW); each disputed escrow is frozen by the dispute itself | DETECTED, NOT ENFORCED (flag only) |

Parenthesised signals are data dependent (the price rule needs 5 or more recent listings of the type with a spread).

## Confirm-then-dispute buyer (`dispute`)

Threat: buyer griefing: keep the goods and claw the money back. What should catch it today: FRD-09 DISPUTE_RATE (live, 30 percent of 5 or more paid orders); the confirm-then-dispute pattern itself is FRD-15 (planned).

Plan:

- order 5 times from the support farmer and pay each (mock gateway)
- confirm 2 deliveries (the GPS spoofer's trucker delivery when that persona runs, and a self-pickup)
- dispute both right after confirming

| Account | Action | Expected | Got | OK |
|---|---|---|---|---|
| dispute-buyer | pay order 1 (20 kg teff, fresh password) | accepted (LOW) | success | yes |
| dispute-buyer | pay order 2 (20 kg teff, fresh password) | accepted (LOW) | success | yes |
| dispute-buyer | pay order 3 (20 kg teff, fresh password) | accepted (LOW) | success | yes |
| dispute-buyer | pay order 4 (20 kg teff, fresh password) | accepted (LOW) | success | yes |
| dispute-buyer | pay order 5 (20 kg teff, fresh password) | accepted (LOW) | success | yes |
| dispute-buyer | confirm self-pickup of order 7cf6daac | delivered | delivered | yes |
| dispute-buyer | confirm self-pickup of order 07b2a89f | delivered | delivered | yes |
| dispute-buyer | dispute confirmed delivery 1 of 2 (escrow 6939b1c5) | accepted (disputes are not tier-gated) | HTTP 200  | yes |
| dispute-buyer | dispute confirmed delivery 2 of 2 (escrow b0c14ff8) | accepted (disputes are not tier-gated) | HTTP 200  | yes |

**dispute-buyer** (`b06c39bc-e031-7056-502c-24e027211b9d`): signals DISPUTE_RATE; score 10, tier LOW from the server (expected LOW; replica 10, LOW).
- `DISPUTE_RATE` (weight 10, FRD-09): TransactionPatternService.CheckDisputeRateAsync via EscrowFunction.HandleDisputeAsync. seen.

Verdict: **DETECTED, NOT ENFORCED (flag only)**.

## Support accounts (counterparties, should stay clean)

- support-farmer (farmer, `708cc9ac-20f1-706c-d69b-5b2ebfc73201`): signals none, tier LOW.

## Gates and holds: where they live

- `FRAUD_ACCOUNT_HELD`: RiskGatePolicy (FRD-04): CRITICAL refuses crop, trip, order create and payments.
- `AUTH_041`: StepUpPolicy.CheckPayment (FRD-04): HIGH asks a fresh Fayda login at any amount.
- `AUTH_040`: StepUpPolicy.CheckPayment: a fresh password for a payment up to 10,000 ETB (LOW and MEDIUM).
- `held`: SettlementRiskGate / PartySuspensionGate + EscrowService.HoldForReviewAsync in the EscrowCronFunction sweeps (FRD-03, FRD-04).

## Left on dev

- support farmer teff listing `a5862ca8-ae99-478b-89fb-26530afb3cca` (orders reference it)
- disputed escrow `escrow-order-7cf6daac-bd43-431e-ba3b-31826939b1c5` in the admin dispute queue
- disputed escrow `escrow-order-07b2a89f-c848-4bf7-bada-de11b0c14ff8` in the admin dispute queue

## Accounts

| Label | Role | User id |
|---|---|---|
| dispute-buyer | buyer | `b06c39bc-e031-7056-502c-24e027211b9d` |
| support-farmer | farmer | `708cc9ac-20f1-706c-d69b-5b2ebfc73201` |

Phones (from +25190017XXXX) and the random passwords are kept only in the `--state-file` when one was given.

Overall: every persona matched its expectation.
