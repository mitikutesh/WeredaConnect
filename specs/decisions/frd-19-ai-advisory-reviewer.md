# ADR: AI advisory reviewer on Amazon Bedrock (FRD-19)

**Date**: 2026-11-07
**Status**: Accepted and built, OFF in every environment. Turning it on, the model and the production Region route are the owner's decisions (section "Open decisions").
**Ticket**: FRD-19 (EPIC FRD, story S6, P3)
**Spec**: `specs/architecture/fraud-detection-architecture.md` section 21 (3.20.0)

## Question

Section 21 of the fraud spec reserves a place for AI advice: one `AI_ADVISORY` signal per review, weight capped by `RISK_ADVISORY_CAP` (10 in a 30-day window), counted in the full score (MEDIUM, HIGH) and left out of the enforcement score, so AI can add a step-up and can never make a user CRITICAL. The scorer has handled such rows since FRD-02. What was missing: which model, where it runs (dev is `eu-north-1`, staging and prod are `me-south-1`), what it is shown, how it is triggered without a Scan, what it costs, and how to stop it.

## Model availability (checked 2026-11-07)

Claude models on Bedrock are reached two ways: the `bedrock-runtime` endpoint (InvokeModel, Converse, and the Messages API) and the newer `bedrock-mantle` endpoint (Messages API only). Current models (Claude Opus 5.5, Sonnet 5.5, Haiku 5.5; Opus 4.7 and later) are served through cross-Region inference profiles; in-Region use of the bare model id is limited to a few Regions (Opus 5.5: `eu-west-2` only).

| Region | `bedrock-runtime` | `bedrock-mantle` | Opus 5.5, Sonnet 5.5, Haiku 5.5 on `bedrock-runtime` |
|--------|-------------------|------------------|-------------------------------------------------------|
| `eu-north-1` (dev) | yes | yes | EU geo profile and Global profile; not in-Region |
| `me-south-1` (staging, prod) | yes | no | Global profile only |
| `me-central-1` (UAE) | yes | no | Global profile only |
| `eu-central-1` (Frankfurt) | yes | yes | EU geo profile and Global profile |

Profile ids (Opus 5.5 model card): `eu.anthropic.claude-opus-5-5`, `global.anthropic.claude-opus-5-5`, bare id `anthropic.claude-opus-5-5`; Converse and Invoke are both supported on `bedrock-runtime`. The EU profile called from `eu-north-1` routes to `eu-central-1`, `eu-north-1`, `eu-south-1`, `eu-south-2`, `eu-west-1` and `eu-west-3`. The Global profile routes to any commercial Region. Sonnet 5.5 and Haiku 5.5 have the same Region pattern (geo plus global from `eu-north-1`, global only from `me-south-1`); their profile ids follow the same form (`eu.anthropic.claude-haiku-5-5`), to be confirmed on each model card before switching. Anthropic lists Opus 5.5, Sonnet 5.5 and Haiku 5.5 with per-model access criteria ("See Access"), so model access must be checked in the Bedrock console of the account before the switch is turned on. Regional (geo) profiles cost 10 percent more than Global.

Sources (read 2026-11-07):

- AWS, Claude Opus 5.5 model card (endpoints, APIs, profile ids, Region table, EU destination Regions): https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-anthropic-claude-opus-5-5.html
- AWS, Regional availability by models (Haiku 5.5, Sonnet 5.5, Opus 5.5 in `eu-north-1`, `me-central-1`, `me-south-1`): https://docs.aws.amazon.com/bedrock/latest/userguide/models-region-compatibility.html
- AWS, Regional availability by endpoints (`bedrock-mantle` not in `me-south-1`): https://docs.aws.amazon.com/bedrock/latest/userguide/endpoints-region-availability.html
- AWS, Supported Regions and models for inference profiles (source and destination Regions, opt-in Regions and abuse detection storage): https://docs.aws.amazon.com/bedrock/latest/userguide/inference-profiles-support.html
- AWS, Prerequisites for running model inference (`bedrock:InvokeModel` covers InvokeModel and Converse): https://docs.aws.amazon.com/bedrock/latest/userguide/inference-prereq.html
- AWS, Data protection (model providers have no access to prompts and completions): https://docs.aws.amazon.com/bedrock/latest/userguide/data-protection.html
- Anthropic, Claude in Amazon Bedrock (model ids, access, Global versus Regional pricing): https://platform.claude.com/docs/en/build-with-claude/claude-in-amazon-bedrock
- Anthropic, Claude on Amazon Bedrock, Opus 4.6 and earlier (current models reachable through InvokeModel on `bedrock-runtime`): https://platform.claude.com/docs/en/build-with-claude/claude-on-amazon-bedrock-legacy

## Options considered

**Model.** Claude Opus 5.5 (most capable, $4 and $20 per million input and output tokens at Anthropic's list price), Sonnet 5.5 ($2 and $10), Haiku 5.5 ($0.10 and $0.50), or an older ARN-versioned model such as Haiku 4.5 (still on Converse, but older and on the retirement path). The task is a short classification with a few sentences of explanation over at most about 9,000 input tokens.

**Dev route.** The EU geo profile (data stays in six EU Regions, 10 percent premium) or the Global profile (no premium, any commercial Region).

**Production route** (`me-south-1` has no geo profile):

1. Off. Nothing leaves Bahrain.
2. The Global profile from `me-south-1`: the redacted timeline may be processed in any commercial Region.
3. The EU geo profile called from an EU Region: the reviewer Lambda stays in `me-south-1` and sets its Bedrock client to `eu-central-1` (`BEDROCK_REGION`), so the timeline goes to the EU Regions only.
4. Claude Platform on AWS or the Claude API directly: a second billing and key path to manage, outside the AWS IAM model the rest of the stack uses.

**Reviewer runtime** (the spec requires a non-AOT Lambda):

1. Node.js 24 with the AWS SDK v3 that ships in the Lambda runtime: no dependency to bundle, no build step, the repo already deploys a Node Lambda this way (`tools/fayda-mock`), and `node --test` covers it.
2. Python 3.13 with boto3: equally dependency-free, but a second scripting stack for a Lambda.
3. .NET (JIT) Lambda referencing the server project: shares the scorer, but needs a second publish step and artifact in `deploy-server.yml`, next to the AOT bootstrap and its glibc guard.
4. Inside the AOT bootstrap: ruled out by the spec, and it would put an LLM client into the binary every money path runs.

**Trigger** (no Scan):

1. An hourly EventBridge rule over the fraud-signals `StatusCreatedAtIndex` (FRD-07): one key-bounded Query of `pending` rows created in the last 2 hours gives every user whose tier may have just risen, since a tier only rises when a signal row is written.
2. A DynamoDB stream on fraud-signals: a new stream on a live table, and the tier is not in the row, so every insert would still need a score read.
3. An event from `RecordFraudSignalAsync`: a new dependency in every Lambda that writes signals (sync, escrow, refresh, the webhook, order-cron), for a P3 feature.

**Split of work.** Either the non-AOT reviewer reads the timeline, calls Bedrock and writes the row, or an AOT cron reads, redacts and writes while the reviewer only calls Bedrock.

## Decision

- **Split**: a new AOT Lambda, `wereda-connect-<env>-fraud-advisory` (`FraudAdvisoryCronFunction`), selects the users, scores them with the existing `FraudDetectionService` (the same code the gates use), builds the redacted timeline, invokes the reviewer synchronously, checks the answer and writes the row. The reviewer, `wereda-connect-<env>-fraud-ai-reviewer` (`server/fraud-ai-reviewer`, Node.js 24, option 1), checks the timeline again and makes the one Bedrock call. The reviewer's role has `bedrock:InvokeModel` on the chosen model and nothing else: the code that talks to the model cannot read or write any table. The cron's role has no grant on orders, escrows or any money table: it cannot move money whatever the model says. The scorer and the signal row schema stay single-sourced in C#.
- **Trigger**: option 1, hourly, lookback 2 hours, at most 3 index pages and 50 candidates. A user is reviewed only when the rules alone put them at HIGH or CRITICAL (enforcement score at least `RISK_TIER_HIGH`, or CRITICAL), so an earlier advisory is never the reason for the next one; and at most once per UTC day.
- **Model**: Claude Opus 5.5 (`eu.anthropic.claude-opus-5-5` in dev), at effort `low`, with structured output (a JSON schema of `concern` and `explanation`) and the fixed system prompt marked for prompt caching. It is a configuration value (`aiReviewModelId` in `infra/lib/config/environments.ts`). Haiku 5.5 is about 40 times cheaper and is likely enough for this task; choosing it is a one-line change and the owner's call (see "Open decisions").
- **Dev route**: the EU geo profile from `eu-north-1`.
- **Production route**: off (option 1) in staging and prod. When the owner turns it on, the recommended route is option 3 (EU profile through `BEDROCK_REGION=eu-central-1`), after the data transfer is cleared under Ethiopia's Personal Data Protection Proclamation (No. 1321/2024). Option 2 is the fallback if EU routing is not wanted.
- **Weight**: the model answers a level, the cron maps it: `none` 0, `low` 3, `medium` 6, `high` 10, never above `RISK_ADVISORY_CAP`. The model cannot pick a number, and no answer lowers a score.

## Cost

Assumptions: per review about 3,000 input tokens (the system prompt with the signal catalogue, about 1,300, plus a typical timeline) and 800 output tokens (thinking at effort `low` plus the answer); at most about 9,000 input (50 signals, 100 events) and 4,096 output (`max_tokens`). Prices are Anthropic's list prices per million tokens, assumed equal to Bedrock's Global price; the EU geo profile adds 10 percent. Confirm on https://aws.amazon.com/bedrock/pricing/ before turning it on (Bedrock bills Claude through AWS Marketplace).

| Model | Typical review (EU profile) | Largest review (EU profile) |
|-------|-----------------------------|-----------------------------|
| Opus 5.5 | about $0.031 | about $0.13 |
| Sonnet 5.5 | about $0.015 | about $0.065 |
| Haiku 5.5 | about $0.0008 | about $0.0032 |

| Volume | Reviews a month | Opus 5.5 | Haiku 5.5 |
|--------|-----------------|----------|-----------|
| Dev (FRD-17 persona runs, manual checks) | about 30 | about $1 | under $0.05 |
| Prod, expected (1 to 5 users a day newly HIGH or above with new signals) | 30 to 150 | $1 to $5 | under $0.15 |
| Hard ceiling (`FRAUD_AI_REVIEW_MAX_PER_RUN` 5, 24 runs a day, one review per user a day; needs 120 distinct HIGH users with new signals every day) | 3,600 | $112 typical, $470 at the largest size | $3 to $12 |

Prompt caching saves a little within one run (the second to fifth review read the cached system prompt at a tenth of the price). AWS side, per environment and month: the cron runs 720 times (about a second each at 256 MB) and the reviewer at most as often as there are reviews; both fit in the Lambda free tier or cost cents. DynamoDB: per review about 5 to 12 reads and one write. CloudWatch: one alarm over two metrics ($0.20); the FRD-16 metric filters on the new log group are free. Lowering `FRAUD_AI_REVIEW_MAX_PER_RUN` lowers the ceiling proportionally.

## Privacy

Sent to the model (built by `FraudAdvisory.BuildTimeline`, checked again by the reviewer):

- the user's role, account age in days, whether Fayda verified, and the rules' tier, score and enforcement score;
- per signal row (at most 50): time, type, class (hard, soft), weight, review status and resolution, and a pseudonymous label of its entity (`order-1`);
- per transaction audit event of the signals' orders, deliveries and escrows (at most 20 per entity, 8 entities, 100 in all): time, entity label and kind, action, from and to state, the actor's role, and whether the actor is the reviewed user (`self`), another user (`other`), an admin or the system.

Never sent, and never read into the timeline: user ids, names, phone numbers, Fayda FIN or FAN, counterparty ids, record ids, signal descriptions, audit metadata, IP addresses, correlation ids, GPS points, amounts, crop names, woreda names, or any free text. The Queries project only the attributes above (the users row read is projected to `role`, `createdAt`, `faydaVerified`). Every value is cut to `[A-Za-z0-9_.:-]`, so no text can carry instructions to the model. The reviewer refuses (`invalid_input`, no call) any timeline holding a run of 9 or more digits, a second check against a phone number or an id slipping through. The model's explanation is cleaned before it is stored: control and bidirectional characters dropped, at most 600 characters, runs of 9 or more digits replaced by `[number]`.

Bedrock: model providers have no access to prompts and completions (AWS data protection page). Cross-Region inference may process the request in any destination Region of the profile, and prompts may be stored in opt-in destination Regions for abuse detection (AWS inference profiles page). Model invocation logging stays off in the account (it is off by default); turning it on would store the timelines in the logging destination. Logs: neither Lambda logs the timeline or the explanation; the reviewer logs the outcome, model and token counts, the cron the user id (as every fraud log line does), concern and weight.

## Failure modes

| Failure | Effect |
|---------|--------|
| Kill switch off (either Lambda) | Cron: logs `FRAUD_AI_REVIEW_OFF`, reads nothing. Reviewer: answers `disabled`, no Bedrock call |
| Candidate Query fails | The run answers 500 `FRAUD_AI_REVIEW_ERROR` (no retry: the next hourly run reads the lookback again) |
| A user's score cannot be read | Counted `unscored`, not reviewed (never treated as HIGH or LOW) |
| Bedrock throttles, denies access (model access not granted) or fails | The reviewer throws, the invocation returns a function error; the cron logs `FRAUD_AI_REVIEW_FAILED` with the reason and writes nothing; the error alarm fires |
| Model refuses, runs out of tokens, or answers outside the schema | `refused`, `truncated` or `unparsable`; logged, nothing written |
| Reviewer slower than 30 seconds | Lambda timeout, function error, as above |
| Two runs race for one user | The conditional put on the day key keeps one row |
| A run near its time limit | No review starts after two minutes of the three-minute Lambda, or with less than 50 seconds left |
| Wrong advice written | It adds at most 10 points in 30 days to the full score, never CRITICAL, never a hold; an admin approves the row (false positive) and its weight stops counting |

A failed review is retried by the next hourly run while the user's new signal is still within the 2-hour lookback (so about once); after that the user is reviewed again only after a new signal.

## Kill switch and caps

- `FRAUD_AI_REVIEW_ENABLED` on both Lambdas (CDK, `aiReviewEnabled` in `environments.ts`, default false in dev, staging and prod). Either one off stops every Bedrock call.
- Emergency stop without a deploy: set the reviewer's reserved concurrency to 0 in the console (every invocation is throttled, the cron logs the failures and writes nothing), or disable the EventBridge rule `wereda-connect-<env>-fraud-advisory-hourly`.
- `FRAUD_AI_REVIEW_MAX_PER_RUN` (default 5, at most 20) and one review per user and UTC day bound the spend; `FRAUD_AI_REVIEW_LOOKBACK_HOURS` (default 2, at most 24).

## Rollout

1. Owner picks the model; check model access for it in the Bedrock console of the account (dev: `eu-north-1`).
2. Set `aiReviewEnabled: true` for dev, deploy infra (the server deploy builds the cron into the same bootstrap). Run `scripts/e2e_fraud_personas.py` to produce HIGH users, wait for the next hourly run, and check Fraud Review (an "AI advice" panel on the user's group) and the logs (`FRAUD_AI_REVIEW_RUN`, `FRAUD_AI_REVIEW_WRITTEN`, the reviewer's `FRAUD_AI_REVIEWER` line). First run also proves the model id, the profile, the IAM resources and structured output on InvokeModel, none of which can be tested without AWS.
3. Staging and prod only after the data transfer decision; set `aiReviewBedrockRegion` (option 3) or a `global.` model id (option 2).

## Rollback

Turn `aiReviewEnabled` off and deploy (or the emergency stops above). Advisory rows already written stay until their 30-day TTL; an admin can approve them to clear their weight at once. Removing the feature entirely: delete the two Lambdas, the rule and the alarm from `api-stack.ts` and `monitoring-stack.ts`; the scorer keeps reading `AI_ADVISORY` rows as before FRD-19.

## Open decisions (owner)

1. The model: Opus 5.5 (configured, most capable) or Haiku 5.5 (about 40 times cheaper, likely enough for a short classification).
2. Turning it on in dev, and later in staging and prod, with the production route (EU profile from `eu-central-1`, Global from `me-south-1`, or off) after a data protection review.
3. Whether a `none` verdict should be written at all (today it is, with weight 0, so the admin sees the AI found an honest explanation).

## Accepted trade-offs

- The reviewer's tests run with `node --test` locally (`cd server/fraud-ai-reviewer && npm test`); CI runs the C# side only, as for `tools/fayda-mock`. The C# side validates every answer again, so the checks that guard the stored row run in CI.
- The day key leaves the scoring window up to a day before its TTL, as the hour key of other rows does by up to an hour.
- The model's answer is advice in English only; the admin app labels it as AI advice in all three languages.
