# PR Scaffold — S6-04: Synthetic mobile-flavor sync test

> **Status:** Design ready · **Depends on:** S6-02 merged (SNS topic) · **Closes HS-6**
> **Sprint:** HS-6 · **Epic:** E3
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s6-04`
> **Reviewer needed:** infra · **Story size:** 2 pts · **Risk:** Low

EventBridge cron invokes a tiny "hello sync" Lambda every 5 minutes
from both regions (`eu-north-1` dev/staging, `me-south-1` prod). On
failure, publish to the SNS topic from S6-02.

---

## 0. TL;DR

A Lambda + EventBridge rule that pretends to be a mobile client.
POSTs an idempotent test request to `/sync/changes` with a synthetic
JWT. If the response is anything other than 200/204, fire an alarm.

---

## 1. How to ship this

```bash
git fetch origin
git log origin/main --oneline | grep S6-02

git checkout -b hs6/s6-04-synthetic-monitor origin/main

# Apply:
#   - infra/lib/stacks/synthetic-monitoring-stack.ts   (new)
#   - infra/lambda/synthetic-sync-monitor/             (new tiny Lambda)
#   - infra/bin/wereda.ts                              (wire the stack)

cd infra && npx cdk synth --quiet && npm test
cd ..

git add ... && git commit -m "feat(infra): synthetic sync monitor every 5 min"
git push -u origin hs6/s6-04-synthetic-monitor
gh pr create --title "feat(infra): synthetic sync monitor [S6-04]"
```

---

## 2. PR description

### Summary

EventBridge `rate(5 minutes)` rule → Lambda. Lambda POSTs a fixed
synthetic-user request to `/sync/changes`. Response non-200 → publish
to SNS topic from S6-02. Cost estimate: 8640 invocations/month × ~1ms
billed = $0.001/month + EventBridge $1/month. Well under guardrails.

### Why

CloudWatch alarms (S6-02) fire on real user-traffic metrics. But if
the system is down, real-user traffic *also* drops, which can mask
the outage in the metrics (the rate stays at 0 because nobody can
get through). A synthetic monitor maintains a known-good baseline.

### What changes

```typescript
// infra/lib/stacks/synthetic-monitoring-stack.ts
const monitor = new lambda.Function(this, 'SyntheticSyncMonitor', {
  runtime: lambda.Runtime.DOTNET_8,         // matches existing AOT pattern
  code: lambda.Code.fromAsset('infra/lambda/synthetic-sync-monitor'),
  handler: 'SyntheticMonitor::SyntheticMonitor.Function::Handler',
  memorySize: 256,
  timeout: Duration.seconds(10),
  environment: {
    API_URL: process.env.API_URL!,
    SYNTHETIC_JWT_SECRET: cdk.SecretValue.secretsManager('synthetic-jwt').unsafeUnwrap(),
  },
});

new events.Rule(this, 'SyntheticSyncSchedule', {
  schedule: events.Schedule.rate(Duration.minutes(5)),
  targets: [new targets.LambdaFunction(monitor)],
});

// Failure alarm on the monitor itself
new Alarm(this, 'SyntheticSyncFailure', {
  metric: monitor.metricErrors({ period: Duration.minutes(5) }),
  threshold: 1,
  evaluationPeriods: 1,
  comparisonOperator: ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
}).addAlarmAction(new SnsAction(alarmsTopic));   // SNS topic from S6-02
```

Lambda body (sketch):

```csharp
// infra/lambda/synthetic-sync-monitor/Function.cs
public static async Task<int> Handler(object _ , ILambdaContext ctx) {
  var jwt = MintSyntheticJwt();           // signed with SYNTHETIC_JWT_SECRET
  var client = new HttpClient();
  client.DefaultRequestHeaders.Authorization = new("Bearer", jwt);
  var response = await client.GetAsync($"{Env.API_URL}/sync/changes?since=now");
  if (response.StatusCode != HttpStatusCode.OK &&
      response.StatusCode != HttpStatusCode.NoContent) {
    throw new Exception($"synthetic sync failed: {response.StatusCode}");
  }
  return 0;
}
```

### What does NOT change

- The real sync endpoint.
- The SNS topic (already exists from S6-02).
- The user-facing app.

### Backwards compatibility

Additive — new Lambda, new schedule rule. Existing CloudWatch alarms
continue to fire on real traffic.

### Test plan

```bash
cd infra
npx cdk synth --quiet
npm test                                  # snapshot test for the new stack
```

Post-deploy validation:
1. Wait 5 min after deploy — check CloudWatch logs for the
   monitor; assert successful invocations
2. Disable the synthetic JWT secret (rotate to wrong value) →
   Lambda invocation throws → alarm fires within 5 min → SNS sends
   to PO email
3. Re-enable secret → monitor recovers within 5 min → alarm
   resolves

### Risk

Low. Lambda + EventBridge is well-trodden. Risk areas:

- **Synthetic JWT validity.** If the JWT signing convention changes
  server-side, the monitor breaks (which fires the alarm spuriously).
  Coordinate with server team; document the convention.

- **Cost runaway.** EventBridge cron is bounded — 8640 invocations/
  month, capped by the rate. Lambda cost is negligible.

### Acceptance criteria

- [ ] EventBridge rule: `rate(5 minutes)` → Lambda
- [ ] Lambda makes a real POST to `/sync/changes` with a synthetic test JWT
- [ ] Response code != 200 → publish to SNS topic from S6-02
- [ ] Estimated cost < $1/month

---

## 3. Reviewer guide

1. **JWT signing.** The synthetic JWT must be signed in a way the
   real `SyncFunction` authorizer accepts. Don't hardcode the
   secret in source.

2. **Region coverage.** Two stacks — one per region. Or one stack
   with cross-region invocation. Default: one per region for
   simplicity and isolation.

3. **Alarm dedupe.** A multi-hour outage will fire the alarm every 5
   min. Set the SNS topic's de-dup window to ~30 min via composite
   alarms.

---

## 4. Rollback plan

`cdk destroy` the synthetic monitoring stack. Real-traffic alarms
from S6-02 remain.

---

## 5. Proposed diffs

See §"What changes."

---

## 6. Open questions

1. **What does the synthetic user look like in metrics?** It hits
   the same handlers as real users. Filter by JWT subject claim if
   you want to exclude from analytics. Default: don't exclude —
   measured noise is acceptable.

2. **Should the synthetic also test the escrow path?** Out of
   scope. Track as follow-up; escrow has financial side-effects so
   the synthetic flow needs careful design.

---

*PR scaffold v1.0 — 2026-05-13 — closes HS-6*
