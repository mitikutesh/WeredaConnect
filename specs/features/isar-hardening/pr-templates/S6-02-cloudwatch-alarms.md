# PR Scaffold — S6-02: CloudWatch alarms for sync surface

> **Status:** Design ready · **Depends on:** — (independent; pure CDK)
> **Sprint:** HS-6 · **Epic:** E3
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s6-02`
> **Reviewer needed:** infra + ops · **Story size:** 5 pts · **Risk:** Low

CDK construct wiring CloudWatch alarms for the sync + escrow Lambda
surface. Single SNS topic; subscribers: PO email + ops Slack webhook.

---

## 0. TL;DR

A new `ObservabilityStack` declares six alarms:

| Metric | Threshold | Reason |
|---|---|---|
| `SyncFunction` 5xx rate | > 1% over 5 min | Server bug or DynamoDB throttle |
| `SyncFunction` 409 rate | > 0.1% over 1 hr | Idempotency dedup hits — client retry storm |
| `EscrowFunction` 4xx with stale `idempotencyKey` | any | Replay attack or aggressive client retry |
| Lambda cold start p95 | > 500 ms | AOT regression |
| DynamoDB throttle on `SyncMetadata` or `EscrowIdempotency` | any | Capacity miss; queue dedup at risk |
| Synthetic mobile-flavor sync test | failure | API outage (added by S6-04) |

All alarms publish to one SNS topic. Topic has two subscribers: PO
email + ops Slack via the existing webhook integration.

---

## 1. How to ship this

```bash
git fetch origin
git checkout -b hs6/s6-02-cloudwatch-alarms origin/main

# Apply:
#   - infra/lib/stacks/observability-stack.ts          (new)
#   - infra/bin/wereda.ts                              (wire the stack)
#   - infra/test/observability-stack.test.ts           (CDK snapshot test)

cd infra && npx cdk synth --quiet && npm test
cd ..

git add ... && git commit -m "feat(infra): CloudWatch alarms for sync + escrow surface"
git push -u origin hs6/s6-02-cloudwatch-alarms
gh pr create --title "feat(infra): CloudWatch alarms for sync [S6-02]"
```

After merge, deploy to dev and validate the alarms fire on synthetic
failures before promoting to staging/prod.

---

## 2. PR description

### Summary

A new CDK stack `ObservabilityStack` that wires CloudWatch alarms,
SNS topic, and subscriber endpoints for the sync + escrow Lambda
surface.

### Why

`docs/mobile/isar-implementation.md` §15b lists six metrics worth
alarming on. Without these, regressions on the server-side surface
are invisible until users report them — too slow for a payments app.

### What changes

```typescript
// infra/lib/stacks/observability-stack.ts
import { Stack, StackProps, Duration } from 'aws-cdk-lib';
import { Construct } from 'constructs';
import { Alarm, ComparisonOperator, TreatMissingData } from 'aws-cdk-lib/aws-cloudwatch';
import { Topic } from 'aws-cdk-lib/aws-sns';
import { EmailSubscription, UrlSubscription } from 'aws-cdk-lib/aws-sns-subscriptions';
import { SnsAction } from 'aws-cdk-lib/aws-cloudwatch-actions';

export class ObservabilityStack extends Stack {
  constructor(scope: Construct, id: string, props: StackProps) {
    super(scope, id, props);

    const topic = new Topic(this, 'AlarmsTopic', {
      displayName: 'WeredaConnect alarms',
    });
    topic.addSubscription(new EmailSubscription(
      process.env.PO_EMAIL || 'ops@example.com'));
    if (process.env.SLACK_WEBHOOK_URL) {
      topic.addSubscription(new UrlSubscription(process.env.SLACK_WEBHOOK_URL));
    }

    const alarmAction = new SnsAction(topic);

    // 1. SyncFunction 5xx rate > 1% over 5 min
    new Alarm(this, 'SyncFunction5xxRate', {
      metric: /* SyncFunction Errors / Invocations as percent */,
      threshold: 1,
      evaluationPeriods: 1,
      comparisonOperator: ComparisonOperator.GREATER_THAN_THRESHOLD,
      treatMissingData: TreatMissingData.NOT_BREACHING,
    }).addAlarmAction(alarmAction);

    // 2. SyncFunction 409 rate > 0.1% over 1 hr
    // 3. EscrowFunction 4xx with stale idempotencyKey (any)
    // 4. Lambda cold-start p95 > 500ms
    // 5. DynamoDB throttle on SyncMetadata or EscrowIdempotency (any)
    // ... (see §5 for the full diff)
  }
}
```

### What does NOT change

- The Lambda code.
- The DynamoDB tables.
- Anything mobile.

### Backwards compatibility

Additive. New stack, new topic, new subscriptions.

### Test plan

```bash
cd infra
npx cdk synth --quiet                    # synth succeeds
npm test                                  # snapshot test
```

After dev deploy:
1. Force a 5xx in `SyncFunction` (e.g., return Status.InternalServerError from a test endpoint)
2. Run > 100 requests in 5 min so the rate crosses 1%
3. Alarm fires; SNS publishes; PO email received; Slack message in #ops

### Risk

Low. Alarms can be noisy on first deploy — start with relaxed
thresholds in dev, tighten in staging based on baseline.

### Acceptance criteria

- [ ] Alarm: `SyncFunction` 5xx rate > 1% over 5 min
- [ ] Alarm: `SyncFunction` 409 rate > 0.1% over 1 hr (idempotency dedup storm)
- [ ] Alarm: `EscrowFunction` 4xx with stale `idempotencyKey` — any occurrence
- [ ] Alarm: Lambda cold-start p95 > 500ms
- [ ] Alarm: DynamoDB throttle on `SyncMetadata` or `EscrowIdempotency` — any
- [ ] All wired to a single SNS topic; subscribers: PO email + ops Slack webhook
- [ ] `infra/lib/stacks/` synth clean

---

## 3. Reviewer guide

1. **Per-region alarms.** Production is in `me-south-1`, dev/staging
   in `eu-north-1`. Both regions need the stack deployed.
2. **`treatMissingData`.** Set to `NOT_BREACHING` for all alarms so
   absence of data (e.g., during a 5-minute quiet period) doesn't
   trigger false alarms.
3. **Slack webhook URL.** Lives in env or Secrets Manager — not
   hardcoded.

---

## 4. Rollback plan

`cdk destroy WeredaConnectObservability-<env>`. Alarms go away;
SNS topic deleted. Server keeps running.

---

## 5. Proposed diffs

Full stack code in §2 sketch. The metric definitions for items 2-6
follow the same pattern; specifics:

- **409 rate:** custom metric — Lambda must emit a `409Count` metric
  via `CloudWatch.PutMetricData`. Coordinate with server team.
- **Cold-start p95:** the standard Lambda `Duration` metric filtered
  by Init duration. Use `Math.percentile(m, 95)`.
- **DynamoDB throttle:** `Aws/DynamoDB ThrottledRequests` on table
  names `SyncMetadata`, `EscrowIdempotency`.

---

## 6. Open questions

1. **PagerDuty integration.** Slack webhook gets us notification;
   PagerDuty gets on-call rotation. Out of scope for this PR; track
   as a follow-up infrastructure story.

2. **Composite alarms.** Should "5xx rate AND DynamoDB throttle"
   trigger a higher-severity alert than either alone? Default: no —
   keep alarms simple.

3. **Cost.** CloudWatch alarms are $0.10/alarm/month. Six alarms =
   $0.60/month. Well under cost guardrails.

---

*PR scaffold v1.0 — 2026-05-13*
