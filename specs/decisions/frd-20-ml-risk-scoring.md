# ADR: ML risk scoring from outcome labels (FRD-20)

**Date**: 2026-11-07
**Status**: Deferred until the dataset trigger below is met (prod: 250 fraud and 250 honest examples with feature snapshots for a fit of at most 10 parameters, from 40 users each, no user above 10 percent of a class, spanning at least 182 days, with 50 of each in the last 60 days). Nothing is built. Until then the hand weights of section 15 stay as they are, tuned by hand from the per-type table (approach 1).
**Ticket**: FRD-20 (EPIC FRD, story S6, P3)
**Spec**: `specs/architecture/fraud-detection-architecture.md` sections 14 (score), 15 (weights), 16 and 17 (tiers and enforcement), 20.1 (outcome labels), 21 (AI advisory boundary)

## Question

The signal weights of section 15 were set by hand. FRD-18 now records the admin's decision on every resolved case (section 20.1). When is there enough of it to learn the weights instead, how should a learned score be built and deployed so the rules stay authoritative, how is it judged, and how is it undone?

## Where the data stands

- A `fraud_signal` label (Fraud Review approve, reject, whitelist) carries the subject user, the one decided signal's type, entity and time, and `final` for the latest decision of the case. Labels start with FRD-18; earlier decisions have none.
- **Missing: the features.** The label does not record the user's other signals at decision time, and signal rows expire after 30 days (TTL). A training row built later cannot see what the admin saw. Prerequisite P1 below adds a feature snapshot to the label; until it ships, no example for a model accrues, and only the per-type table (approach 1) can be computed.
- `held_escrow` and `dispute` labels are not training targets (decided below).

## Trigger to start

An example is one subject user on one UTC day: the day of the final `fraud_signal` labels of that user, positive when any of them is `fraud_confirmed`, honest when all are `false_positive` or `whitelisted`. An admin usually decides several signals of one user at once, and those rows share their features, so counting raw labels would overstate the data. Its features are the P1 snapshot of that user's first decision of that day (any label, final or not); an example without one does not count.

| Condition (all must hold) | Minimum | Why |
|---------------------------|---------|-----|
| Examples of the smaller class | 250 at p of 10 or less; else 20 x p + 50 | 20 per parameter to fit, 50 to test (below) |
| Examples of the other class | the same | the class sizes are unknown, so both are counted |
| Distinct users in each class | 40 | one ring or one stuck client must not be a whole class |
| Largest share of one user in a class | 10 percent | one user's many decision days must not dominate the fit |
| Time span, first to last example | 182 days | covers part of the Meher harvest (about October to January) and the Belg season, and lets the test window be months the fit never saw |
| Last 60 days (the test window) | 50 of each class | power of the comparison, below |
| Source | prod only | dev holds the FRD-17 personas and the test users; staging is synthetic |

Why these numbers. The recommended model is a logistic regression; p is the number of its coefficients, the intercept not counted. Stable coefficients need about 10 to 20 examples of the smaller class per coefficient (events per variable: Peduzzi et al., 1996, give 10 as a floor; Riley et al., 2020, usually ask for more), so at 20 per coefficient and p of 10, 200 in training. The first fit keeps p at 10 or less by pooling (Training data, below); a fit with more coefficients waits for 20 x p + 50 of the smaller class. The test window needs 50 per class: a precision or recall near 0.7 measured on 50 cases is known to about plus or minus 0.13 (95 percent binomial interval), and on 25 cases to about plus or minus 0.18, which cannot tell a better weighting from noise.

### Measuring the trigger

With credentials that may Query the prod transaction-audit table (section 20.1). The export writes outside any git checkout (it refuses otherwise) and the file is deleted afterwards. The export reads every label (no `--latest-only`: the snapshot sits on the day's first decision, which may not be final); a stop at `--max-pages` (exit 3) ends the chain before anything is counted. The trigger counts only examples whose first decision of the day carries `snapshotVersion`, a field P1 adds to the label and the export; before P1 it reports none. The per-type table (approach 1) uses every final label, snapshot or not.

```bash
mkdir -p ~/wc-labels && python3 -I scripts/export_outcome_labels.py --env prod --max-pages 500 \
    --out ~/wc-labels/labels.jsonl && python3 -I - ~/wc-labels/labels.jsonl <<'EOF'
import collections, datetime, json, math, sys
rows = [json.loads(line) for line in open(sys.argv[1], encoding="utf-8") if line.strip()]
rows = [r for r in rows if r["case"] == "fraud_signal" and r.get("subjectUserId") and r.get("labelledAt")]
user_day = lambda r: (r["subjectUserId"], r["labelledAt"][:10])
first = {}  # (user, day) -> that day's first decision, final or not
for r in sorted(rows, key=lambda r: r["labelledAt"]):
    first.setdefault(user_day(r), r)
examples = collections.defaultdict(bool)  # (user, day) -> any final fraud_confirmed
for r in rows:
    if r["final"]:
        examples[user_day(r)] |= r["outcome"] == "fraud_confirmed"
snap = {k: v for k, v in examples.items() if first[k].get("snapshotVersion")}
day = lambda k: datetime.date.fromisoformat(k[1])
if snap:
    start, last = min(map(day, snap)), max(map(day, snap))
    for name, cls in (("fraud", True), ("honest", False)):
        keys = [k for k, v in snap.items() if v == cls]
        users = collections.Counter(k[0] for k in keys)
        top = max(users.values(), default=0) / max(len(keys), 1)
        print(f"{name}: examples {len(keys)} (need 250 at p <= 10), users {len(users)} (need 40), "
              f"largest user {top:.0%} (at most 10%), last 60 days "
              f"{sum(day(k) > last - datetime.timedelta(days=60) for k in keys)} (need 50)")
    print(f"span {start} to {last}: {(last - start).days} days (need 182)")
else:
    print("no example with a feature snapshot yet (P1)")
per_type = collections.defaultdict(bool)  # (user, day, type) -> any final fraud_confirmed
for r in rows:
    if r["final"]:
        per_type[user_day(r) + (r.get("signalType"),)] |= r["outcome"] == "fraud_confirmed"
totals = collections.defaultdict(lambda: [0, 0])
for (_, _, t), confirmed in per_type.items():
    totals[t][0] += confirmed
    totals[t][1] += 1
z = 1.96  # Wilson 95 percent interval
for t, (c, n) in sorted(totals.items(), key=lambda x: -x[1][1]):
    p = c / n
    mid = (p + z * z / (2 * n)) / (1 + z * z / n)
    half = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / (1 + z * z / n)
    hint = "under 30, not judged" if n < 30 else "lower?" if mid + half < 0.25 else "raise?" if mid - half > 0.75 else ""
    print(f"  {t}: {c} of {n} confirmed, 95% {mid - half:.2f} to {mid + half:.2f} {hint}")
EOF
rm -f ~/wc-labels/labels.jsonl
```

The last block is the per-type table of approach 1, counted once per user, day and type. Who and when: the owner, quarterly, recorded in the review log at the end.

## Options, ranked by cost and maintainability

| # | Approach | Runtime | Upkeep | Needs |
|---|----------|---------|--------|-------|
| 1 | **Per-type precision, no model.** The confirmed share per signal type from the export; a type with 30 or more (user, day) groups whose 95 percent interval lies wholly below 25 percent confirmed is a candidate to lower, wholly above 75 percent a candidate to raise, by hand through the usual spec and code change | none | a quarterly read | labels only (available now) |
| 2 | **Offline logistic re-fit of the weights.** Fit on the snapshots, turn the coefficients into integer points per type, load them as a weights table in configuration; `RiskScoreCalculator` sums a second score over the rows it already reads, with the same dedup, per-entity counting, caps, whitelist and credits | arithmetic in the AOT Lambdas, no ML runtime, no new read or resource | one offline script, a re-fit at most quarterly | P1 and the trigger |
| 3 | **A separate model scoring service** (gradient boosted trees or similar in a non-AOT Python Lambda, asynchronous like FRD-19, or called from the gates) | a model file, a feature pipeline, a second runtime; called from the gates it puts a cold start on money paths | dependency upgrades, model versions, per-decision explanations for admins | thousands of labels to beat approach 2 reliably |
| 4 | **A managed endpoint** (SageMaker real-time or serverless inference) | an always-on endpoint bills idle capacity every hour; serverless has cold starts | a second platform | as 3 |

Approach 1 is rule tuning, not ML, and is ignorant of co-occurring signals and account context; it is the bridge until the trigger. Approach 2 learns from the same counted signals the rules use, so its output is explainable the way the score is today (points per type), stays inside the AOT constraints (`server/AOT_COMPLIANCE.md`) and adds no read, table, Lambda or model runtime.

## Decision

- **Now**: approach 1 from the per-type table, quarterly. A resulting change is a hand rule change (section 15 and `FraudDetectionService.SignalWeight`), reviewed like any rule. A changed weight applies to rows written after it, since the calculator prefers the stored `weight` (section 14.2), so it takes up to 30 days to apply fully.
- **Ship P1 early**: the trigger clock starts with it.
- **At the trigger**: approach 2, through the phases below. The rules' score stays the score that gates, holds and delays.
- **Targets**: final `fraud_signal` labels only. `held_escrow` labels are not targets or features: a hold exists only because the rules made a party CRITICAL or an admin suspended one, so it would teach the model the rules back. `dispute` verdicts are not either: `buyer_wins` or `farmer_wins` judges the trade's quality, not fraud. Held labels serve only as a sanity check of a fitted score (the refund share by score band).
- **Approach 3** is reconsidered only after approach 2 has run in priority mode for two quarters, its test-window average precision has stopped improving between re-fits, and there are at least 2,000 labels. Approach 4 is not considered (cost, a second platform for a solo maintainer).

## Training data

**Features** (from the P1 snapshot of the day's first decision):

- per signal group, the units the calculator counts in the 30-day window before that decision: after dedup and per-entity counting, with resolutions and the whitelist as they stood before it, **uncapped**. The caps of section 14.2 are in points; they stay in the calculator and apply to the fitted points when a score is computed, which is how the evaluation computes it too.
- the clean-delivery credits counted, one feature.
- **Pooling to p of 10 or less**: each type carried by fewer than 30 examples goes into "other hard" or "other soft"; while p is above 10, the rarest remaining type joins its class pool, then the two pools merge. A pooled type gets its pool's points.
- **Controls**, not in the first fit: account age in days and completed escrows (the FRD-13 trust inputs), Fayda verified and role (the users row). Together 5 coefficients (role as two indicators). A second fit adds them only once the smaller class reaches 20 x p + 50 for the larger p; they enter the deployed score only by a separate decision, since the calculator does not read the users row or the escrows today and that would add reads at every gate. Role and the Fayda flag group the fairness check (bar 5) in any case.

Ids only: the subject user id groups the examples and is dropped before the fit. **Never** used: phone numbers, Fayda numbers, names, `labelReason` or any free text, location (woreda, region, GPS points), language, crop type, device ids, IP addresses (location, language and crop also stand in for region and ethnicity); the rules' score, tier and enforcement score (the thing being replaced, and what the admin saw); `AI_ADVISORY` rows (FRD-19's output); `SUSPENDED_TOKEN_USE` (written because of a suspension, often the reject being learned).

**Label leakage and bias:**

| Risk | Effect | Handling |
|------|--------|----------|
| Labels exist only where the rules flagged a user | the fit learns to rank flagged users and cannot learn fraud the rules miss; honest examples are flagged honest users, not the population | stated scope: approach 2 re-weights known signals and adds no detection; the population false positive rate comes from shadow mode, not the offline fit; a random audit sample is an open decision |
| The admin decides with the rules' score and tier in view (FRD-07), and with AI advice once FRD-19 is on | decisions anchor on today's weights and a re-fit drifts toward them | score, tier and advice are never features; every result is judged against the hand weights on the same examples; one reviewer (the owner) labels everything, which the report states |
| The decision changes the features | an approve or whitelist stops rows counting (the whitelist silences every soft type), so a snapshot taken after the decision gives honest examples fewer units; a reject suspends the user (FRD-05), producing `SUSPENDED_TOKEN_USE`, holds and no further trades | P1 reads before the decision's side effects and its stamp; the example uses the day's first decision only, before any decision of that day; `SUSPENDED_TOKEN_USE` left out |
| Changes of mind | reject then approve on one signal | the outcome comes from final labels only |
| One user, many rows | correlated examples, optimistic metrics | one example per user and day; no user above 10 percent of a class (trigger); split and resample by user, never a user on both sides of the split |
| Rules change over time | a type added later (FRD-08, LT-07, FRD-09, FRD-15 in October and November 2026) reads 0 before it existed | train only on examples after every type in the feature set existed, or pool the type; the report says which |
| Test traffic | personas and test users look like fraud | prod only; the three test users excluded |

**Class imbalance**: the confirmed share is unknown, and since Fraud Review sees what the rules flag either class may be the smaller. The trigger counts each class on its own. Fit unweighted when the classes are within 1 to 3; otherwise weight by inverse class frequency and then reset the intercept to the observed share, so probabilities stay calibrated. Judge by average precision and by precision and recall at the thresholds, never by accuracy.

**Points from the fit**: scale all coefficients by one factor so that, on the training examples, the share of honest examples at HIGH or above equals the hand weights' share (the thresholds keep their meaning and the step-up load does not jump); round to integers; clamp each to 0 to 50 (a signal never lowers a score; only credits do). A type added after the fit keeps its hand weight.

## Evaluation and acceptance bar

Split by time: the fit uses every example before the last 60 days, the test window is the last 60 days, and a user in the test window is removed from the fit. No random split. Both scores are computed by the calculator's rules (dedup, per-entity counting, caps in points, credits) over the same snapshots: the baseline with the hand weights, the candidate with the fitted points. Reported for both: precision, recall and false positive rate among honest examples at HIGH (score 60) and CRITICAL (100); average precision; for the fit, a reliability table by score band, calibration slope and intercept, and the Brier score; the false positive rate at HIGH per role (farmer, trucker, buyer) and per Fayda flag; p and the pooling used. Intervals from 1,000 paired bootstrap resamples by user. The report (aggregates only, no rows) goes to `specs/reports/`.

The fitted weights go from offline to shadow only when all hold on the test window:

1. Average precision above the hand weights', with the 95 percent interval of the gain above 0.
2. At HIGH, no worse within a margin of 0.05: the 95 percent interval of the recall difference (fitted minus hand) lies above minus 0.05, and that of the false positive rate difference among honest examples lies below plus 0.05.
3. At CRITICAL: precision at least 0.90 and not lower than the hand weights', judged when at least 10 test examples reach CRITICAL under either weighting (else reported). A false CRITICAL holds an honest party's money. The ML score never makes CRITICAL on its own (below), so this bar guards phase 4, where fitted weights become hand weights.
4. Calibration slope from 0.8 to 1.2, and the mean predicted probability within 0.05 of the observed fraud share.
5. For each role and each Fayda flag with at least 20 honest examples, the false positive rate at HIGH at most 0.05 above the hand weights'. Smaller groups are reported, not judged.

From shadow to priority, after at least 4 weeks of shadow in prod:

6. The share of scored users the ML score puts at HIGH or above is at most 1.5 times the rules' share, and every disagreement in a weekly sample of 20 can be explained from the points per type.

## Deployment

None of the settings below exists today; P2 builds them.

| Phase | Configuration | Effect | To move on |
|-------|---------------|--------|------------|
| 0, deferred (now) | none | approach 1, quarterly | the trigger |
| 1, offline | none | the fit and the report | bars 1 to 5 |
| 2, shadow | `RISK_ML_MODE=shadow`, `RISK_ML_WEIGHTS`, `RISK_ML_WEIGHTS_VERSION` | `RiskScoreCalculator` sums a second score with the fitted points over the rows it already read; when its tier differs from the rules', `GetUserRiskScoreAsync` logs `RISK_SCORE_SHADOW userId=<id> tier=<rules> mlTier=<ml> score=<n> mlScore=<n> version=<v>` (ids through `MoneyAlarmLog.LogId`). A read that stopped early (section 14.3) logs no comparison. No other effect | bar 6 |
| 3, priority | `RISK_ML_MODE=priority` | admin only: Fraud Review orders users by the higher of the two tiers and shows the ML points apart from the rules' (as FRD-19 shows the AI advice). Nothing else reads the ML score | the owner's decision |
| 4, replace (optional) | a code change | the owner adopts fitted weights as new hand weights in section 15 and `SignalWeight` through the usual PR; from then on they are rules | none |

**Rules stay authoritative in every phase.** The tier that adds a step-up, refuses with `FRAUD_ACCOUNT_HELD`, holds or delays a payout (section 17.5) is the section 14 tier with the hand weights (the AI advisory inside its cap, as today). The ML score never lowers a tier and never refuses, holds, releases, refunds or delays money; a hold still needs the rules' CRITICAL. Not part of this decision, each needing its own record: letting the ML tier add a step-up, and letting it choose users for the FRD-19 AI reviewer (whose advisory weight counts in the full score, so that would add step-ups through the AI weight; FRD-19 selects on the rules alone).

Configuration (P2): set per environment in `infra/lib/config/environments.ts` (as FRD-19's `aiReviewEnabled` is), on every Lambda that builds a `FraudDetectionService` (the FRD-16 infra test already derives that list). `RISK_ML_WEIGHTS` is `TYPE:points` pairs, parsed without reflection; every value must be an integer from 0 to 50 and every type a known one, else the whole table is ignored, the mode reads as `off` and the service logs the alarm token `RISK_ML_WEIGHTS_INVALID` (FRD-16 pattern, with its own metric filter and alarm). Default `off` everywhere.

## Monitoring and drift

- Shadow and priority: a metric filter on `RISK_SCORE_SHADOW` with the direction (ML higher, ML lower) as its dimension; the owner reads the disagreement share weekly during shadow, monthly after.
- Quarterly, with the measurement script: the confirmed share per type. A type whose share has left its interval from the fit, or a new type with 30 or more (user, day) groups, calls for a re-fit.
- `FraudSignalsByType` (FRD-16): a type whose monthly volume doubles or halves is re-evaluated before the next re-fit.
- A re-fit is never automatic, at most quarterly, passes bars 1 to 5 again and runs 2 weeks in shadow (a new `RISK_ML_WEIGHTS_VERSION`) before it replaces the live table.

## Rollback

- **One switch** (built by P2): `riskMlMode: 'off'` in `infra/lib/config/environments.ts` (`RISK_ML_MODE=off`) and an infra deploy of the API stack, about 15 minutes. The next score read uses the hand weights only. Nothing stored needs undoing: the ML score writes no row and chooses no user for the AI reviewer, so no advisory weight comes from it; decisions an admin took in priority mode are ordinary decisions, with their usual effects and labels.
- **Who**: the owner (the one person with deploy rights), on a jump in the disagreement share, a failed quarterly check, an admin report that the ordering misleads, or `RISK_ML_WEIGHTS_INVALID`.
- **Why no faster path**: in no phase can the ML score refuse, hold or move money, so the worst case during the deploy is a misordered Fraud Review. An invalid table switches itself off.
- **Phase 4** rolls back like any rule change, by reverting its PR. Rows written meanwhile keep their stored weight for up to 30 days; an admin approve clears a row that matters sooner.

## Cost

- Phases 0 and 1: no AWS resource; the export reads a few units; the fit runs on the owner's machine in seconds.
- Phases 2 and 3: no new read, table, Lambda or model runtime; the second sum is arithmetic over rows already read; log lines only on a disagreement. Per environment and month: the shadow metric with two dimension values (at most $0.60), a metric for `RISK_ML_WEIGHTS_INVALID` (at most $0.30, billed only in hours with a line) and its alarm ($0.10), so at most $1.00.
- P1: one full-window Query of the user's signals (one or two pages), one users-row GetItem and one escrows Query per admin decision, a few read units; no new table, index or Lambda.
- Owner time: a quarterly trigger check (15 minutes) until the trigger; about a day per fit (export, fit, report, review).
- For comparison, approaches 3 and 4 add a model runtime and its upgrades; an always-on managed endpoint alone costs more a month than the whole fraud monitoring of section 23.1.

## Privacy

- The data are pseudonymous: Cognito subs, case and signal ids, codes, counts, account age, a Fayda flag, a role. The export never carries free text and stops on a value that looks like a phone number (section 20.1). The proxies for region and ethnicity above are left out.
- The export file stays outside any git checkout, on the owner's encrypted disk, and is deleted after the fit. Only aggregates and the weights table enter the repo; the weights table holds no personal data.
- The FRD-19 record names Ethiopia's Personal Data Protection Proclamation (No. 1321/2024) for its data transfer. Fitting a model on decisions about identifiable users is profiling, and the prod export leaves `me-south-1` for the owner's machine; both go into the same data protection review before phase 1. In every phase a person (the admin) takes each decision with an effect on a user; the ML score has none on its own.
- Users never see a tier or a score (section 17); unchanged.

## Prerequisites and follow-ups (not built here)

- **P1, feature snapshot on the fraud label** (server, small). In `AdminFunction.HandleFraudActionAsync`, right after the signal row is read and before the side effects (the reject's suspension, the whitelist write) and the review stamp, so the decided signal still counts as pending and a whitelist being granted silences nothing yet:
  - a full-window read of the user's signal partition (the section 14.3 key condition and page cap, without the score's early stop at CRITICAL, since the score read stops once the tier is certain and would undercount), turned by the calculator's rules into units per type, uncapped, plus the credits and a truncated flag;
  - the users row (`createdAt`, `faydaVerified`, `role`) and one Query of the role's escrow status index for its completed escrows (the read FRD-13 makes for a farmer or trucker; `BuyerStatusIndex` for a buyer).

  `OutcomeLabelWriter` stores them with `snapshotVersion` on the label (counts, codes and ages only), and the export adds the fields. A failed read leaves the label without a snapshot and never fails the decision. Every decision gets one; the fit uses the day's first.
- **P2, shadow and priority** (server, infra, admin with en, am, om; at the trigger): the second weights table in `RiskScoreSettings` and `RiskScoreCalculator`, the settings `RISK_ML_MODE`, `RISK_ML_WEIGHTS` and `RISK_ML_WEIGHTS_VERSION`, the shadow line and its metric, the invalid-weights metric and alarm, the Fraud Review ordering and display.
- **P3, the fit script** (`scripts/fit_risk_weights.py`, stdlib like the export, its tests offline) and the report template.
- Optional: a random audit sample of unflagged trades; the pre-FRD-18 label backfill (section 20.1).

## Open decisions (owner)

1. Build P1 now (recommended: it starts the clock) or later.
2. A random audit sample of unflagged trades (for example 10 a month) to estimate what the rules miss; it costs admin time.
3. Adding the profiling and the export to the data protection review FRD-19 asks for.
4. Whether `whitelisted` counts as honest (assumed here) or is left out.

## Review log

| Date | Fraud examples | Honest examples | Users (fraud, honest) | Largest user share (fraud, honest) | Span (days) | Last 60 days (fraud, honest) | Note |
|------|----------------|-----------------|-----------------------|------------------------------------|-------------|------------------------------|------|
| 2026-11-07 | 0 | 0 | 0, 0 | none | 0 | 0, 0 | No snapshot yet (P1 not built); FRD-18 labels merged the same day (#299) |
