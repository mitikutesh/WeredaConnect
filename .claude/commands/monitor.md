Check observability health: CloudWatch alarms, Lambda errors, DynamoDB throttles, and API health.

The user may provide: $ARGUMENTS (specific check: "alarms", "errors", "health", "costs", or environment name)

Prerequisites: AWS CLI configured with access to `eu-north-1`.

Steps:

1. **Parse scope**:
   - Default environment: `dev`
   - If user specified an environment (dev/staging/prod), use that
   - If "alarms": check alarm configuration only
   - If "errors": check recent Lambda errors only
   - If "health": run health check only
   - If "costs": check AWS cost estimates only

2. **Health check**:
   ```bash
   # API health endpoint
   curl -s https://vhhm8gh9c1.execute-api.eu-north-1.amazonaws.com/health | python3 -m json.tool
   ```

3. **Check CloudWatch alarms exist** (run in parallel):
   ```bash
   # List all alarms for the environment
   aws cloudwatch describe-alarms \
     --alarm-name-prefix "wereda-connect-$ENV" \
     --region eu-north-1 \
     --query 'MetricAlarms[].{Name:AlarmName,State:StateValue,Metric:MetricName}' \
     --output table
   ```

   Expected alarms (flag if missing):
   - Lambda error rate > 1%
   - API Gateway 5xx rate > 0.5%
   - Lambda p99 duration > 5s
   - DynamoDB throttles > 0

4. **Check recent Lambda errors**:
   ```bash
   # Tail error logs from the last hour for key functions
   for fn in health crop trip order delivery escrow sync auth; do
     echo "=== wereda-connect-$ENV-$fn ==="
     aws logs filter-log-events \
       --log-group-name "/aws/lambda/wereda-connect-$ENV-$fn" \
       --filter-pattern "ERROR" \
       --start-time $(date -u -v-1H +%s)000 \
       --region eu-north-1 \
       --query 'events[].message' \
       --output text 2>/dev/null | head -5
   done
   ```

5. **Check DynamoDB throttles**:
   ```bash
   aws cloudwatch get-metric-statistics \
     --namespace AWS/DynamoDB \
     --metric-name ThrottledRequests \
     --start-time $(date -u -v-24H +%Y-%m-%dT%H:%M:%S) \
     --end-time $(date -u +%Y-%m-%dT%H:%M:%S) \
     --period 3600 \
     --statistics Sum \
     --region eu-north-1 \
     --query 'Datapoints[?Sum>`0`]'
   ```

6. **Check Lambda cold starts** (key metric for Ethiopian 3G users):
   ```bash
   aws logs filter-log-events \
     --log-group-name "/aws/lambda/wereda-connect-$ENV-health" \
     --filter-pattern "Init Duration" \
     --start-time $(date -u -v-24H +%s)000 \
     --region eu-north-1 \
     --query 'events[].message' \
     --output text | head -10
   ```

7. **Report results**:

   ```
   ## Monitoring Report ($ENV)

   ### Health
   - [UP/DOWN] API health endpoint

   ### Alarms
   - [OK/MISSING] Lambda error rate alarm
   - [OK/MISSING] API 5xx rate alarm
   - [OK/MISSING] Lambda duration alarm
   - [OK/MISSING] DynamoDB throttle alarm

   ### Recent Errors (last 1h)
   - [function]: [error count] errors — [sample message]
   - [or: "No errors in the last hour"]

   ### DynamoDB Throttles (last 24h)
   - [count] throttled requests — [or: "None"]

   ### Cold Starts (last 24h)
   - Average init duration: [X]ms (target: <100ms)

   ### Action Items
   1. [if alarms missing: "Create missing CloudWatch alarms"]
   2. [if errors found: "Investigate Lambda errors in [function]"]
   ```

8. If alarms are missing, offer to create them via CDK or AWS CLI.
