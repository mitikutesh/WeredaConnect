#!/bin/bash
# Confirm a Cognito user (skip OTP) for dev testing.
# Also updates the DynamoDB user record to set IsPhoneVerified=true.
#
# Usage: ./scripts/confirm-user.sh +251911831670
#
# Required environment variables (set in your shell or .env):
#   COGNITO_USER_POOL_ID  — Cognito User Pool ID (from CDK output)
#   USERS_TABLE           — DynamoDB users table name (from CDK output)
#   AWS_REGION            — AWS region (default: me-south-1)

set -euo pipefail

REGION="${AWS_REGION:-me-south-1}"

if [ -z "${COGNITO_USER_POOL_ID:-}" ]; then
  echo "Error: COGNITO_USER_POOL_ID environment variable is not set."
  echo "Set it from your CDK stack output, e.g.:"
  echo "  export COGNITO_USER_POOL_ID=me-south-1_XXXXXXX"
  exit 1
fi

if [ -z "$1" ]; then
  echo "Usage: $0 <phone_number>"
  echo "Example: $0 +251911831670"
  exit 1
fi

PHONE="$1"

# 1. Confirm the user in Cognito (skip OTP)
echo "Confirming user $PHONE in Cognito..."
aws cognito-idp admin-confirm-sign-up \
  --user-pool-id "$COGNITO_USER_POOL_ID" \
  --username "$PHONE" \
  --region "$REGION"

echo "Cognito user confirmed."

# 2. Update DynamoDB IsPhoneVerified flag (if USERS_TABLE is set)
if [ -n "${USERS_TABLE:-}" ]; then
  echo "Looking up user in DynamoDB ($USERS_TABLE)..."

  # Get the Cognito sub (userId) for this phone number
  USER_SUB=$(aws cognito-idp admin-get-user \
    --user-pool-id "$COGNITO_USER_POOL_ID" \
    --username "$PHONE" \
    --region "$REGION" \
    --query "UserAttributes[?Name=='sub'].Value | [0]" \
    --output text 2>/dev/null) || true

  if [ -n "$USER_SUB" ] && [ "$USER_SUB" != "None" ]; then
    aws dynamodb update-item \
      --table-name "$USERS_TABLE" \
      --key "{\"pk\": {\"S\": \"USER#$USER_SUB\"}, \"sk\": {\"S\": \"PROFILE\"}}" \
      --update-expression "SET isPhoneVerified = :v" \
      --expression-attribute-values '{":v": {"BOOL": true}}' \
      --region "$REGION" 2>/dev/null && \
      echo "DynamoDB user record updated (IsPhoneVerified=true)." || \
      echo "Warning: Could not update DynamoDB record. User may need a different key schema."
  else
    echo "Warning: Could not find Cognito sub for $PHONE. DynamoDB not updated."
  fi
else
  echo "Tip: Set USERS_TABLE env var to also update the DynamoDB user record."
fi

echo "Done. User $PHONE can now log in."
