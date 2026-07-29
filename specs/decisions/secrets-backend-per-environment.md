# ADR: Per-environment secrets backend — SSM Parameter Store (dev/staging), Secrets Manager (prod)

**Date**: 2026-07-28
**Status**: Accepted

## Decision

- **dev + staging**: secrets are SSM Parameter Store **SecureString** parameters
  (standard tier — free). CloudFormation cannot create SecureStrings, so
  `scripts/seed-ssm-secrets.sh` creates them out-of-band; CDK only *references*
  them (`ssm.StringParameter.fromSecureStringParameterAttributes`) and grants
  `ssm:GetParameter`.
- **prod**: secrets stay in **AWS Secrets Manager**, template unchanged
  (identical construct IDs/props — the migration produces a zero-diff prod
  deploy).
- **Server** stays environment-blind: the same `*_ARN` env vars carry either a
  Secrets Manager ARN (prod) or an SSM parameter name (non-prod).
  `AwsParameterResolver.GetSecretOrParameterValue` routes on the value's shape
  (contains `:secretsmanager:` → Secrets Manager; other `arn:` → error; else →
  SSM with decryption) and **caches per Lambda container** — one AWS call per
  secret per container, which is the "caching enabled" requirement for prod.

Secret paths are identical across backends: `/wereda-connect-{env}/pagination-secret`,
`/wereda-connect-{env}/fayda/client-secret`, `/wereda-connect-{env}/fayda/signing-secret`,
`/wereda-connect-{env}/telebirr/credentials` (Telebirr only when `enableTelebirrLive`).

## Why

Secrets Manager costs ~$0.40–0.44/secret/month + API calls; the non-prod
secrets are pure waste (~$2.40–3.20/month today, growing with each new secret,
e.g. the suggested FCM service account). SSM standard tier is free, already
used by this stack for 5 config parameters with identical wiring, and the
server already shipped `GetSsmParameterValue`.

## Alternatives rejected

1. **Consolidate secrets into one SM secret per env** — still costs money; more
   server churn; doesn't remove the cost class.
2. **CDK-created `String` (non-secure) parameters** — values land in the
   template, and every deploy would revert out-of-band updates (real
   Fayda/Telebirr credentials) to PLACEHOLDER.
3. **`ENVIRONMENT` env var + explicit backend flag** — more moving parts than
   value sniffing; the server has no environment concept and doesn't need one.
4. **`AWSSDK.SecretsManager.Caching` package** — unverified under Native AOT;
   the hand-rolled container-lifetime memo is reflection-free by construction.
5. **TTL cache with stale-serving** — dead machinery: every reader resolves
   once per container (constructor or memo); nothing rotates today.
6. **SSM interface endpoint for the Telebirr VPC lambdas** — not needed (they
   egress via the NAT instance, same as they reached Secrets Manager) and would
   cost ~3× the savings. Do not add one.

## Accepted trade-offs

- Non-prod loses SM-native rotation and resource policies. Nothing rotates
  today; a manually rotated SSM value reaches only **new** Lambda containers
  (readers memoize for the container's life). To force a refresh, redeploy or
  wait for container recycling.
- The `*_ARN` env-var suffix is inaccurate in non-prod (holds a parameter
  name). Kept to avoid churn across server + infra + tests; commented at both
  wiring points.
- `docs/security/security-audit-2026-02-22.md` recommends Secrets Manager for
  the pagination secret. **Consciously overridden for dev/staging only** —
  SecureString under the `aws/ssm` key is encryption-at-rest with IAM-scoped
  reads; prod is unchanged.
- SSM standard tier caps values at 4 KB → the Telebirr credentials JSON must
  use an **RSA-2048** merchant key (4096-bit exceeds the limit). Escape hatch:
  `--tier Advanced` ($0.05/param/month) for that one parameter.
- The non-prod parameters live **outside CloudFormation**: `cdk destroy` leaves
  them behind (delete manually with `aws ssm delete-parameter` when retiring an
  environment).
- The workflow's exclusive GithubActions-stack pre-deploy runs on every
  non-prod push (a no-op changeset after the first run) and consumes Web-stack
  exports — if a future change renames an export the GithubActions stack
  imports, deploy the Web stack first (or run `cdk deploy --all` once from
  `scripts/deploy.sh`) before the workflow's exclusive step can succeed.

## How the pieces fit (deploy ordering is load-bearing)

When a `AWS::SecretsManager::Secret` leaves the template, CloudFormation
deletes it with **ForceDeleteWithoutRecovery — there is no 30-day recovery
window**. The verified SSM copy therefore must exist *before* the switch
deploy. `deploy-server.yml` (non-prod) enforces this:

1. `cdk deploy WeredaConnect-<env>-GithubActions --exclusively` — lands the
   deploy role's seeding grants (`ssm:Get/PutParameter` +
   `secretsmanager:GetSecretValue`, scoped to `/wereda-connect-<env>/*`,
   non-prod roles only). Under `--all` this stack deploys *last*, after the
   seed step would have needed it.
2. `scripts/seed-ssm-secrets.sh <env>` — for each path: skip if the parameter
   exists; else copy from the legacy SM secret (read-back verified — any
   failure aborts the job before the deploy can delete anything); else generate
   (`openssl rand -hex 32` for HMAC keys, placeholder JSON for Telebirr).
3. `cdk deploy --all` — dev/staging templates now reference SSM; CloudFormation
   deletes the legacy non-prod SM secrets (their values already live in SSM).

`scripts/deploy.sh` runs the same seed step for non-prod. Expect one-off
`AccessDenied` Lambda init errors *during* the switch deploy (env-var flip can
land before the role policy update in the same CFN update) — self-heals.

## Runbook

### Seed / migrate (normally automatic via CI)

```bash
# CloudShell or any shell with admin-ish creds; idempotent
./scripts/seed-ssm-secrets.sh dev       # eu-north-1
./scripts/seed-ssm-secrets.sh staging   # me-south-1
```

### Read / update a non-prod secret

```bash
aws ssm get-parameter --name /wereda-connect-dev/pagination-secret \
  --with-decryption --query Parameter.Value --output text --region eu-north-1

aws ssm put-parameter --name /wereda-connect-dev/fayda/client-secret \
  --type SecureString --overwrite --value '<real-value>' --region eu-north-1
```

Never pass `--key-id`: Lambda readers are granted `ssm:GetParameter` only,
which suffices for the default `aws/ssm` key but not a customer-managed one.
New values reach new Lambda containers only (see trade-offs).

### Rotate (non-prod)

```bash
aws ssm put-parameter --name <path> --type SecureString --overwrite \
  --value "$(openssl rand -hex 32)"
# then force new containers: redeploy, or update any env var on the functions
```

### Rollback (back to Secrets Manager in non-prod)

Revert the migration commit and redeploy. CloudFormation recreates the SM
secrets with **fresh generated values** (the old ones were force-deleted).
Restore real values from the surviving SSM parameters:

```bash
aws secretsmanager put-secret-value \
  --secret-id /wereda-connect-<env>/<path> \
  --secret-string "$(aws ssm get-parameter --name /wereda-connect-<env>/<path> \
      --with-decryption --query Parameter.Value --output text)"
```

### Brand-new environment

`deploy-server.yml`'s exclusive GithubActions deploy fails if the Web stack has
never been deployed (missing exports). Bootstrap a fresh env with
`./scripts/deploy.sh <env>` (seeds first, then deploys everything), or seed
from CloudShell and run the workflow.

## Cost notes

- Do **not** enable SSM higher-throughput mode (makes every `GetParameter`
  billable); cold-start volume is far below the free 40 TPS.
- Savings: ~$2.40–3.20/month + API-call fees; more as secrets are added.
