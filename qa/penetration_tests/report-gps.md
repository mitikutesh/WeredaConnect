# Adversarial personas: dev stack, 2026-11-19 (run `frd17-11191359`)

FRD-17. Script `scripts/e2e_fraud_personas.py`, API `https://vhhm8gh9c1.execute-api.eu-north-1.amazonaws.com`, started 2026-11-19 13:59:07Z, 33 HTTP calls. Expectations come from `specs/architecture/fraud-detection-architecture.md` (sections 2, 14 to 18) and the server code on `main` at the time of the run.

Observed signals are the rows `GET /admin/fraud-signals` returned for each persona account, read for both statuses (`pending` and `reviewed`). The score and tier are the server's, from the same responses' `users[]` (FRD-07); a replica of section 14.2 recomputes them from the rows as a cross-check, and a disagreement is flagged (a server without `users[]` leaves the replica's numbers). The gates the persona met cross-check the tier: a payment with a fresh password passes at LOW and MEDIUM, meets `AUTH_041` at HIGH and `FRAUD_ACCOUNT_HELD` at CRITICAL; a create meets `FRAUD_ACCOUNT_HELD` only at CRITICAL.

## Summary

| Persona | Accounts | Expected signals | Observed signals | Tier exp / obs | Challenge (expected) | Hold (expected) | Verdict |
|---|---|---|---|---|---|---|---|
| GPS spoofer | gps-trucker | GPS_MOCKED, GPS_JUMP, IMPOSSIBLE_DELIVERY_TIME | GPS_JUMP, GPS_MOCKED, IMPOSSIBLE_DELIVERY_TIME | MEDIUM / MEDIUM (50, server) | none (a trucker makes no payment; trips stay allowed below CRITICAL) | none (below CRITICAL); the payout waits the new-payee delay as for any new account | DETECTED, NOT ENFORCED (flag only) |

Parenthesised signals are data dependent (the price rule needs 5 or more recent listings of the type with a spread).

## GPS spoofer (`gps`)

Threat: T4 fake delivery to claim escrow (trucker). What should catch it today: LT-07 check-in signals and the FRD-09 delivery time rule (live); Rule 4 GPS_ANOMALY is written on the confirming buyer, never on the trucker.

Plan:

- list a trip Debre Birhan to Dire Dawa, its destination point in Dire Dawa; carry a paid order of the support farmer's teff
- pick up at the farm with the farmer's QR
- send two check-ins: the first flagged mocked by the phone, the second 50 km away 30 s later
- the buyer confirms the drop-off at Dire Dawa minutes after the pickup (about 250 km)

| Account | Action | Expected | Got | OK |
|---|---|---|---|---|
| gps-trucker | pick up at the farm with the farmer's QR | success | success | yes |
| gps-trucker | check-in flagged mocked by the phone | accepted (flag only) | success | yes |
| gps-trucker | check-in 50 km away 30 s later (6,000 km/h) | accepted (flag only) | success | yes |
| support-buyer | confirm the drop-off about 250 km away minutes after the pickup | accepted (the delivery time rule flags the trucker) | success | yes |

**gps-trucker** (`304cc9ac-b0b1-70f0-e232-f8254ed7f0ca`): signals GPS_JUMP, GPS_MOCKED, IMPOSSIBLE_DELIVERY_TIME; score 50, tier MEDIUM from the server (expected MEDIUM; replica 50, MEDIUM).
- `GPS_MOCKED` (weight 25, LT-07): SyncFunction.CheckInFraudSignalsAsync (SyncFunction.CheckInEffects.cs), check-in flagged mocked. seen.
- `GPS_JUMP` (weight 10, LT-07): SyncFunction.CheckInFraudSignalsAsync, neighbouring check-ins over 1 km apart at over 130 km/h. seen.
- `IMPOSSIBLE_DELIVERY_TIME` (weight 15, FRD-09): TransactionPatternService.CheckDeliveryTime via SyncFunction.TryRecordDeliveryPatternSignalsAsync, on the delivery's trucker. seen.
- `GPS_ANOMALY` must not be on this account (Rule 4: FraudDetectionService.CheckGpsConsistency, recorded on the caller of the delivered update (the buyer)): absent.

Verdict: **DETECTED, NOT ENFORCED (flag only)**.

## Support accounts (counterparties, should stay clean)

- support-buyer (buyer, `a01ca91c-80e1-70fd-8b51-9564c9710527`): signals none, tier LOW.
- support-farmer (farmer, `a0ccb98c-1061-70b5-8be7-073d758fa075`): signals none, tier LOW.

## Gates and holds: where they live

- `FRAUD_ACCOUNT_HELD`: RiskGatePolicy (FRD-04): CRITICAL refuses crop, trip, order create and payments.
- `AUTH_041`: StepUpPolicy.CheckPayment (FRD-04): HIGH asks a fresh Fayda login at any amount.
- `AUTH_040`: StepUpPolicy.CheckPayment: a fresh password for a payment up to 10,000 ETB (LOW and MEDIUM).
- `held`: SettlementRiskGate / PartySuspensionGate + EscrowService.HoldForReviewAsync in the EscrowCronFunction sweeps (FRD-03, FRD-04).

## Left on dev

- support farmer teff listing `9ab88ade-ec7e-46db-92c8-f6c41459899c` (orders reference it)
- GPS spoofer trip `6fe680e7-f1e6-402d-b90e-f66d121c4000` with delivered delivery `ed9f0d50-ee7a-4792-bfb5-d1013b4273c4`

## Accounts

| Label | Role | User id |
|---|---|---|
| support-buyer | buyer | `a01ca91c-80e1-70fd-8b51-9564c9710527` |
| support-farmer | farmer | `a0ccb98c-1061-70b5-8be7-073d758fa075` |
| gps-trucker | trucker | `304cc9ac-b0b1-70f0-e232-f8254ed7f0ca` |

Phones (from +25190017XXXX) and the random passwords are kept only in the `--state-file` when one was given.

Overall: every persona matched its expectation.
