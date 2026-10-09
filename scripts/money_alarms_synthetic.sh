#!/usr/bin/env bash
#
# Money alarms synthetic test (ESC-13; the dispute verdict mismatch alarm since ESC-03).
#
# Proves that the CloudWatch metric filters and alarms behind the escrow money paths
# fire on the exact log lines the server emits, without moving any money. Pairs with
# server/src/Helpers/MoneyAlarmLog.cs and infra/lib/stacks/monitoring-stack.ts; the
# alarm table is in specs/contracts/escrow-protocol.md ("Additions in 1.8.1").
#
# Check mode (default; read-only apart from `aws logs test-metric-filter`):
#   - reads each deployed metric filter and tests it against a sample line, bare and
#     with the Lambda runtime's tab-separated prefix
#   - checks that the lines of a normal queued payout (ESC-02) and lines carrying hostile
#     client text, as the server writes them, match no money filter
#   - asserts the shape of the escrow-cron Errors alarm
#   - prints the current state of every money alarm
#
# --fire writes one synthetic line per log alarm into a throwaway log stream of the
# escrow, escrow-cron and sync log groups, invokes escrow-cron once with a payload the
# typed serializer cannot read (one Lambda error, the handler never runs), then waits
# until each alarm's history shows the transition to ALARM. It never calls
# set-alarm-state: every alarm clears by itself once the synthetic datapoint leaves its
# evaluation window. Subscribers of the <prefix>-alarms topic get one ALARM and, later,
# one OK notification per alarm. An alarm that is already in ALARM cannot transition: it
# is reported as not proven (investigate it, then run again once it is OK), and the run
# does not pass.
#
# Usage:
#   scripts/money_alarms_synthetic.sh [--env dev|staging|prod] [--region <aws-region>]
#                                     [--fire [--wait-clear]] [--allow-prod]
#
#   --env         environment (default dev)
#   --region      AWS region (default eu-north-1 for dev, me-south-1 otherwise)
#   --fire        write the synthetic lines, invoke escrow-cron, wait for ALARM
#   --wait-clear  after --fire, keep polling until every fired alarm is OK again
#   --allow-prod  required for --fire on prod
#
# Needs an AWS identity with: logs:DescribeMetricFilters, logs:TestMetricFilter and
# cloudwatch:DescribeAlarms (check); plus logs:CreateLogStream, logs:PutLogEvents,
# logs:DeleteLogStream, cloudwatch:DescribeAlarmHistory and lambda:InvokeFunction on
# <prefix>-escrow-cron (--fire).
#
# Exit codes: 0 every check (and, with --fire, every alarm) passed; 1 a check or an
# alarm failed, or an alarm was already in ALARM so its firing is not proven; 2 usage
# error.
#
set -uo pipefail

ENV_NAME="dev"
REGION=""
FIRE=0
WAIT_CLEAR=0
ALLOW_PROD=0

while [ $# -gt 0 ]; do
  case "$1" in
    --env) ENV_NAME="${2:-}"; shift 2 ;;
    --region) REGION="${2:-}"; shift 2 ;;
    --fire) FIRE=1; shift ;;
    --wait-clear) WAIT_CLEAR=1; shift ;;
    --allow-prod) ALLOW_PROD=1; shift ;;
    -h|--help) grep '^#' "$0" | sed -E 's/^# ?//'; exit 0 ;;
    *) echo "unknown arg: $1 (see --help)" >&2; exit 2 ;;
  esac
done

case "$ENV_NAME" in
  dev|staging|prod) ;;
  *) echo "--env must be dev, staging or prod" >&2; exit 2 ;;
esac
if [ -z "$REGION" ]; then
  if [ "$ENV_NAME" = "dev" ]; then REGION="eu-north-1"; else REGION="me-south-1"; fi
fi
if [ "$WAIT_CLEAR" = 1 ] && [ "$FIRE" = 0 ]; then
  echo "--wait-clear only makes sense with --fire" >&2; exit 2
fi
if [ "$FIRE" = 1 ] && [ "$ENV_NAME" = "prod" ] && [ "$ALLOW_PROD" = 0 ]; then
  echo "refusing --fire on prod without --allow-prod (it pages the alarm subscribers)" >&2; exit 2
fi
command -v aws >/dev/null 2>&1 || { echo "aws CLI not found" >&2; exit 2; }

PREFIX="wereda-connect-${ENV_NAME}"
LG_ESCROW="/aws/lambda/${PREFIX}-escrow"
LG_CRON="/aws/lambda/${PREFIX}-escrow-cron"
LG_SYNC="/aws/lambda/${PREFIX}-sync"

A_SETTLE="${PREFIX}-escrow-settlement-failures"
A_CRON="${PREFIX}-escrow-cron-errors"
A_ORDER="${PREFIX}-order-escrow-problems"
A_PAYOUT="${PREFIX}-manual-payouts-overdue"
A_QUEUE="${PREFIX}-admin-queue-overdue"
A_MISMATCH="${PREFIX}-dispute-verdict-order-mismatch"
ALL_ALARMS=("$A_SETTLE" "$A_CRON" "$A_ORDER" "$A_PAYOUT" "$A_QUEUE" "$A_MISMATCH")

# Sample lines: the exact formats of MoneyAlarmLog and the sync Lambda, with a synthetic id.
SID="synthetic-esc13"
L_SETTLE="ESCROW_SETTLEMENT_FAILED escrowId=${SID} action=release_failed outcome=failed code=SYNTHETIC"
L_LINK="ORDER_ESCROW_LINK_FAILED orderId=${SID} deliveryId=${SID} reason=[Synthetic]"
L_PAYOUT="MANUAL_PAYOUT_OVERDUE escrowId=${SID} ref=release:farmer ageHours=25"
L_QUEUE="ADMIN_QUEUE_OVERDUE orderId=${SID} reason=synthetic ageHours=25"
L_MISMATCH="DISPUTE_VERDICT_ORDER_MISMATCH escrowId=${SID} op=refund legs=order:mismatch:accepted"

# How the Lambda runtime writes a line to CloudWatch: time, request id and level, tab separated.
RT_PREFIX=$'2026-10-09T10:00:00.000Z\tsynthetic-esc13-request\twarn\t'

# No money filter may count these: the lines of a normal queued payout (ESC-02, 202
# ESCROW_034), and lines that carry client text exactly as the server writes them, with a
# hostile value that spells a token (the server turns "_" into "-" in client values, and the
# escrow Lambda logs the route template, not the path).
HOSTILE="ESCROW-SETTLEMENT-FAILED-ORDER-ESCROW-LINK-FAILED-MANUAL-PAYOUT-OVERDUE-DISPUTE-VERDICT-ORDER-MISMATCH"
NEGATIVE_LINES=(
  "ESCROW_MANUAL_PAYOUT_QUEUED escrowId=${SID} op=release legs=farmer,trucker reason=PAYOUT_NOT_SUPPORTED"
  "ESCROW_MANUAL_PAYOUT_WAITING escrowId=${SID} sweep=auto-release"
  "ESCROW_MANUAL_PAYOUT_PAID escrowId=${SID} op=release leg=farmer remaining=1"
  "EscrowFunction invoked: GET /escrow/{id}"
  "Error getting escrow ${HOSTILE}: [AmazonDynamoDBException]"
  "{\"category\":\"ESCROW\",\"action\":\"DISPUTE\",\"correlationId\":\"${HOSTILE}\",\"entityId\":\"${HOSTILE}\",\"outcome\":\"failure\"}"
  "Op ${HOSTILE}: status=error, msg=Invalid action: '${HOSTILE}'. Must be create, update, or delete."
  "ESCROW_MANUAL_PAYOUT_WAITING escrowId=escrow-order-${HOSTILE} sweep=auto-release"
  "DISPUTE_VERDICT_PROPAGATION_FAILED escrowId=${SID} orderId=${HOSTILE} deliveryId=- legs=order:conflict"
)

# log group | metric name | sample line
CHECKS=(
  "${LG_ESCROW}|EscrowSettlementFailures|${L_SETTLE}"
  "${LG_CRON}|EscrowSettlementFailures|${L_SETTLE}"
  "${LG_SYNC}|OrderEscrowProblems|${L_LINK}"
  "${LG_CRON}|ManualPayoutsOverdue|${L_PAYOUT}"
  "${LG_CRON}|AdminQueueOverdue|${L_QUEUE}"
  "${LG_ESCROW}|DisputeVerdictOrderMismatch|${L_MISMATCH}"
  "${LG_CRON}|DisputeVerdictOrderMismatch|${L_MISMATCH}"
)

FAILED=0
pass() { printf '  PASS  %s\n' "$1"; }
fail() { printf '  FAIL  %s\n' "$1"; FAILED=1; }
aws_text() { aws --region "$REGION" --output text "$@"; }

echo "Money alarms synthetic test: env=${ENV_NAME} region=${REGION} prefix=${PREFIX}"
echo

# ─── Check mode ───────────────────────────────────────────────────────────
echo "Metric filters (deployed pattern against the server's exact line, bare and runtime-prefixed):"
for entry in "${CHECKS[@]}"; do
  IFS='|' read -r lg metric sample <<< "$entry"
  pattern="$(aws_text logs describe-metric-filters --log-group-name "$lg" \
    --query "metricFilters[?metricTransformations[0].metricName=='${metric}'].filterPattern | [0]" 2>/dev/null)"
  if [ -z "$pattern" ] || [ "$pattern" = "None" ]; then
    fail "${metric} on ${lg}: no metric filter deployed"
    continue
  fi
  matches="$(aws_text logs test-metric-filter --filter-pattern "$pattern" \
    --log-event-messages "$sample" "${RT_PREFIX}${sample}" --query 'length(matches)' 2>/dev/null)"
  if [ "$matches" = "2" ]; then
    pass "${metric} on ${lg}: pattern ${pattern} matches"
  else
    fail "${metric} on ${lg}: pattern ${pattern} does not match '${sample}' bare and prefixed (matches: ${matches:-none})"
  fi
  queued="$(aws_text logs test-metric-filter --filter-pattern "$pattern" \
    --log-event-messages "${NEGATIVE_LINES[@]}" --query 'length(matches)' 2>/dev/null)"
  if [ "$queued" = "0" ]; then
    pass "${metric} on ${lg}: queued payouts and hostile client text are not counted"
  else
    fail "${metric} on ${lg}: pattern ${pattern} counts a queued-payout or hostile client line (matches: ${queued:-none})"
  fi
done
echo

echo "Escrow cron errors alarm shape:"
shape="$(aws_text cloudwatch describe-alarms --alarm-names "$A_CRON" \
  --query 'MetricAlarms[0].[Namespace,MetricName,Dimensions[0].Value,Threshold,AlarmActions[0]]' 2>/dev/null | tr '\t' '|')"
expected_shape="AWS/Lambda|Errors|${PREFIX}-escrow-cron|1.0|"
if [ -z "$shape" ] || [ "$shape" = "None" ]; then
  fail "${A_CRON}: alarm not found"
elif [ "${shape%%arn:*}" = "$expected_shape" ] && [[ "$shape" == *":${PREFIX}-alarms" ]]; then
  pass "${A_CRON}: AWS/Lambda Errors on ${PREFIX}-escrow-cron, threshold 1, notifies ${PREFIX}-alarms"
else
  fail "${A_CRON}: unexpected shape '${shape}' (expected ${expected_shape}arn:...:${PREFIX}-alarms)"
fi
echo

echo "Current alarm states:"
aws_text cloudwatch describe-alarms --alarm-names "${ALL_ALARMS[@]}" \
  --query 'MetricAlarms[].[AlarmName,StateValue]' 2>/dev/null | sed 's/^/  /'
found="$(aws_text cloudwatch describe-alarms --alarm-names "${ALL_ALARMS[@]}" --query 'length(MetricAlarms)' 2>/dev/null)"
if [ "${found:-0}" = "${#ALL_ALARMS[@]}" ]; then
  pass "all ${#ALL_ALARMS[@]} money alarms exist"
else
  fail "expected ${#ALL_ALARMS[@]} money alarms, found ${found:-0}"
fi
echo

if [ "$FIRE" = 0 ]; then
  if [ "$FAILED" = 0 ]; then
    echo "Check passed. Add --fire to make the alarms fire."
  else
    echo "Check FAILED."
  fi
  exit "$FAILED"
fi

# ─── Fire mode ────────────────────────────────────────────────────────────
echo "Firing: synthetic lines plus one failed escrow-cron invocation (no money moves)."
echo "Subscribers of ${PREFIX}-alarms get one ALARM and, later, one OK notification per alarm."
T0="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
STREAM="synthetic-esc13-$(date +%s)"
NOW_MS="$(( $(date +%s) * 1000 ))"

# An alarm already in ALARM cannot transition, so this run cannot prove it fires.
PENDING=()
SKIPPED=()
for a in "${ALL_ALARMS[@]}"; do
  state="$(aws_text cloudwatch describe-alarms --alarm-names "$a" --query 'MetricAlarms[0].StateValue' 2>/dev/null)"
  if [ "$state" = "ALARM" ]; then
    echo "  SKIP  ${a} is already in ALARM: not proven by this run, investigate it first"
    SKIPPED+=("$a")
  else
    PENDING+=("$a")
  fi
done

put_lines() {
  local lg="$1"; shift
  local events="[" first=1 msg
  for msg in "$@"; do
    [ "$first" = 1 ] || events+=","
    first=0
    events+="{\"timestamp\":${NOW_MS},\"message\":\"${msg}\"}"
  done
  events+="]"
  if ! aws --region "$REGION" logs create-log-stream --log-group-name "$lg" --log-stream-name "$STREAM" >/dev/null 2>&1; then
    fail "could not create log stream ${STREAM} in ${lg}"
    return
  fi
  if aws --region "$REGION" logs put-log-events --log-group-name "$lg" --log-stream-name "$STREAM" \
       --log-events "$events" >/dev/null 2>&1; then
    pass "wrote $# synthetic line(s) to ${lg}"
  else
    fail "could not write the synthetic lines to ${lg}"
  fi
}
put_lines "$LG_ESCROW" "$L_SETTLE" "$L_MISMATCH"
put_lines "$LG_CRON" "$L_SETTLE" "$L_PAYOUT" "$L_QUEUE" "$L_MISMATCH"
put_lines "$LG_SYNC" "$L_LINK"

# A JSON string cannot deserialize into APIGatewayProxyRequest, so the runtime fails the
# invocation before the handler runs: one Errors datapoint, no sweep, no money effect. If
# it ever reached the handler, the missing action would answer 400 and the invoke would
# carry no FunctionError, which is reported as "not exercised" rather than as a pass.
# --query and --output pin the answer whatever AWS_DEFAULT_OUTPUT says.
INVOKE_OUT="$(mktemp)"
invoke_meta="$(aws --region "$REGION" --output text lambda invoke --function-name "${PREFIX}-escrow-cron" \
  --cli-binary-format raw-in-base64-out --payload '"synthetic-esc13"' --query FunctionError "$INVOKE_OUT" 2>&1)"
rm -f "$INVOKE_OUT"
if [ "$invoke_meta" = "Unhandled" ]; then
  pass "escrow-cron invocation failed as intended (one Lambda error, handler never ran)"
else
  fail "escrow-cron invocation did not fail, so ${A_CRON} is not exercised (${invoke_meta})"
  still=()
  for a in "${PENDING[@]+"${PENDING[@]}"}"; do
    [ "$a" = "$A_CRON" ] || still+=("$a")
  done
  PENDING=("${still[@]+"${still[@]}"}")
fi

echo
echo "Waiting for each alarm's history to show a transition to ALARM after ${T0} (up to 25 minutes)..."
deadline=$(( $(date +%s) + 25 * 60 ))
FIRED=()
while [ ${#PENDING[@]} -gt 0 ] && [ "$(date +%s)" -lt "$deadline" ]; do
  sleep 60
  still=()
  for a in "${PENDING[@]}"; do
    n="$(aws_text cloudwatch describe-alarm-history --alarm-name "$a" --history-item-type StateUpdate \
      --start-date "$T0" --query "length(AlarmHistoryItems[?contains(HistorySummary, 'to ALARM')])" 2>/dev/null)"
    if [ -n "$n" ] && [ "$n" != "0" ] && [ "$n" != "None" ]; then
      pass "${a} reached ALARM"
      FIRED+=("$a")
    else
      still+=("$a")
    fi
  done
  PENDING=("${still[@]+"${still[@]}"}")
  if [ ${#PENDING[@]} -gt 0 ]; then
    echo "  ... still waiting for: ${PENDING[*]} ($(date -u +%H:%M:%SZ))"
  fi
done
for a in "${PENDING[@]+"${PENDING[@]}"}"; do
  fail "${a} did not reach ALARM within 25 minutes"
done

echo
echo "Cleaning up the synthetic log streams (the metric datapoints stay; alarms clear by themselves):"
for lg in "$LG_ESCROW" "$LG_CRON" "$LG_SYNC"; do
  if aws --region "$REGION" logs delete-log-stream --log-group-name "$lg" --log-stream-name "$STREAM" >/dev/null 2>&1; then
    echo "  deleted ${lg}:${STREAM}"
  else
    echo "  (no stream to delete in ${lg})"
  fi
done
echo
echo "Expected to clear by themselves with no new lines or errors:"
echo "  ${A_SETTLE}, ${A_PAYOUT}, ${A_QUEUE}, ${A_MISMATCH}: about 75 to 90 minutes after the lines were written"
echo "  ${A_ORDER}: 15 to 30 minutes"
echo "  ${A_CRON}: 20 to 25 minutes"

if [ "$WAIT_CLEAR" = 1 ] && [ ${#FIRED[@]} -gt 0 ]; then
  echo
  echo "Waiting for the fired alarms to return to OK by themselves (up to 120 minutes)..."
  deadline=$(( $(date +%s) + 120 * 60 ))
  WAITING=("${FIRED[@]}")
  while [ ${#WAITING[@]} -gt 0 ] && [ "$(date +%s)" -lt "$deadline" ]; do
    sleep 60
    still=()
    for a in "${WAITING[@]}"; do
      state="$(aws_text cloudwatch describe-alarms --alarm-names "$a" --query 'MetricAlarms[0].StateValue' 2>/dev/null)"
      if [ "$state" = "OK" ]; then
        pass "${a} back to OK"
      else
        still+=("$a")
      fi
    done
    WAITING=("${still[@]+"${still[@]}"}")
  done
  for a in "${WAITING[@]+"${WAITING[@]}"}"; do
    fail "${a} did not clear within 120 minutes (a real failure may be keeping it in ALARM)"
  done
fi

echo
for a in "${SKIPPED[@]+"${SKIPPED[@]}"}"; do
  fail "${a} was already in ALARM before the test: not proven, run again once it is OK"
done
if [ "$FAILED" = 0 ]; then
  echo "All money alarms fired."
else
  echo "Some checks FAILED or are not proven."
fi
exit "$FAILED"
