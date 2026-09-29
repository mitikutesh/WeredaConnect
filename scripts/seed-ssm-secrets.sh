#!/bin/bash
# scripts/seed-ssm-secrets.sh
# Seed SSM Parameter Store SecureString secrets for a NON-PROD environment.
#
# dev/staging read their secrets from SSM Parameter Store (free standard tier)
# instead of Secrets Manager (prod-only — ~$0.40/secret/mo). CloudFormation
# cannot CREATE SecureString parameters, so this script creates them
# out-of-band. CI (deploy-server.yml) runs it before every non-prod
# `cdk deploy`; it is idempotent (existing parameters are never overwritten).
#
# For each secret path:
#   1. Skip if the SSM parameter already exists.
#   2. Else copy the value from the legacy Secrets Manager secret of the same
#      name if it exists (migration path). The copy is read back and verified.
#   3. Else — ONLY on a definitive ResourceNotFoundException — generate a
#      fresh value (HMAC keys) or a placeholder.
#
# FAIL-CLOSED CONTRACT: any error other than "does not exist" (AccessDenied,
# throttling, expired credentials, wrong region, …) aborts with a non-zero
# exit. This is load-bearing: once a secret leaves the CloudFormation
# template, CloudFormation force-deletes it WITHOUT a recovery window, so a
# silently-generated replacement would permanently destroy the real value.
# AccessDenied is retried briefly first — the workflow attaches this script's
# IAM grants moments earlier, and IAM is eventually consistent.
#
# Parameters intentionally use the default aws/ssm KMS key (never --key-id):
# Lambda readers are granted ssm:GetParameter only, which is sufficient for
# the AWS-managed key but NOT for a customer-managed one.
#
# Usage: ./scripts/seed-ssm-secrets.sh [dev|staging]

set -euo pipefail

ENV="${1:-}"

if [[ "$ENV" != "dev" && "$ENV" != "staging" ]]; then
  echo "Error: Invalid environment '$ENV'. Use: dev or staging."
  echo "prod keeps its secrets in Secrets Manager — nothing to seed."
  exit 1
fi

# Same env → region mapping as deploy-server.yml; explicit AWS_REGION wins.
if [[ -z "${AWS_REGION:-}${AWS_DEFAULT_REGION:-}" ]]; then
  if [[ "$ENV" == "dev" ]]; then
    export AWS_REGION="eu-north-1"
  else
    export AWS_REGION="me-south-1"
  fi
fi

PREFIX="/wereda-connect-${ENV}"
RETRIES="${SEED_RETRIES:-6}"
RETRY_DELAY="${SEED_RETRY_DELAY:-5}"

# Placeholder template matching api-stack.ts (TelebirrCredentials), minus the
# random filler key generateSecretString appends. Every key the server
# requires is non-empty, so TelebirrConfig.FromJson parses cleanly and the
# escrow/webhook Lambdas cold-start; signing fails cleanly until real
# credentials are set via `aws ssm put-parameter --overwrite`
# (see .ai/telebirr-testing.md).
TELEBIRR_PLACEHOLDER_JSON='{"fabricAppId":"PLACEHOLDER","appSecret":"PLACEHOLDER","merchantAppId":"PLACEHOLDER","merchantCode":"PLACEHOLDER","shortCode":"PLACEHOLDER","baseUrl":"https://developerportal.ethiotelebirr.et:38443/apiaccess/payment/gateway","webUrl":"https://developerportal.ethiotelebirr.et:38443/payment/web/paygate","merchantPrivateKeyPem":"PLACEHOLDER","telebirrPublicKeyPem":"PLACEHOLDER","returnUrl":"weredaconnect://payment/complete","signPadding":"pkcs1","httpProxy":""}'

is_transient() {
  grep -Eq "AccessDenied|Throttling|Rate exceeded|ExpiredToken" "$1"
}

# 0 = exists, 1 = definitively missing (ParameterNotFound); aborts otherwise.
parameter_exists() {
  local path="$1" attempt err_file
  err_file=$(mktemp)
  for attempt in $(seq 1 "$RETRIES"); do
    if aws ssm get-parameter --name "$path" >/dev/null 2>"$err_file"; then
      rm -f "$err_file"
      return 0
    fi
    if grep -q "ParameterNotFound" "$err_file"; then
      rm -f "$err_file"
      return 1
    fi
    if is_transient "$err_file" && ((attempt < RETRIES)); then
      echo "  ..  $path: transient error checking parameter (attempt $attempt/$RETRIES), retrying in ${RETRY_DELAY}s" >&2
      sleep "$RETRY_DELAY"
      continue
    fi
    echo "FATAL: could not check SSM parameter $path (not a ParameterNotFound):" >&2
    cat "$err_file" >&2
    rm -f "$err_file"
    exit 1
  done
}

# Copies the legacy secret's value into LEGACY_VALUE.
# 0 = copied, 1 = definitively absent (ResourceNotFoundException); aborts on
# ANY other error — never fall through to a generated replacement.
LEGACY_VALUE=""
fetch_legacy_secret() {
  local path="$1" attempt err_file value
  err_file=$(mktemp)
  for attempt in $(seq 1 "$RETRIES"); do
    if value=$(aws secretsmanager get-secret-value --secret-id "$path" \
        --query SecretString --output text 2>"$err_file") \
        && [[ -n "$value" && "$value" != "None" ]]; then
      LEGACY_VALUE="$value"
      rm -f "$err_file"
      return 0
    fi
    if grep -q "ResourceNotFoundException" "$err_file"; then
      rm -f "$err_file"
      return 1
    fi
    if is_transient "$err_file" && ((attempt < RETRIES)); then
      echo "  ..  $path: transient error reading legacy secret (attempt $attempt/$RETRIES), retrying in ${RETRY_DELAY}s" >&2
      sleep "$RETRY_DELAY"
      continue
    fi
    echo "FATAL: could not read legacy Secrets Manager secret $path — refusing to generate a replacement (the deploy would permanently delete the real value):" >&2
    cat "$err_file" >&2
    rm -f "$err_file"
    exit 1
  done
}

seed_parameter() {
  local path="$1"
  local fallback="$2"

  if parameter_exists "$path"; then
    echo "  OK  $path (exists — skipped)"
    return 0
  fi

  local value source
  if fetch_legacy_secret "$path"; then
    # NOTE: --output text strips a trailing newline; all current secrets are
    # single-line hex/JSON values, so this is lossless for them.
    value="$LEGACY_VALUE"
    source="copied from Secrets Manager"
  else
    value="$fallback"
    source="generated"
  fi

  # file:// keeps the secret value out of the process table.
  local value_file
  value_file=$(umask 077 && mktemp)
  printf '%s' "$value" >"$value_file"
  aws ssm put-parameter \
    --name "$path" \
    --type SecureString \
    --value "file://$value_file" \
    --description "WeredaConnect ${ENV} secret (seeded by seed-ssm-secrets.sh)" \
    >/dev/null
  rm -f "$value_file"

  # Read back and verify — abort before any deploy can delete the legacy
  # secret. Parameter Store reads are not strongly consistent; retry briefly.
  local readback="" attempt
  for attempt in 1 2 3; do
    if readback=$(aws ssm get-parameter --name "$path" --with-decryption \
        --query Parameter.Value --output text 2>/dev/null); then
      break
    fi
    sleep 1
  done
  if [[ "$readback" != "$value" ]]; then
    echo "FATAL: read-back verification failed for $path — aborting." >&2
    exit 1
  fi

  echo "  OK  $path ($source)"
}

echo "=== Seeding SSM SecureString secrets: $ENV (${AWS_REGION:-${AWS_DEFAULT_REGION}}) ==="
seed_parameter "$PREFIX/pagination-secret" "$(openssl rand -hex 32)"
seed_parameter "$PREFIX/fayda/signing-secret" "$(openssl rand -hex 32)"
# Fayda RP private key (private_key_jwt); replace with the key registered at partner.fayda.et.
seed_parameter "$PREFIX/fayda/client-private-key" "PLACEHOLDER"
seed_parameter "$PREFIX/telebirr/credentials" "$TELEBIRR_PLACEHOLDER_JSON"
echo "=== Seed complete: $ENV ==="
