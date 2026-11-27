#!/usr/bin/env bash
#
# Fraud alarms synthetic test (FRD-16; also fires FRD-03's risk-settlement-skipped alarm).
#
# Proves that the CloudWatch metric filters and alarms of the fraud spec, section 23.1, fire on the
# exact lines the server writes, without writing a fraud-signals row or moving any money. Pairs with
# server/src/Services/FraudDetectionService.cs (SignalRecordedLine and the RISK_SCORE_* lines),
# server/src/Helpers/MoneyAlarmLog.cs and infra/lib/stacks/monitoring-stack.ts. The money alarms
# have their own script: scripts/money_alarms_synthetic.sh.
#
# Check mode (default; read-only apart from `aws logs test-metric-filter`):
#   - reads each deployed filter and tests it against the server's exact line, bare and with the
#     Lambda runtime's tab-separated prefix (the way Console.WriteLine reaches CloudWatch), and
#     against neighbouring lines and defanged client text, which it must not count
#   - the per-type filter is a JSON filter: if it reads the bare line but not the prefixed one, the
#     per-type metric stays empty in real traffic (FAIL; the spike alarm reads the total, a plain
#     term, and still works)
#   - asserts that the composite fraud-signal-volume-spike alarm notifies <prefix>-alarms and that
#     its two children notify nothing
#   - prints every alarm's state and the anomaly model's state
#
# --fire writes, into a throwaway log stream: --spike N signal lines (signalType SYNTHETIC_FRD16, so
# the per-type metric gets its own dimension value) into the sync log group, and one line for each
# risk alarm into the log group that alarm reads. It then waits until each alarm's history shows the
# transition to ALARM, and checks that the per-type metric counted the N lines. It never calls
# set-alarm-state; every alarm clears by itself. Subscribers of <prefix>-alarms get one ALARM and,
# later, one OK notification per alarm. An alarm already in ALARM cannot transition: it is reported
# as not proven and the run does not pass.
#
# The spike alarm needs a trained anomaly model: run --fire at least a day after the first FRD-16
# deploy (the script prints the model state). The synthetic spike then becomes part of the model's
# history; the script prints the command that leaves its window out of the model.
#
# Usage:
#   scripts/fraud_alarms_synthetic.sh [--env dev|staging|prod] [--region <aws-region>]
#                                     [--fire [--wait-clear]] [--spike <n>] [--allow-prod]
#
#   --env         environment (default dev)
#   --region      AWS region (default eu-north-1 for dev, me-south-1 otherwise)
#   --fire        write the synthetic lines, wait for ALARM
#   --wait-clear  after --fire, keep polling until every fired alarm is OK again
#   --spike       signal lines in the spike (default 30, at least 10, the alarm's floor); raise it
#                 where the learned band is higher than that (staging, prod)
#   --allow-prod  required for --fire on prod
#
# Needs an AWS identity with: logs:DescribeMetricFilters, logs:DescribeLogGroups,
# logs:TestMetricFilter, cloudwatch:DescribeAlarms and cloudwatch:DescribeAnomalyDetectors (check);
# plus logs:CreateLogStream, logs:PutLogEvents, logs:DeleteLogStream,
# cloudwatch:DescribeAlarmHistory and cloudwatch:GetMetricStatistics (--fire).
#
# Exit codes: 0 every check (and, with --fire, every alarm) passed; 1 a check or an alarm failed,
# or an alarm was already in ALARM so its firing is not proven; 2 usage error.
#
set -uo pipefail

ENV_NAME="dev"
REGION=""
FIRE=0
WAIT_CLEAR=0
ALLOW_PROD=0
SPIKE=30

while [ $# -gt 0 ]; do
  case "$1" in
    --env) ENV_NAME="${2:-}"; shift 2 ;;
    --region) REGION="${2:-}"; shift 2 ;;
    --fire) FIRE=1; shift ;;
    --wait-clear) WAIT_CLEAR=1; shift ;;
    --spike) SPIKE="${2:-}"; shift 2 ;;
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
case "$SPIKE" in
  ''|*[!0-9]*) echo "--spike must be a whole number" >&2; exit 2 ;;
esac
if [ "$SPIKE" -lt 10 ] || [ "$SPIKE" -gt 5000 ]; then
  echo "--spike must be from 10 (the alarm's floor) to 5000" >&2; exit 2
fi
if [ "$WAIT_CLEAR" = 1 ] && [ "$FIRE" = 0 ]; then
  echo "--wait-clear only makes sense with --fire" >&2; exit 2
fi
if [ "$FIRE" = 1 ] && [ "$ENV_NAME" = "prod" ] && [ "$ALLOW_PROD" = 0 ]; then
  echo "refusing --fire on prod without --allow-prod (it pages the alarm subscribers)" >&2; exit 2
fi
command -v aws >/dev/null 2>&1 || { echo "aws CLI not found" >&2; exit 2; }

PREFIX="wereda-connect-${ENV_NAME}"
NS="WeredaConnect/${ENV_NAME}"
LG_SYNC="/aws/lambda/${PREFIX}-sync"
LG_ESCROW="/aws/lambda/${PREFIX}-escrow"
LG_CRON="/aws/lambda/${PREFIX}-escrow-cron"
LG_WEBHOOK="/aws/lambda/${PREFIX}-telebirr-webhook"
# Every Lambda whose handler builds a FraudDetectionService (monitoring-stack.ts, fraudLogGroups).
FRAUD_LGS=("$LG_SYNC" "$LG_ESCROW" "$LG_CRON" "/aws/lambda/${PREFIX}-order-cron"
  "/aws/lambda/${PREFIX}-auth-refresh" "/aws/lambda/${PREFIX}-admin")

A_SPIKE="${PREFIX}-fraud-signal-volume-spike"
A_ANOMALY="${PREFIX}-fraud-signal-volume-anomaly"
A_FLOOR="${PREFIX}-fraud-signal-volume-floor"
A_UNAVAILABLE="${PREFIX}-risk-score-unavailable"
A_TRUNCATED="${PREFIX}-risk-score-truncated"
A_UNKNOWN="${PREFIX}-risk-score-unknown-record"
A_HOLD="${PREFIX}-risk-hold-order-mismatch"
A_TABLE="${PREFIX}-risk-table-unconfigured"
A_SKIPPED="${PREFIX}-risk-settlement-skipped"
METRIC_ALARMS=("$A_ANOMALY" "$A_FLOOR" "$A_UNAVAILABLE" "$A_TRUNCATED" "$A_UNKNOWN" "$A_HOLD" "$A_TABLE" "$A_SKIPPED")
# The alarms --fire proves (the two children are checked through the composite).
FIRED_ALARMS=("$A_SPIKE" "$A_UNAVAILABLE" "$A_TRUNCATED" "$A_UNKNOWN" "$A_HOLD" "$A_TABLE" "$A_SKIPPED")

# Sample lines: the exact formats of the server, with a synthetic id.
SID="synthetic-frd16"
SYNTHETIC_TYPE="SYNTHETIC_FRD16"
L_SIGNAL="{\"event\":\"FRAUD_SIGNAL_RECORDED\",\"signalType\":\"${SYNTHETIC_TYPE}\",\"entityType\":\"synthetic\"}"
L_UNAVAILABLE="RISK_SCORE_UNAVAILABLE userId=${SID} error=SyntheticException"
L_TRUNCATED="RISK_SCORE_TRUNCATED userId=${SID} pages=2"
L_UNKNOWN="RISK_SCORE_UNKNOWN_RECORD userId=${SID} rows=1 recordType=synthetic"
L_HOLD="RISK_HOLD_ORDER_MISMATCH escrowId=${SID} heldFor=refund legs=order:synthetic"
L_TABLE="RISK_TABLE_UNCONFIGURED action=auto_release_and_refund"
L_SKIPPED="RISK_SETTLEMENT_SKIPPED escrowId=${SID} sweep=auto-refund reason=score_unavailable holdReason=-"
L_CREDIT="CLEAN_DELIVERY_CREDIT_FAILED escrowId=${SID} error=SyntheticException"

# How the Lambda runtime writes a line to CloudWatch: time, request id and level, tab separated.
RT_PREFIX=$'2026-11-07T10:00:00.000Z\tsynthetic-frd16-request\tinfo\t'

# Lines no FRD-16 filter may count, besides the other metrics' samples: neighbours of the same log
# groups, and client text that spells every token as the server writes it (it turns _ into -).
HOSTILE="FRAUD-SIGNAL-RECORDED-RISK-SCORE-UNAVAILABLE-RISK-SCORE-TRUNCATED-RISK-SCORE-UNKNOWN-RECORD-RISK-HOLD-ORDER-MISMATCH-RISK-TABLE-UNCONFIGURED-RISK-SETTLEMENT-SKIPPED-CLEAN-DELIVERY-CREDIT-FAILED"
NEUTRAL_LINES=(
  "CLEAN_DELIVERY_CREDITED escrowId=${SID} credited=3 alreadyCredited=0 failed=0"
  "Failed to record fraud signal ROLE_FORBIDDEN for user ${SID}: [TimeoutException]"
  "ESCROW_FRAUD_HOLD escrowId=${SID} holdReason=risk_critical heldFor=refund sweep=auto-refund"
  "Op ${HOSTILE}: status=error, msg=Invalid action: '${HOSTILE}'. Must be create, update, or delete."
  "{\"timestamp\":\"2026-11-07T10:00:00Z\",\"category\":\"ESCROW\",\"action\":\"DISPUTE\",\"correlationId\":\"${HOSTILE}\",\"entityId\":\"${HOSTILE}\",\"outcome\":\"failure\"}"
)

# metric | sample line (the other samples are each check's negatives)
SAMPLES=(
  "FraudSignalsRecorded|${L_SIGNAL}"
  "RiskScoreUnavailable|${L_UNAVAILABLE}"
  "RiskScoreTruncated|${L_TRUNCATED}"
  "RiskScoreUnknownRecord|${L_UNKNOWN}"
  "RiskHoldOrderMismatch|${L_HOLD}"
  "RiskTableUnconfigured|${L_TABLE}"
  "RiskSettlementSkipped|${L_SKIPPED}"
  "CleanDeliveryCreditFailed|${L_CREDIT}"
)

FAILED=0
pass() { printf '  PASS  %s\n' "$1"; }
fail() { printf '  FAIL  %s\n' "$1"; FAILED=1; }
note() { printf '  NOTE  %s\n' "$1"; }
aws_text() { aws --region "$REGION" --output text "$@"; }

# A JSON string literal of $1 (quotes, backslashes, tabs and newlines escaped).
json_str() {
  local s="$1"
  s="${s//\\/\\\\}"
  s="${s//\"/\\\"}"
  s="${s//$'\t'/\\t}"
  s="${s//$'\n'/\\n}"
  printf '"%s"' "$s"
}

# How many of the messages the pattern matches. --cli-input-json keeps the CLI from reading a JSON
# message or pattern as a parameter value of its own.
match_count() {
  local pattern="$1"; shift
  local input first=1 m
  input="{\"filterPattern\":$(json_str "$pattern"),\"logEventMessages\":["
  for m in "$@"; do
    [ "$first" = 1 ] || input+=","
    first=0
    input+="$(json_str "$m")"
  done
  input+="]}"
  aws_text logs test-metric-filter --cli-input-json "$input" --query 'length(matches)' 2>/dev/null
}

deployed_pattern() { # log group, metric
  aws_text logs describe-metric-filters --log-group-name "$1" \
    --query "metricFilters[?metricTransformations[0].metricName=='$2'].filterPattern | [0]" 2>/dev/null
}

sample_of() { # metric
  local entry
  for entry in "${SAMPLES[@]}"; do
    [ "${entry%%|*}" = "$1" ] && { printf '%s' "${entry#*|}"; return; }
  done
}

negatives_of() { # metric: every other sample plus the neutral lines, one per line of output
  local entry
  for entry in "${SAMPLES[@]}"; do
    [ "${entry%%|*}" = "$1" ] || printf '%s\n' "${entry#*|}"
  done
  printf '%s\n' "${NEUTRAL_LINES[@]}"
}

check_filter() { # log group, metric
  local lg="$1" metric="$2" pattern sample matches neg
  local negatives=()
  pattern="$(deployed_pattern "$lg" "$metric")"
  if [ -z "$pattern" ] || [ "$pattern" = "None" ]; then
    fail "${metric} on ${lg}: no metric filter deployed"
    return
  fi
  sample="$(sample_of "$metric")"
  matches="$(match_count "$pattern" "$sample" "${RT_PREFIX}${sample}")"
  if [ "$matches" = "2" ]; then
    pass "${metric} on ${lg}: ${pattern} matches the server line, bare and prefixed"
  else
    fail "${metric} on ${lg}: ${pattern} does not match '${sample}' bare and prefixed (matches: ${matches:-none})"
  fi
  while IFS= read -r neg; do negatives+=("$neg"); done < <(negatives_of "$metric")
  matches="$(match_count "$pattern" "${negatives[@]}")"
  if [ "$matches" = "0" ]; then
    pass "${metric} on ${lg}: other risk lines, neighbours and defanged client text are not counted"
  else
    fail "${metric} on ${lg}: ${pattern} counts a line it must not (matches: ${matches:-none})"
  fi
}

echo "Fraud alarms synthetic test: env=${ENV_NAME} region=${REGION} prefix=${PREFIX}"
echo

webhook="$(aws_text logs describe-log-groups --log-group-name-prefix "$LG_WEBHOOK" \
  --query "length(logGroups[?logGroupName=='${LG_WEBHOOK}'])" 2>/dev/null)"
if [ "$webhook" = "1" ]; then
  FRAUD_LGS+=("$LG_WEBHOOK")
else
  note "no ${LG_WEBHOOK} (live Telebirr gateway off): its filters are not checked"
fi

# ─── Check mode ───────────────────────────────────────────────────────────
echo "Signal and risk score filters on every Lambda that builds a FraudDetectionService:"
for lg in "${FRAUD_LGS[@]}"; do
  for metric in FraudSignalsRecorded RiskScoreUnavailable RiskScoreTruncated RiskScoreUnknownRecord; do
    check_filter "$lg" "$metric"
  done
done
echo

echo "Per-type filter (JSON, dimension SignalType):"
for lg in "${FRAUD_LGS[@]}"; do
  pattern="$(deployed_pattern "$lg" FraudSignalsByType)"
  if [ -z "$pattern" ] || [ "$pattern" = "None" ]; then
    fail "FraudSignalsByType on ${lg}: no metric filter deployed"
    continue
  fi
  dimension="$(aws_text logs describe-metric-filters --log-group-name "$lg" \
    --query "metricFilters[?metricTransformations[0].metricName=='FraudSignalsByType'].metricTransformations[0].dimensions.SignalType | [0]" 2>/dev/null)"
  if [ "$dimension" = '$.signalType' ]; then
    pass "FraudSignalsByType on ${lg}: dimension SignalType from \$.signalType"
  else
    fail "FraudSignalsByType on ${lg}: dimension SignalType is '${dimension}', expected \$.signalType"
  fi
  bare="$(match_count "$pattern" "$L_SIGNAL")"
  behind="$(match_count "$pattern" "${RT_PREFIX}${L_SIGNAL}")"
  if [ "$bare" = "1" ] && [ "$behind" = "1" ]; then
    pass "FraudSignalsByType on ${lg}: ${pattern} reads the line bare and behind the runtime prefix"
  elif [ "$bare" = "1" ]; then
    fail "FraudSignalsByType on ${lg}: ${pattern} reads the bare line only, so the per-type metric stays empty for real traffic (the spike alarm is unaffected); log the line without the runtime prefix or switch to a space-delimited pattern"
  else
    fail "FraudSignalsByType on ${lg}: ${pattern} does not read '${L_SIGNAL}' (bare ${bare:-none}, prefixed ${behind:-none})"
  fi
  neutral="$(match_count "$pattern" "${NEUTRAL_LINES[@]}" "$L_UNAVAILABLE" "$L_SKIPPED")"
  if [ "$neutral" = "0" ]; then
    pass "FraudSignalsByType on ${lg}: audit JSON and other lines are not counted"
  else
    fail "FraudSignalsByType on ${lg}: ${pattern} counts a line it must not (matches: ${neutral:-none})"
  fi
done
echo

echo "Single-source risk filters (and the credit failure, on both Lambdas that write credits):"
check_filter "$LG_ESCROW" RiskHoldOrderMismatch
check_filter "$LG_CRON" RiskTableUnconfigured
check_filter "$LG_CRON" RiskSettlementSkipped
check_filter "$LG_CRON" CleanDeliveryCreditFailed
# FUP-155: the admin Lambda logs the token after a manual payout close.
check_filter "/aws/lambda/${PREFIX}-admin" CleanDeliveryCreditFailed
echo

echo "Spike alarm wiring:"
spike_actions="$(aws_text cloudwatch describe-alarms --alarm-names "$A_SPIKE" --alarm-types CompositeAlarm \
  --query 'CompositeAlarms[0].[AlarmActions[0],OKActions[0]]' 2>/dev/null | tr '\t' '|')"
if [[ "$spike_actions" == *":${PREFIX}-alarms|"*":${PREFIX}-alarms" ]]; then
  pass "${A_SPIKE}: notifies ${PREFIX}-alarms on ALARM and OK"
else
  fail "${A_SPIKE}: composite alarm missing or not wired to ${PREFIX}-alarms ('${spike_actions}')"
fi
child_actions="$(aws_text cloudwatch describe-alarms --alarm-names "$A_ANOMALY" "$A_FLOOR" \
  --query 'MetricAlarms[].[AlarmName,length(AlarmActions),length(OKActions)]' 2>/dev/null | tr '\t' '|' | sort)"
expected_children="$(printf '%s|0|0\n%s|0|0\n' "$A_ANOMALY" "$A_FLOOR" | sort)"
if [ "$child_actions" = "$expected_children" ]; then
  pass "the anomaly and floor children exist and notify nothing (only the composite pages)"
else
  fail "children of ${A_SPIKE}: expected two alarms without actions, got '${child_actions}'"
fi
MODEL_STATE="$(aws_text cloudwatch describe-anomaly-detectors --namespace "$NS" --metric-name FraudSignalsRecorded \
  --query 'AnomalyDetectors[0].StateValue' 2>/dev/null)"
echo "  anomaly model for ${NS} FraudSignalsRecorded: ${MODEL_STATE:-not found}"
echo

echo "Current alarm states:"
aws_text cloudwatch describe-alarms --alarm-names "${METRIC_ALARMS[@]}" \
  --query 'MetricAlarms[].[AlarmName,StateValue]' 2>/dev/null | sed 's/^/  /'
aws_text cloudwatch describe-alarms --alarm-names "$A_SPIKE" --alarm-types CompositeAlarm \
  --query 'CompositeAlarms[].[AlarmName,StateValue]' 2>/dev/null | sed 's/^/  /'
found="$(aws_text cloudwatch describe-alarms --alarm-names "${METRIC_ALARMS[@]}" --query 'length(MetricAlarms)' 2>/dev/null)"
if [ "${found:-0}" = "${#METRIC_ALARMS[@]}" ]; then
  pass "all ${#METRIC_ALARMS[@]} metric alarms exist"
else
  fail "expected ${#METRIC_ALARMS[@]} metric alarms, found ${found:-0}"
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
echo "Firing: ${SPIKE} synthetic signal lines plus one line per risk alarm (no fraud-signals row, no money)."
echo "Subscribers of ${PREFIX}-alarms get one ALARM and, later, one OK notification per alarm."
case "$MODEL_STATE" in
  TRAINED) ;;
  *) note "the anomaly model is ${MODEL_STATE:-not found}, not TRAINED: ${A_ANOMALY} may stay INSUFFICIENT_DATA and ${A_SPIKE} may not fire; run again a day after the deploy" ;;
esac
T0="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
STREAM="synthetic-frd16-$(date +%s)"
NOW_MS="$(( $(date +%s) * 1000 ))"

alarm_state() { # name
  if [ "$1" = "$A_SPIKE" ]; then
    aws_text cloudwatch describe-alarms --alarm-names "$1" --alarm-types CompositeAlarm \
      --query 'CompositeAlarms[0].StateValue' 2>/dev/null
  else
    aws_text cloudwatch describe-alarms --alarm-names "$1" --query 'MetricAlarms[0].StateValue' 2>/dev/null
  fi
}

# An alarm already in ALARM cannot transition, so this run cannot prove it fires.
PENDING=()
SKIPPED=()
for a in "${FIRED_ALARMS[@]}"; do
  if [ "$(alarm_state "$a")" = "ALARM" ]; then
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
    events+="{\"timestamp\":${NOW_MS},\"message\":$(json_str "$msg")}"
  done
  events+="]"
  if ! aws --region "$REGION" logs create-log-stream --log-group-name "$lg" --log-stream-name "$STREAM" >/dev/null 2>&1; then
    fail "could not create log stream ${STREAM} in ${lg}"
    return
  fi
  # Through a file: a large --spike would exceed the single-argument limit (128 KB on Linux).
  local events_file
  events_file=$(mktemp "${TMPDIR:-/tmp}/fraud-alarms-events.XXXXXX")
  printf '%s' "$events" > "$events_file"
  if aws --region "$REGION" logs put-log-events --log-group-name "$lg" --log-stream-name "$STREAM" \
       --log-events "file://${events_file}" >/dev/null 2>&1; then
    rm -f "$events_file"
    pass "wrote $# synthetic line(s) to ${lg}"
  else
    rm -f "$events_file"
    fail "could not write the synthetic lines to ${lg}"
  fi
}
SPIKE_LINES=()
for _ in $(seq 1 "$SPIKE"); do SPIKE_LINES+=("$L_SIGNAL"); done
put_lines "$LG_SYNC" "${SPIKE_LINES[@]}" "$L_UNAVAILABLE" "$L_TRUNCATED" "$L_UNKNOWN"
put_lines "$LG_ESCROW" "$L_HOLD"
put_lines "$LG_CRON" "$L_TABLE" "$L_SKIPPED"

echo
echo "Waiting for each alarm's history to show a transition to ALARM after ${T0} (up to 30 minutes)..."
deadline=$(( $(date +%s) + 30 * 60 ))
FIRED=()
while [ ${#PENDING[@]} -gt 0 ] && [ "$(date +%s)" -lt "$deadline" ]; do
  sleep 60
  still=()
  for a in "${PENDING[@]}"; do
    type="MetricAlarm"
    [ "$a" = "$A_SPIKE" ] && type="CompositeAlarm"
    n="$(aws_text cloudwatch describe-alarm-history --alarm-name "$a" --alarm-types "$type" \
      --history-item-type StateUpdate --start-date "$T0" \
      --query "length(AlarmHistoryItems[?contains(HistorySummary, 'to ALARM')])" 2>/dev/null)"
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
  if [ "$a" = "$A_SPIKE" ]; then
    fail "${a} did not reach ALARM within 30 minutes (${A_ANOMALY}: $(alarm_state "$A_ANOMALY"), ${A_FLOOR}: $(alarm_state "$A_FLOOR"); a learned band above ${SPIKE} needs a larger --spike)"
  else
    fail "${a} did not reach ALARM within 30 minutes"
  fi
done

echo
echo "Per-type metric (SignalType=${SYNTHETIC_TYPE}):"
by_type="$(aws_text cloudwatch get-metric-statistics --namespace "$NS" --metric-name FraudSignalsByType \
  --dimensions "Name=SignalType,Value=${SYNTHETIC_TYPE}" --start-time "$T0" \
  --end-time "$(date -u +%Y-%m-%dT%H:%M:%SZ)" --period 3600 --statistics Sum \
  --query 'sum(Datapoints[].Sum)' 2>/dev/null)"
if [ "${by_type%.*}" = "$SPIKE" ]; then
  pass "FraudSignalsByType counted the ${SPIKE} synthetic lines under their own type"
else
  fail "FraudSignalsByType counted ${by_type:-nothing} under SignalType=${SYNTHETIC_TYPE}, expected ${SPIKE}"
fi

echo
echo "Cleaning up the synthetic log streams (the metric datapoints stay; alarms clear by themselves):"
for lg in "$LG_SYNC" "$LG_ESCROW" "$LG_CRON"; do
  if aws --region "$REGION" logs delete-log-stream --log-group-name "$lg" --log-stream-name "$STREAM" >/dev/null 2>&1; then
    echo "  deleted ${lg}:${STREAM}"
  else
    echo "  (no stream to delete in ${lg})"
  fi
done
echo
echo "Expected to clear by themselves with no new lines:"
echo "  ${A_SPIKE}: 15 to 30 minutes (once a 15 minute period is under the floor or the band)"
echo "  the risk alarms: about 75 to 90 minutes after the lines were written"
echo
echo "The spike is now part of the anomaly model's history and widens its band for a while. To leave"
echo "the window out (replace <end> with a time after the spike):"
echo "  aws --region ${REGION} cloudwatch put-anomaly-detector --namespace ${NS} --metric-name FraudSignalsRecorded \\"
echo "    --stat Sum --configuration '{\"ExcludedTimeRanges\":[{\"StartTime\":\"${T0}\",\"EndTime\":\"<end>\"}]}'"

if [ "$WAIT_CLEAR" = 1 ] && [ ${#FIRED[@]} -gt 0 ]; then
  echo
  echo "Waiting for the fired alarms to return to OK by themselves (up to 120 minutes)..."
  deadline=$(( $(date +%s) + 120 * 60 ))
  WAITING=("${FIRED[@]}")
  while [ ${#WAITING[@]} -gt 0 ] && [ "$(date +%s)" -lt "$deadline" ]; do
    sleep 60
    still=()
    for a in "${WAITING[@]}"; do
      if [ "$(alarm_state "$a")" = "OK" ]; then
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
  echo "All fraud alarms fired."
else
  echo "Some checks FAILED or are not proven."
fi
exit "$FAILED"
