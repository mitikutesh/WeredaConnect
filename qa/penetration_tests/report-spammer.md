# Adversarial personas: dev stack, 2026-11-19 (run `frd17-11191406`)

FRD-17. Script `scripts/e2e_fraud_personas.py`, API `https://vhhm8gh9c1.execute-api.eu-north-1.amazonaws.com`, started 2026-11-19 14:06:04Z, 20 HTTP calls. Expectations come from `specs/architecture/fraud-detection-architecture.md` (sections 2, 14 to 18) and the server code on `main` at the time of the run.

Observed signals are the rows `GET /admin/fraud-signals` returned for each persona account, read for both statuses (`pending` and `reviewed`). The score and tier are the server's, from the same responses' `users[]` (FRD-07); a replica of section 14.2 recomputes them from the rows as a cross-check, and a disagreement is flagged (a server without `users[]` leaves the replica's numbers). The gates the persona met cross-check the tier: a payment with a fresh password passes at LOW and MEDIUM, meets `AUTH_041` at HIGH and `FRAUD_ACCOUNT_HELD` at CRITICAL; a create meets `FRAUD_ACCOUNT_HELD` only at CRITICAL.

## Summary

| Persona | Accounts | Expected signals | Observed signals | Tier exp / obs | Challenge (expected) | Hold (expected) | Verdict |
|---|---|---|---|---|---|---|---|
| Listing spammer | spammer | FRAUD_DUPLICATE_LISTING, FRAUD_VELOCITY_EXCEEDED, (PRICE_ANOMALY) | DUPLICATE_LOT, FRAUD_DUPLICATE_LISTING, FRAUD_VELOCITY_EXCEEDED | MEDIUM / MEDIUM (35, server) | none (a farmer makes no payment); the duplicate and both creates after the 5th accepted listing are refused; creates stay allowed below CRITICAL (the 8th create attempt meets FRAUD_VELOCITY_EXCEEDED, not FRAUD_ACCOUNT_HELD) | none (below CRITICAL) | DETECTED AND ENFORCED; the replica disagrees with the server's score (deviates) |

Parenthesised signals are data dependent (the price rule needs 5 or more recent listings of the type with a spread).

## Listing spammer (`spammer`)

Threat: T1 listing spam, T2 duplicates, T3 price manipulation. What should catch it today: Rules 1 to 3 (live).

Plan:

- create 5 listings of 4 crop types, one teff at 999 ETB/kg (the price rule's range ends at 1,000)
- repost the first teff listing at a price 2 percent away (duplicate)
- create 2 more listings in the same hour (over the 5 per hour limit)

| Account | Action | Expected | Got | OK |
|---|---|---|---|---|
| spammer | create teff @ 40.0 ETB/kg | success | success | yes |
| spammer | create teff @ 40.8 ETB/kg | refused FRAUD_DUPLICATE_LISTING | FRAUD_DUPLICATE_LISTING: A similar listing already exists. Please update your existing listing instead. | yes |
| spammer | create wheat @ 22.0 ETB/kg | success | success | yes |
| spammer | create barley @ 26.0 ETB/kg | success | success | yes |
| spammer | create maize @ 18.0 ETB/kg | success | success | yes |
| spammer | create teff @ 999.0 ETB/kg | success | FRAUD_DUPLICATE_LISTING: A similar listing already exists. Please update your existing listing instead. | NO |
| spammer | create chickpea @ 60.0 ETB/kg | refused FRAUD_VELOCITY_EXCEEDED | success | NO |
| spammer | create lentils @ 70.0 ETB/kg (8th create attempt, the tier gate runs first) | refused FRAUD_VELOCITY_EXCEEDED, not FRAUD_ACCOUNT_HELD (below CRITICAL) | FRAUD_VELOCITY_EXCEEDED: Maximum 5 listings per hour exceeded. | yes |

**spammer** (`705c591c-6021-7018-6441-58e09f3146d9`): signals DUPLICATE_LOT, FRAUD_DUPLICATE_LISTING, FRAUD_VELOCITY_EXCEEDED; score 35, tier MEDIUM from the server (expected MEDIUM; replica 45, MEDIUM, DISAGREES).
- `FRAUD_DUPLICATE_LISTING` (weight 20, Rule 1): FraudDetectionService.CheckListingCreationAsync, recorded by the SyncFunction crop create. seen.
- `FRAUD_VELOCITY_EXCEEDED` (weight 15, Rule 3): FraudDetectionService.CheckListingCreationAsync (5 listings per hour), SyncFunction crop create. seen.
- `PRICE_ANOMALY` (weight 10, Rule 2): FraudDetectionService.CheckListingCreationAsync (more than 3 robust spreads from the median of the last 50 listings of the type: 1.4826 x MAD, floored at 10% of the median), flag only. not seen (data dependent).
- Also written: DUPLICATE_LOT.

Verdict: **DETECTED AND ENFORCED; the replica disagrees with the server's score**.

## Gates and holds: where they live

- `FRAUD_ACCOUNT_HELD`: RiskGatePolicy (FRD-04): CRITICAL refuses crop, trip, order create and payments.
- `AUTH_041`: StepUpPolicy.CheckPayment (FRD-04): HIGH asks a fresh Fayda login at any amount.
- `AUTH_040`: StepUpPolicy.CheckPayment: a fresh password for a payment up to 10,000 ETB (LOW and MEDIUM).
- `held`: SettlementRiskGate / PartySuspensionGate + EscrowService.HoldForReviewAsync in the EscrowCronFunction sweeps (FRD-03, FRD-04).

## Accounts

| Label | Role | User id |
|---|---|---|
| spammer | farmer | `705c591c-6021-7018-6441-58e09f3146d9` |

Phones (from +25190017XXXX) and the random passwords are kept only in the `--state-file` when one was given.

Overall: at least one persona deviated.
