# fraud-ai-reviewer (FRD-19)

The AI advisory reviewer of the fraud engine (`specs/architecture/fraud-detection-architecture.md` section 21,
decision record `specs/decisions/frd-19-ai-advisory-reviewer.md`). A Node.js 24 Lambda,
`wereda-connect-<env>-fraud-ai-reviewer`, with no dependencies to bundle: the AWS SDK for JavaScript v3 ships with
the Lambda runtime.

It is the only code that talks to Amazon Bedrock. The advisory cron (`FraudAdvisoryCronFunction`, AOT .NET) invokes
it synchronously with a redacted timeline of one HIGH or CRITICAL user; it answers a verdict, and the cron writes the
`AI_ADVISORY` signal row. Its IAM role allows `bedrock:InvokeModel` and `bedrock:GetInferenceProfile` on the
configured model, plus the CloudWatch Logs writes every Lambda role has: no table, no queue, no other Lambda. The
cron's role is the one granted `lambda:InvokeFunction` on it; no resource policy restricts other principals of the
account that hold a broad invoke permission.

## What it does

1. Kill switch: answers `{ "ok": false, "error": "disabled" }` unless `AI_REVIEW_ENABLED` is `true`.
2. Rebuilds the request from an allow-list of fields, cutting every value to `[A-Za-z0-9_.:-]` (no free text reaches
   the model), and refuses (`invalid_input`, no call) a timeline holding a run of 9 or more digits.
3. Calls InvokeModel on `bedrock-runtime` with the Messages API body: the fixed system prompt (`src/prompt.js`,
   cached), the timeline as data in the user turn, effort `low`, and a JSON schema for the answer
   (`concern`: `none`, `low`, `medium` or `high`; `explanation`).
4. Answers `{ ok, concern, explanation, model, inputTokens, outputTokens }`, or `{ ok: false, error }` for a refusal
   (`refused`), a cut answer (`truncated`) or anything outside the schema (`unparsable`). A Bedrock error is thrown,
   so the invocation reports a function error and the `fraud-advisory-errors` alarm counts it.
5. Logs one JSON line per review (`FRAUD_AI_REVIEWER`: outcome, model, concern, token counts), never the timeline or
   the explanation.

## Configuration

| Variable | Meaning |
|----------|---------|
| `AI_REVIEW_ENABLED` | Kill switch, `true` to call Bedrock (CDK `aiReviewEnabled`, default off) |
| `BEDROCK_MODEL_ID` | Model or inference profile id, e.g. `eu.anthropic.claude-opus-5-5` (CDK `aiReviewModelId`) |
| `BEDROCK_REGION` | Region of the Bedrock endpoint; defaults to the Lambda's Region (CDK `aiReviewBedrockRegion`) |
| `AI_REVIEW_EFFORT` | `output_config.effort`, default `low`; set it empty for a model without effort (Haiku 4.5 and older) |

## Tests

```bash
cd server/fraud-ai-reviewer
npm run build   # node --check of every file
npm test        # node --test with a fake Bedrock; no AWS call
```

CI does not run these (like `tools/fayda-mock`); the cron validates every answer again in C#, under CI.
