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

## Admin Playwright Specs (local only)

No CI workflow runs the Playwright suite in `e2e/`; it is run by hand. Five admin specs,
`e2e/admin/fraud-review.spec.ts` (FRD-07), `e2e/admin/dispute-evidence.spec.ts` (FRD-14),
`e2e/admin/legs-and-stuck-orders.spec.ts` (ESC-24: the per-leg verdict, the ledger view, Stuck Orders) and
`e2e/admin/reputation.spec.ts` (ESC-32: excuse and restrict on the user page, the appeals queue) and
`e2e/admin/manual-payout-claims.spec.ts` (FUP-05: claim a payout leg, another admin's claim, a duplicate reference), also
need a local admin build. They do not sign in (the admin pool needs TOTP): each seeds a fake Cognito session in
localStorage under the app client id `e2eadminclient` and answers every API call with `page.route`, so
they check what the page sends and shows, never the server. Against the deployed dev admin the seeded
session is not found, so they skip themselves unless `ADMIN_URL` is a localhost address (or
`ADMIN_COGNITO_CLIENT_ID` names the deployed build's client id).

```bash
# Terminal 1: build the admin with the client id the specs seed, and serve it (no .env needed, no network)
cd admin && npm ci
VITE_COGNITO_ADMIN_USER_POOL_ID=eu-north-1_e2eAdmin VITE_COGNITO_ADMIN_CLIENT_ID=e2eadminclient \
  VITE_API_BASE_URL=https://api.e2e.invalid npx vite build
npx vite preview --port 4173

# Terminal 2: run the five specs against it (first time only: npx playwright install chromium)
cd e2e && npm ci
ADMIN_URL=http://localhost:4173 npx playwright test --project=admin \
  admin/fraud-review.spec.ts admin/dispute-evidence.spec.ts admin/legs-and-stuck-orders.spec.ts \
  admin/reputation.spec.ts admin/manual-payout-claims.spec.ts
```

Expected: 13 passed (6 Fraud Review, 3 dispute evidence, 4 legs and stuck orders), a few seconds. The values on the build line
override `admin/.env` for that build only; rebuild with the real values (`npm run build`) before using
`admin/dist` for anything else. The other admin specs (`login`, `dashboard`, `navigation`) and the
`webapp` project run against the deployed dev sites by default (`e2e/playwright.config.ts`):
`cd e2e && npm run test:admin`.

## AI Advisory Reviewer (FRD-19, local only)

The reviewer Lambda (`server/fraud-ai-reviewer`, Node.js, no dependencies) has its own tests on Node's
built-in runner, with a fake Bedrock client: no AWS call, no `npm ci`, under a second. No CI workflow
runs them (the C# side, which validates every answer again, is under CI), so run them by hand after
any change in that folder:

```bash
cd server/fraud-ai-reviewer
npm run build   # node --check of every file
npm test        # node --test test/*.test.js
```

Expected: 25 tests pass, 0 fail. Needs Node 20 or newer.

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

### Outcome label backfill (FUP-143, one-off)

`scripts/backfill_outcome_labels.py` writes the label of every case resolved before FRD-18, or whose
live label write failed (`OUTCOME_LABEL_FAILED`), from the `fraud_approve`, `fraud_reject`,
`fraud_whitelist`, `admin_dispute_resolved`, `fraud_hold_release` and `fraud_hold_refund` rows of the
transaction-audit table. Dry run by default, `--apply` writes. Append only (a conditional put at the
live label's key, never an update or delete), and a case that already has its label is skipped, so it
can be run again at any time. It reads the audit table with one full Scan (there is no index on
`action`): about 1 read unit per 8 KB, a few cents for a million rows. Do not schedule it. Needs the
AWS CLI v2 with `dynamodb:Scan`, `Query` and `PutItem` on transaction-audit, `GetItem` on
fraud-signals and `Query` on escrows. Mapping and limits: section 20.1 of the fraud spec.

Run per environment (dev, then staging, then prod):

```bash
python3 -I scripts/test_backfill_outcome_labels.py                               # offline tests, run after editing the script
python3 -I scripts/backfill_outcome_labels.py --env dev > /tmp/backfill-dry.jsonl    # 1. dry run, writes nothing
#    2. read the summary on stderr: would_write per action and outcome, skipped per reason.
#       Check every `unresolvable` line in the file (its `detail` says why) before going on.
python3 -I scripts/backfill_outcome_labels.py --env dev --limit 5 --apply        # 3. a small first pass
python3 -I scripts/backfill_outcome_labels.py --env dev --apply > /tmp/backfill-apply.jsonl   # 4. the rest
python3 -I scripts/backfill_outcome_labels.py --env dev                          # 5. dry run again: no would_write left
python3 -I scripts/export_outcome_labels.py --env dev --out /tmp/labels.jsonl    # 6. verify: the labels are in the export
```

Exit codes: 0 done, 1 an AWS call or a write failed (fix the cause and run again), 2 usage, 3 the Scan
stopped at `--max-pages`. The JSON lines carry ids only. A backfilled row has `labelBackfill: FUP-143`
and `backfilledAt` on the table row; the export carries `labelBackfill` (empty on a live label), so
an analysis can tell the two apart. The summary also names every other `fraud_*` action it saw
(`fraud_hold`, or one the script does not know): counted, never labelled.
