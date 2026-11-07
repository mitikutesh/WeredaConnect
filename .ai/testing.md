# Testing Strategy

## Philosophy

1. **Offline-First Testing** — Test offline scenarios BEFORE online scenarios
2. **Test Pyramid** — Many unit tests, fewer integration tests, minimal E2E tests
3. **Fast Feedback** — Unit tests <1 min, integration tests <5 min
4. **Deterministic** — No flaky tests, same results every run
5. **Isolated** — Tests don't depend on each other or external state

## Testing Priorities (WeredaConnect-specific)

1. **Offline functionality** — Critical for Ethiopian context
2. **Sync conflict resolution** — Data integrity is paramount
3. **Low-end device performance** — 2GB RAM, battery, storage
4. **Multi-language support** — Amharic, Afan Oromo, English
5. **Security** — Authentication, authorization, data privacy

## Test Types

| Type | What | Coverage Target | Speed |
|------|------|-----------------|-------|
| **Unit** | Individual functions/classes in isolation | Mobile >80%, Server >75%, Infra >70% | <1 min |
| **Integration** | Multiple components together (repos, sync, API+DB) | Critical user flows | <5 min |
| **E2E** | Complete user flows from UI to backend | 5-10 critical paths only | <15 min |
| **Smoke** | Quick validation of critical functionality | 5-10 critical paths | <2 min |

## Coverage Exceptions

- Don't unit test: trivial getters/setters, framework code, generated code (Isar, JSON serializers)
- Don't E2E test: every feature (too slow/brittle), edge cases, UI variations
- **100% coverage required**: sync engine, conflict resolution, IAM policies

## Component-Specific Guides

- `mobile/TESTING.md` — Flutter testing patterns, offline-first test examples, widget tests
- `server/TESTING.md` — .NET AOT-compatible testing, NSubstitute patterns, LocalStack integration
- `infra/TESTING.md` — CDK construct tests, IAM policy validation, snapshot tests

## Adversarial Fraud Personas (dev, FRD-17)

`scripts/e2e_fraud_personas.py` drives six abusive users against the dev API (listing spammer,
self-trader, IDOR prober, payer mismatch, GPS spoofer, confirm-then-dispute buyer), each on accounts
it registers itself through the mock Fayda provider (never the shared test users), and reports per
persona the expected and observed fraud signals, risk tier, step-up (`AUTH_040`/`AUTH_041`),
`FRAUD_ACCOUNT_HELD` and escrow hold. Details, run rules and limits: section 11.6 of
`specs/architecture/fraud-detection-architecture.md`.

```bash
python3 scripts/e2e_fraud_personas.py --self-test   # offline checks, run after editing the script
python3 scripts/e2e_fraud_personas.py --dry-run     # plan only
python3 scripts/e2e_fraud_personas.py --state-file /tmp/frd17-state.json   # run against dev (9 registrations)
python3 scripts/e2e_fraud_personas.py --recheck /tmp/frd17-state.json      # later: tiers and held escrows, exit 1 on a missing hold
```

## Outcome Label Export (FRD-18)

`scripts/export_outcome_labels.py` exports the labelled outcomes of resolved fraud and dispute cases
(Fraud Review decisions, dispute verdicts, held escrow exits) as JSONL or CSV, ids only, with one
paged Query of the transaction-audit `EntityIndex`. Row format, run rules and limits: section 20.1 of
`specs/architecture/fraud-detection-architecture.md`.

```bash
python3 -I scripts/test_export_outcome_labels.py                  # offline tests, run after editing the script
python3 -I scripts/export_outcome_labels.py --env dev --dry-run   # print the AWS call only
python3 -I scripts/export_outcome_labels.py --env dev --out /tmp/labels.jsonl   # needs AWS credentials
```
