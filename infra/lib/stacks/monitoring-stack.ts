import * as cdk from 'aws-cdk-lib';
import { Construct } from 'constructs';
import * as cloudwatch from 'aws-cdk-lib/aws-cloudwatch';
import * as cloudwatch_actions from 'aws-cdk-lib/aws-cloudwatch-actions';
import * as logs from 'aws-cdk-lib/aws-logs';
import * as sns from 'aws-cdk-lib/aws-sns';
import { EnvironmentConfig } from '../config/environments';

export interface MonitoringStackProps extends cdk.StackProps {
  readonly envConfig: EnvironmentConfig;
  readonly apiUrl: string;
  readonly apiId: string;
}

/**
 * Lambda function definition for metrics — uses naming conventions from ApiStack.
 * No cross-stack references needed; we reference log groups and functions by name.
 */
interface LambdaFunctionDef {
  /** Short label for dashboard display */
  readonly label: string;
  /** Suffix after `wereda-connect-{env}-` */
  readonly nameSuffix: string;
}

/**
 * DynamoDB table definition for throttle monitoring.
 */
interface DynamoTableDef {
  /** Short label for dashboard display */
  readonly label: string;
  /** Suffix after `wereda-connect-{env}-` */
  readonly nameSuffix: string;
}

/**
 * Monitoring stack: CloudWatch dashboard, metric filters, and alarms.
 *
 * S2-04 Phase 2: Audit Logging — compliance observability layer.
 *
 * Cost: ~$7/month (1 dashboard @ $3 + 12 alarms @ $0.10 + 9 custom metrics from the log
 * filters @ $0.30; the live gateway adds 1 alarm and 1 metric). Metric filters are free.
 *
 * References Lambda functions and DynamoDB tables by name (no cross-stack refs needed).
 */
export class MonitoringStack extends cdk.Stack {
  constructor(scope: Construct, id: string, props: MonitoringStackProps) {
    super(scope, id, props);

    const { envConfig } = props;
    const prefix = `wereda-connect-${envConfig.name}`;

    // ─── Lambda functions to monitor (name suffix after prefix) ──────
    const lambdaFunctions: LambdaFunctionDef[] = [
      { label: 'Auth Login', nameSuffix: 'auth-login' },
      { label: 'Auth Register', nameSuffix: 'auth-register' },
      { label: 'Auth Refresh', nameSuffix: 'auth-refresh' },
      { label: 'Auth Password Reset', nameSuffix: 'auth-password-reset' },
      { label: 'Sync', nameSuffix: 'sync' },
      { label: 'Escrow', nameSuffix: 'escrow' },
      { label: 'Escrow Cron', nameSuffix: 'escrow-cron' },
      { label: 'Order Cron', nameSuffix: 'order-cron' },
      { label: 'Crops', nameSuffix: 'crops' },
      { label: 'Trips', nameSuffix: 'trips' },
      { label: 'Orders', nameSuffix: 'orders' },
      { label: 'Deliveries', nameSuffix: 'deliveries' },
      { label: 'Admin', nameSuffix: 'admin' },
      { label: 'Health', nameSuffix: 'health' },
    ];

    // ─── DynamoDB tables to monitor ──────────────────────────────────
    const dynamoTables: DynamoTableDef[] = [
      { label: 'Users', nameSuffix: 'users' },
      { label: 'Crops', nameSuffix: 'crops' },
      { label: 'Trips', nameSuffix: 'trips' },
      { label: 'Orders', nameSuffix: 'orders' },
      { label: 'Escrows', nameSuffix: 'escrows' },
      { label: 'Escrow Audit', nameSuffix: 'escrow-audit' },
      { label: 'Deliveries', nameSuffix: 'deliveries' },
      { label: 'Payments', nameSuffix: 'payments' },
      { label: 'Sync Metadata', nameSuffix: 'sync-metadata' },
      { label: 'Fraud Signals', nameSuffix: 'fraud-signals' },
    ];

    // ─── SNS Topic for alarm notifications ───────────────────────────
    const alarmTopic = new sns.Topic(this, 'AlarmTopic', {
      topicName: `${prefix}-alarms`,
      displayName: `WeredaConnect ${envConfig.name} Alarms`,
    });
    const snsAction = new cloudwatch_actions.SnsAction(alarmTopic);

    // ─── Metric Filters on audit logs ────────────────────────────────
    // Auth function log group — extract login success/failure metrics
    const authLogGroupName = `/aws/lambda/${prefix}-auth-login`;
    const authLogGroup = logs.LogGroup.fromLogGroupName(
      this,
      'AuthLogGroup',
      authLogGroupName,
    );

    // Escrow function log group — extract escrow activity metrics
    const escrowLogGroupName = `/aws/lambda/${prefix}-escrow`;
    const escrowLogGroup = logs.LogGroup.fromLogGroupName(
      this,
      'EscrowLogGroup',
      escrowLogGroupName,
    );

    // Escrow cron log group (the hourly money sweeps). The api-stack Lambda construct
    // creates it as an explicit LogGroup, so a filter on it is safe on a fresh deploy.
    const escrowCronLogGroup = logs.LogGroup.fromLogGroupName(
      this,
      'EscrowCronLogGroup',
      `/aws/lambda/${prefix}-escrow-cron`,
    );

    const metricNamespace = `WeredaConnect/${envConfig.name}`;

    // AUTH: Login failed
    new logs.MetricFilter(this, 'AuthLoginFailedFilter', {
      logGroup: authLogGroup,
      filterPattern: logs.FilterPattern.all(
        logs.FilterPattern.stringValue('$.category', '=', 'SECURITY'),
        logs.FilterPattern.stringValue('$.action', '=', 'LOGIN_FAILED'),
      ),
      metricNamespace,
      metricName: 'AuthLoginFailed',
      metricValue: '1',
      defaultValue: 0,
    });

    // AUTH: Login success
    new logs.MetricFilter(this, 'AuthLoginSuccessFilter', {
      logGroup: authLogGroup,
      filterPattern: logs.FilterPattern.all(
        logs.FilterPattern.stringValue('$.category', '=', 'SECURITY'),
        logs.FilterPattern.stringValue('$.action', '=', 'LOGIN'),
        logs.FilterPattern.stringValue('$.outcome', '=', 'success'),
      ),
      metricNamespace,
      metricName: 'AuthLoginSuccess',
      metricValue: '1',
      defaultValue: 0,
    });

    // ESCROW: Dispute
    new logs.MetricFilter(this, 'EscrowDisputeFilter', {
      logGroup: escrowLogGroup,
      filterPattern: logs.FilterPattern.all(
        logs.FilterPattern.stringValue('$.category', '=', 'ESCROW'),
        logs.FilterPattern.stringValue('$.action', '=', 'DISPUTE'),
      ),
      metricNamespace,
      metricName: 'EscrowDispute',
      metricValue: '1',
      defaultValue: 0,
    });

    // ESCROW: Initiate
    new logs.MetricFilter(this, 'EscrowInitiateFilter', {
      logGroup: escrowLogGroup,
      filterPattern: logs.FilterPattern.all(
        logs.FilterPattern.stringValue('$.category', '=', 'ESCROW'),
        logs.FilterPattern.stringValue('$.action', '=', 'INITIATE'),
      ),
      metricNamespace,
      metricName: 'EscrowInitiate',
      metricValue: '1',
      defaultValue: 0,
    });

    // ESCROW: Release
    new logs.MetricFilter(this, 'EscrowReleaseFilter', {
      logGroup: escrowLogGroup,
      filterPattern: logs.FilterPattern.all(
        logs.FilterPattern.stringValue('$.category', '=', 'ESCROW'),
        logs.FilterPattern.stringValue('$.action', '=', 'RELEASE'),
      ),
      metricNamespace,
      metricName: 'EscrowRelease',
      metricValue: '1',
      defaultValue: 0,
    });

    // ESCROW: order-funded escrow problems in the sync Lambda (ESC-13 widened the
    // create-failed filter). Each token is a funded order whose money the escrow
    // subsystem cannot see or settle:
    //   ORDER_ESCROW_CREATE_FAILED: no escrow record (delivery-time linkage self-heals);
    //   ORDER_ESCROW_LINK_FAILED: the delivery is not tied to the escrow, so delivery
    //     confirmation may never reach it and the refund-safety check cannot see it;
    //   ORDER_ESCROW_REFUND_SCHEDULE_FAILED: a cancelled order's money stays held;
    //   DISPUTE_NO_ESCROW (ESC-07): a funded order's delivery was disputed but no escrow
    //     holds its money, so the dispute was refused;
    //   ORDER_TRANSPORT_CANCEL_LATE_DELIVERY (ESC-06): a buyer cancelled an order after a
    //     transport failure and a delivery that was not pending (picked up, or unreadable)
    //     held the order's slot right after: goods may have left the farm on a refunded order;
    //   ORDER_TRANSPORT_CANCEL_PICKUP_REFUSED (ESC-06): a pickup synced after that cancel had
    //     cancelled the delivery (the reverse offline race): the trucker has the goods.
    const syncLogGroup = logs.LogGroup.fromLogGroupName(
      this,
      'SyncLogGroup',
      `/aws/lambda/${prefix}-sync`,
    );
    new logs.MetricFilter(this, 'OrderEscrowProblemsFilter', {
      logGroup: syncLogGroup,
      filterPattern: logs.FilterPattern.anyTerm(
        'ORDER_ESCROW_CREATE_FAILED',
        'ORDER_ESCROW_LINK_FAILED',
        'ORDER_ESCROW_REFUND_SCHEDULE_FAILED',
        'DISPUTE_NO_ESCROW',
        'ORDER_TRANSPORT_CANCEL_LATE_DELIVERY',
        'ORDER_TRANSPORT_CANCEL_PICKUP_REFUSED',
      ),
      metricNamespace,
      metricName: 'OrderEscrowProblems',
      metricValue: '1',
      defaultValue: 0,
    });

    // ESC-13: settlement failures. EscrowService logs one ESCROW_SETTLEMENT_FAILED line
    // per failed release, refund or trucker leg (from the escrow Lambda on the admin
    // routes, from escrow-cron on the sweeps); the cron and the admin routes add one per
    // exception mid-settlement. Plain-text tokens, so the Lambda runtime's line prefix
    // does not matter. Two filters publish one metric. Line formats:
    // server/src/Helpers/MoneyAlarmLog.cs.
    const countSettlementFailures = (id: string, logGroup: logs.ILogGroup) =>
      new logs.MetricFilter(this, id, {
        logGroup,
        filterPattern: logs.FilterPattern.literal('"ESCROW_SETTLEMENT_FAILED"'),
        metricNamespace,
        metricName: 'EscrowSettlementFailures',
        metricValue: '1',
        defaultValue: 0,
      });
    countSettlementFailures('EscrowSettlementFailuresFilter', escrowLogGroup);
    countSettlementFailures('EscrowCronSettlementFailuresFilter', escrowCronLogGroup);

    // ESC-05 (FUP-06): the unknown outcomes alone (Telebirr may already have paid), for their own
    // alarm and runbook. The same lines also count above. Both terms must be on the line; the
    // server logs outcome=unknown only after a settlement took its lock (or for a stale lock).
    const countUnknownOutcomes = (id: string, logGroup: logs.ILogGroup) =>
      new logs.MetricFilter(this, id, {
        logGroup,
        filterPattern: logs.FilterPattern.literal('"ESCROW_SETTLEMENT_FAILED" "outcome=unknown"'),
        metricNamespace,
        metricName: 'EscrowSettlementUnknownOutcomes',
        metricValue: '1',
        defaultValue: 0,
      });
    countUnknownOutcomes('EscrowUnknownOutcomesFilter', escrowLogGroup);
    countUnknownOutcomes('EscrowCronUnknownOutcomesFilter', escrowCronLogGroup);

    // ESC-03: a dispute verdict whose order or delivery cannot follow it is not settled.
    // EscrowService logs DISPUTE_VERDICT_ORDER_MISMATCH on every refused attempt: from
    // escrow-cron on the hourly dispute sweep, from the escrow Lambda on an admin release
    // or refund. Two filters publish one metric.
    const countVerdictMismatches = (id: string, logGroup: logs.ILogGroup) =>
      new logs.MetricFilter(this, id, {
        logGroup,
        filterPattern: logs.FilterPattern.literal('"DISPUTE_VERDICT_ORDER_MISMATCH"'),
        metricNamespace,
        metricName: 'DisputeVerdictOrderMismatch',
        metricValue: '1',
        defaultValue: 0,
      });
    countVerdictMismatches('EscrowVerdictMismatchFilter', escrowLogGroup);
    countVerdictMismatches('EscrowCronVerdictMismatchFilter', escrowCronLogGroup);

    // ESC-03: a dispute verdict whose settlement fails closed (the order or delivery could not
    // be read, or a buyer_wins order could not be cancelled or restocked first). The hourly
    // sweep retries; only a cause that persists matters, so the alarm (below) needs the line in
    // three of four hourly periods. escrow-cron only: an admin's own call already answers 503.
    new logs.MetricFilter(this, 'EscrowCronSettlementBlockedFilter', {
      logGroup: escrowCronLogGroup,
      filterPattern: logs.FilterPattern.literal('"DISPUTE_VERDICT_SETTLEMENT_BLOCKED"'),
      metricNamespace,
      metricName: 'DisputeVerdictSettlementBlocked',
      metricValue: '1',
      defaultValue: 0,
    });

    // ESC-13: queue age. The hourly escrow-cron run logs one line per pending manual
    // payout leg older than 24 hours (ESC-02, ReportOverdueManualPayoutsAsync) and one per
    // admin stuck-order queue entry due for 24 hours (ESC-06, ReportAdminQueueAsync; plus
    // a capped escrow whose order is missing).
    new logs.MetricFilter(this, 'ManualPayoutsOverdueFilter', {
      logGroup: escrowCronLogGroup,
      filterPattern: logs.FilterPattern.literal('"MANUAL_PAYOUT_OVERDUE"'),
      metricNamespace,
      metricName: 'ManualPayoutsOverdue',
      metricValue: '1',
      defaultValue: 0,
    });
    new logs.MetricFilter(this, 'AdminQueueOverdueFilter', {
      logGroup: escrowCronLogGroup,
      filterPattern: logs.FilterPattern.literal('"ADMIN_QUEUE_OVERDUE"'),
      metricNamespace,
      metricName: 'AdminQueueOverdue',
      metricValue: '1',
      defaultValue: 0,
    });
    // ESC-04: the hourly trucker retry logs one line per released escrow whose trucker leg is
    // still owed (and not queued) more than 24 hours after it went pending.
    new logs.MetricFilter(this, 'TruckerPayoutsOverdueFilter', {
      logGroup: escrowCronLogGroup,
      filterPattern: logs.FilterPattern.literal('"TRUCKER_PAYOUT_OVERDUE"'),
      metricNamespace,
      metricName: 'TruckerPayoutsOverdue',
      metricValue: '1',
      defaultValue: 0,
    });
    // ESC-09: the hourly order-money sweep logs one line when an order does not show what its
    // escrow's money says and it cannot fix it: write_failed (every run once the money moved
    // more than two hours ago), cannot_follow and order_ahead (once per escrow), sweep_truncated
    // (a cycle unfinished after 24 hours) and sweep_failed. escrow-cron only: the inline follow after each payout logs
    // ORDER_MONEY_FOLLOW_DEFERRED (not counted) and the sweep repairs it.
    new logs.MetricFilter(this, 'OrderMoneyMismatchFilter', {
      logGroup: escrowCronLogGroup,
      filterPattern: logs.FilterPattern.literal('"ORDER_MONEY_MISMATCH"'),
      metricNamespace,
      metricName: 'OrderMoneyMismatch',
      metricValue: '1',
      defaultValue: 0,
    });

    // ─── CloudWatch Dashboard ────────────────────────────────────────
    const dashboard = new cloudwatch.Dashboard(this, 'Dashboard', {
      dashboardName: `wereda-connect-${envConfig.name}`,
    });

    // Row 1: Header
    dashboard.addWidgets(
      new cloudwatch.TextWidget({
        markdown: [
          `# WeredaConnect ${envConfig.name} Dashboard`,
          '',
          `**API URL:** ${props.apiUrl}`,
          '',
          `**Region:** ${envConfig.region} | **Cost:** ~$7/month | **Updated:** ESC-13 money alarms`,
        ].join('\n'),
        width: 24,
        height: 3,
      }),
    );

    // ─── Panel 1: API Error Rate (4xx + 5xx from HTTP API Gateway) ──
    const api4xxMetric = new cloudwatch.Metric({
      namespace: 'AWS/ApiGateway',
      metricName: '4xx',
      dimensionsMap: { ApiId: props.apiId },
      statistic: 'Sum',
      period: cdk.Duration.minutes(5),
      label: '4xx Errors',
    });

    const api5xxMetric = new cloudwatch.Metric({
      namespace: 'AWS/ApiGateway',
      metricName: '5xx',
      dimensionsMap: { ApiId: props.apiId },
      statistic: 'Sum',
      period: cdk.Duration.minutes(5),
      label: '5xx Errors',
    });

    const apiRequestCountMetric = new cloudwatch.Metric({
      namespace: 'AWS/ApiGateway',
      metricName: 'Count',
      dimensionsMap: { ApiId: props.apiId },
      statistic: 'Sum',
      period: cdk.Duration.minutes(5),
      label: 'Total Requests',
    });

    dashboard.addWidgets(
      new cloudwatch.GraphWidget({
        title: 'API Error Rate (4xx & 5xx)',
        left: [api4xxMetric, api5xxMetric],
        right: [apiRequestCountMetric],
        width: 12,
        height: 6,
        leftYAxis: { label: 'Error Count', showUnits: false },
        rightYAxis: { label: 'Total Requests', showUnits: false },
      }),

      // ─── Panel 2: Lambda Errors ─────────────────────────────────────
      new cloudwatch.GraphWidget({
        title: 'Lambda Errors by Function',
        left: lambdaFunctions.map(
          (fn) =>
            new cloudwatch.Metric({
              namespace: 'AWS/Lambda',
              metricName: 'Errors',
              dimensionsMap: { FunctionName: `${prefix}-${fn.nameSuffix}` },
              statistic: 'Sum',
              period: cdk.Duration.minutes(5),
              label: fn.label,
            }),
        ),
        width: 12,
        height: 6,
        leftYAxis: { label: 'Error Count', showUnits: false },
      }),
    );

    // ─── Panel 3: Lambda Duration (p50, p90, p99) ────────────────────
    // Show latency for the most critical functions
    const criticalFunctions: LambdaFunctionDef[] = [
      { label: 'Auth Login', nameSuffix: 'auth-login' },
      { label: 'Sync', nameSuffix: 'sync' },
      { label: 'Escrow', nameSuffix: 'escrow' },
      { label: 'Crops', nameSuffix: 'crops' },
      { label: 'Trips', nameSuffix: 'trips' },
    ];

    const durationWidgets: cloudwatch.IWidget[] = [];
    for (const fn of criticalFunctions) {
      const functionName = `${prefix}-${fn.nameSuffix}`;
      durationWidgets.push(
        new cloudwatch.GraphWidget({
          title: `${fn.label} Latency`,
          left: [
            new cloudwatch.Metric({
              namespace: 'AWS/Lambda',
              metricName: 'Duration',
              dimensionsMap: { FunctionName: functionName },
              statistic: 'p50',
              period: cdk.Duration.minutes(5),
              label: 'p50',
            }),
            new cloudwatch.Metric({
              namespace: 'AWS/Lambda',
              metricName: 'Duration',
              dimensionsMap: { FunctionName: functionName },
              statistic: 'p90',
              period: cdk.Duration.minutes(5),
              label: 'p90',
            }),
            new cloudwatch.Metric({
              namespace: 'AWS/Lambda',
              metricName: 'Duration',
              dimensionsMap: { FunctionName: functionName },
              statistic: 'p99',
              period: cdk.Duration.minutes(5),
              label: 'p99',
            }),
          ],
          width: Math.floor(24 / criticalFunctions.length),
          height: 6,
          leftYAxis: { label: 'ms', showUnits: false },
        }),
      );
    }
    dashboard.addWidgets(...durationWidgets);

    // ─── Panel 4: DynamoDB Throttles ─────────────────────────────────
    const readThrottleMetrics = dynamoTables.map(
      (table) =>
        new cloudwatch.Metric({
          namespace: 'AWS/DynamoDB',
          metricName: 'ReadThrottleEvents',
          dimensionsMap: { TableName: `${prefix}-${table.nameSuffix}` },
          statistic: 'Sum',
          period: cdk.Duration.minutes(5),
          label: `${table.label} Read`,
        }),
    );

    const writeThrottleMetrics = dynamoTables.map(
      (table) =>
        new cloudwatch.Metric({
          namespace: 'AWS/DynamoDB',
          metricName: 'WriteThrottleEvents',
          dimensionsMap: { TableName: `${prefix}-${table.nameSuffix}` },
          statistic: 'Sum',
          period: cdk.Duration.minutes(5),
          label: `${table.label} Write`,
        }),
    );

    dashboard.addWidgets(
      new cloudwatch.GraphWidget({
        title: 'DynamoDB Read Throttles',
        left: readThrottleMetrics,
        width: 12,
        height: 6,
        leftYAxis: { label: 'Throttle Events', showUnits: false },
      }),
      new cloudwatch.GraphWidget({
        title: 'DynamoDB Write Throttles',
        left: writeThrottleMetrics,
        width: 12,
        height: 6,
        leftYAxis: { label: 'Throttle Events', showUnits: false },
      }),
    );

    // ─── Panel 5: Escrow Activity (from audit log metric filters) ────
    const escrowInitiateMetric = new cloudwatch.Metric({
      namespace: metricNamespace,
      metricName: 'EscrowInitiate',
      statistic: 'Sum',
      period: cdk.Duration.minutes(5),
      label: 'Initiate',
    });

    const escrowReleaseMetric = new cloudwatch.Metric({
      namespace: metricNamespace,
      metricName: 'EscrowRelease',
      statistic: 'Sum',
      period: cdk.Duration.minutes(5),
      label: 'Release',
    });

    const escrowDisputeMetric = new cloudwatch.Metric({
      namespace: metricNamespace,
      metricName: 'EscrowDispute',
      statistic: 'Sum',
      period: cdk.Duration.minutes(5),
      label: 'Dispute',
    });

    // ─── Panel 6: Auth Activity (from audit log metric filters) ──────
    const authLoginSuccessMetric = new cloudwatch.Metric({
      namespace: metricNamespace,
      metricName: 'AuthLoginSuccess',
      statistic: 'Sum',
      period: cdk.Duration.minutes(5),
      label: 'Login Success',
    });

    const authLoginFailedMetric = new cloudwatch.Metric({
      namespace: metricNamespace,
      metricName: 'AuthLoginFailed',
      statistic: 'Sum',
      period: cdk.Duration.minutes(5),
      label: 'Login Failed',
    });

    dashboard.addWidgets(
      new cloudwatch.GraphWidget({
        title: 'Escrow Activity (from Audit Logs)',
        left: [escrowInitiateMetric, escrowReleaseMetric, escrowDisputeMetric],
        width: 12,
        height: 6,
        leftYAxis: { label: 'Count', showUnits: false },
      }),
      new cloudwatch.GraphWidget({
        title: 'Auth Activity (from Audit Logs)',
        left: [authLoginSuccessMetric, authLoginFailedMetric],
        width: 12,
        height: 6,
        leftYAxis: { label: 'Count', showUnits: false },
      }),
    );

    // ─── Alarms ──────────────────────────────────────────────────────

    // Alarm 1: High API Gateway 5xx Error Rate (>5% of requests for 5 min)
    const api5xxAlarm = new cloudwatch.Alarm(this, 'HighApiErrorRate', {
      alarmName: `${prefix}-high-api-5xx-rate`,
      alarmDescription:
        'API Gateway 5xx errors exceed 5% of total requests for 5 minutes. ' +
        'Check Lambda errors and CloudWatch logs for root cause.',
      metric: new cloudwatch.MathExpression({
        expression: 'IF(requests > 0, errors / requests * 100, 0)',
        usingMetrics: {
          errors: new cloudwatch.Metric({
            namespace: 'AWS/ApiGateway',
            metricName: '5xx',
            dimensionsMap: { ApiId: props.apiId },
            statistic: 'Sum',
            period: cdk.Duration.minutes(5),
          }),
          requests: new cloudwatch.Metric({
            namespace: 'AWS/ApiGateway',
            metricName: 'Count',
            dimensionsMap: { ApiId: props.apiId },
            statistic: 'Sum',
            period: cdk.Duration.minutes(5),
          }),
        },
        period: cdk.Duration.minutes(5),
        label: '5xx Error Rate %',
      }),
      threshold: 5,
      evaluationPeriods: 1,
      comparisonOperator:
        cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
      treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
    });
    api5xxAlarm.addAlarmAction(snsAction);
    api5xxAlarm.addOkAction(snsAction);

    // Alarm 2: Lambda Errors — critical function errors > 3 in 5 minutes
    // CloudWatch math expressions limited to 10 metrics — monitor the 10 most critical
    // functions. escrow-cron is included because it auto-moves money on a schedule and
    // must never fail silently (Review: escrow-cron unmonitored).
    const alarmLambdaFunctions: LambdaFunctionDef[] = [
      { label: 'Auth Login', nameSuffix: 'auth-login' },
      { label: 'Auth Register', nameSuffix: 'auth-register' },
      { label: 'Auth Password Reset', nameSuffix: 'auth-password-reset' },
      { label: 'Sync', nameSuffix: 'sync' },
      { label: 'Escrow', nameSuffix: 'escrow' },
      { label: 'Escrow Cron', nameSuffix: 'escrow-cron' },
      { label: 'Crops', nameSuffix: 'crops' },
      { label: 'Trips', nameSuffix: 'trips' },
      { label: 'Orders', nameSuffix: 'orders' },
      { label: 'Deliveries', nameSuffix: 'deliveries' },
      // NOTE: order-cron is monitored by its own dedicated alarm (Alarm 7 below),
      // not this aggregate — a CloudWatch math-expression alarm caps at 10 metrics
      // and this list is already at 10.
    ];
    const lambdaErrorMetrics: Record<string, cloudwatch.IMetric> = {};
    const metricIds: string[] = [];
    for (let i = 0; i < alarmLambdaFunctions.length; i++) {
      const fn = alarmLambdaFunctions[i];
      const metricId = `e${i}`;
      metricIds.push(metricId);
      lambdaErrorMetrics[metricId] = new cloudwatch.Metric({
        namespace: 'AWS/Lambda',
        metricName: 'Errors',
        dimensionsMap: { FunctionName: `${prefix}-${fn.nameSuffix}` },
        statistic: 'Sum',
        period: cdk.Duration.minutes(5),
      });
    }

    const lambdaErrorsAlarm = new cloudwatch.Alarm(this, 'LambdaErrors', {
      alarmName: `${prefix}-lambda-errors`,
      alarmDescription:
        'Total Lambda errors across critical functions exceed 3 in 5 minutes. ' +
        'Check individual function logs.',
      metric: new cloudwatch.MathExpression({
        expression: metricIds.join(' + '),
        usingMetrics: lambdaErrorMetrics,
        period: cdk.Duration.minutes(5),
        label: 'Total Lambda Errors',
      }),
      threshold: 3,
      evaluationPeriods: 1,
      comparisonOperator:
        cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
      treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
    });
    lambdaErrorsAlarm.addAlarmAction(snsAction);
    lambdaErrorsAlarm.addOkAction(snsAction);

    // Alarm 3: DynamoDB Throttles — any table throttle events > 0 in 5 min
    // CloudWatch math expressions limited to 10 metrics. Monitor the 5 most
    // critical tables (read + write = 10 metrics at the limit).
    const alarmDynamoTables: DynamoTableDef[] = [
      { label: 'Users', nameSuffix: 'users' },
      { label: 'Crops', nameSuffix: 'crops' },
      { label: 'Trips', nameSuffix: 'trips' },
      { label: 'Orders', nameSuffix: 'orders' },
      { label: 'Escrows', nameSuffix: 'escrows' },
    ];
    const throttleMetrics: Record<string, cloudwatch.IMetric> = {};
    const throttleIds: string[] = [];
    for (let i = 0; i < alarmDynamoTables.length; i++) {
      const table = alarmDynamoTables[i];
      const readId = `r${i}`;
      const writeId = `w${i}`;
      throttleIds.push(readId, writeId);
      throttleMetrics[readId] = new cloudwatch.Metric({
        namespace: 'AWS/DynamoDB',
        metricName: 'ReadThrottleEvents',
        dimensionsMap: { TableName: `${prefix}-${table.nameSuffix}` },
        statistic: 'Sum',
        period: cdk.Duration.minutes(5),
      });
      throttleMetrics[writeId] = new cloudwatch.Metric({
        namespace: 'AWS/DynamoDB',
        metricName: 'WriteThrottleEvents',
        dimensionsMap: { TableName: `${prefix}-${table.nameSuffix}` },
        statistic: 'Sum',
        period: cdk.Duration.minutes(5),
      });
    }

    const dynamoThrottleAlarm = new cloudwatch.Alarm(
      this,
      'DynamoDBThrottles',
      {
        alarmName: `${prefix}-dynamodb-throttles`,
        alarmDescription:
          'DynamoDB throttle events detected on critical tables. On-demand billing ' +
          'should not throttle — investigate sudden traffic spikes or hot keys.',
        metric: new cloudwatch.MathExpression({
          expression: throttleIds.join(' + '),
          usingMetrics: throttleMetrics,
          period: cdk.Duration.minutes(5),
          label: 'Total Throttle Events',
        }),
        threshold: 0,
        evaluationPeriods: 1,
        comparisonOperator:
          cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
        treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
      },
    );
    dynamoThrottleAlarm.addAlarmAction(snsAction);
    dynamoThrottleAlarm.addOkAction(snsAction);

    // Alarm 4: Cold Start Duration — any Lambda p99 duration > 5 seconds
    // Monitor the sync and escrow functions (highest timeout risk)
    const coldStartFunctions = [
      { label: 'Sync', nameSuffix: 'sync' },
      { label: 'Escrow', nameSuffix: 'escrow' },
      { label: 'Auth Login', nameSuffix: 'auth-login' },
    ];

    const durationMetrics: Record<string, cloudwatch.IMetric> = {};
    const durationIds: string[] = [];
    for (let i = 0; i < coldStartFunctions.length; i++) {
      const fn = coldStartFunctions[i];
      const metricId = `d${i}`;
      durationIds.push(metricId);
      durationMetrics[metricId] = new cloudwatch.Metric({
        namespace: 'AWS/Lambda',
        metricName: 'Duration',
        dimensionsMap: { FunctionName: `${prefix}-${fn.nameSuffix}` },
        statistic: 'p99',
        period: cdk.Duration.minutes(5),
      });
    }

    const coldStartAlarm = new cloudwatch.Alarm(this, 'ColdStartDuration', {
      alarmName: `${prefix}-cold-start-duration`,
      alarmDescription:
        'Lambda p99 duration exceeds 5 seconds. Possible cold start issue. ' +
        'Check function memory, package size, and provisioned concurrency.',
      metric: new cloudwatch.MathExpression({
        expression: `MAX([${durationIds.join(',')}])`,
        usingMetrics: durationMetrics,
        period: cdk.Duration.minutes(5),
        label: 'Max p99 Duration (ms)',
      }),
      threshold: 5000,
      evaluationPeriods: 1,
      comparisonOperator:
        cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
      treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
    });
    coldStartAlarm.addAlarmAction(snsAction);
    coldStartAlarm.addOkAction(snsAction);

    // Alarm 5: Escrow Dispute Rate — more than 5 disputes in 1 hour
    const escrowDisputeAlarm = new cloudwatch.Alarm(
      this,
      'EscrowDisputeRate',
      {
        alarmName: `${prefix}-escrow-dispute-rate`,
        alarmDescription:
          'More than 5 escrow disputes in 1 hour. Possible fraud or ' +
          'systematic delivery issues. Investigate dispute patterns.',
        metric: new cloudwatch.Metric({
          namespace: metricNamespace,
          metricName: 'EscrowDispute',
          statistic: 'Sum',
          period: cdk.Duration.hours(1),
          label: 'Escrow Disputes',
        }),
        threshold: 5,
        evaluationPeriods: 1,
        comparisonOperator:
          cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
        treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
      },
    );
    escrowDisputeAlarm.addAlarmAction(snsAction);
    escrowDisputeAlarm.addOkAction(snsAction);

    // Alarm 6: Auth Brute Force — more than 10 LOGIN_FAILED in 5 minutes
    const authBruteForceAlarm = new cloudwatch.Alarm(
      this,
      'AuthBruteForce',
      {
        alarmName: `${prefix}-auth-brute-force`,
        alarmDescription:
          'More than 10 failed login attempts in 5 minutes. Possible brute ' +
          'force attack. Check rate limiter effectiveness and source IPs.',
        metric: new cloudwatch.Metric({
          namespace: metricNamespace,
          metricName: 'AuthLoginFailed',
          statistic: 'Sum',
          period: cdk.Duration.minutes(5),
          label: 'Failed Logins',
        }),
        threshold: 10,
        evaluationPeriods: 1,
        comparisonOperator:
          cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
        treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
      },
    );
    authBruteForceAlarm.addAlarmAction(snsAction);
    authBruteForceAlarm.addOkAction(snsAction);

    // Alarm: order-funded escrow problems. Any occurrence means a funded order's
    // money is not reflected, not linked or not scheduled for refund in the escrow
    // subsystem (replaces the ESC-01 create-failed alarm, ESC-13).
    const orderEscrowProblemsAlarm = new cloudwatch.Alarm(
      this,
      'OrderEscrowProblems',
      {
        alarmName: `${prefix}-order-escrow-problems`,
        alarmDescription:
          'The sync Lambda logged ORDER_ESCROW_CREATE_FAILED, ORDER_ESCROW_LINK_FAILED, ' +
          'ORDER_ESCROW_REFUND_SCHEDULE_FAILED or DISPUTE_NO_ESCROW (the line carries the orderId). CREATE: a ' +
          'funded order has no escrow record; the buyer\'s money is held but invisible to ' +
          '/escrow/history and refund scheduling; delivery-time linkage self-heals it, ' +
          'verify via the OrderIndex GSI. LINK: the delivery is not tied to the order ' +
          'escrow, so delivery confirmation may never reach it and the refund-safety check ' +
          'cannot see the delivery; link them by hand. REFUND_SCHEDULE: a rejected or ' +
          'cancelled order\'s money stays in escrow; set the escrow expiry by hand so the ' +
          'hourly sweep refunds the buyer. DISPUTE_NO_ESCROW (ESC-07): a delivery of the order ' +
          'was disputed but no escrow-order row holds its money, so the dispute was refused; ' +
          'find where the payment is held and contact the parties. ORDER_TRANSPORT_CANCEL_LATE_DELIVERY ' +
          'or _PICKUP_REFUSED (ESC-06): goods may have left the farm on an order cancelled for ' +
          'transport failure; call the parties.',
        metric: new cloudwatch.Metric({
          namespace: metricNamespace,
          metricName: 'OrderEscrowProblems',
          statistic: 'Sum',
          period: cdk.Duration.minutes(15),
          label: 'Order Escrow Problems',
        }),
        threshold: 0,
        evaluationPeriods: 1,
        comparisonOperator:
          cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
        treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
      },
    );
    orderEscrowProblemsAlarm.addAlarmAction(snsAction);
    orderEscrowProblemsAlarm.addOkAction(snsAction);

    // ESC-13 money alarms on the log tokens. 15 minute periods, 5 evaluation periods,
    // 1 datapoint to alarm. The producers run hourly, so the 75 minute window always holds
    // the previous run's line: a sweep that keeps failing or a payout that stays overdue
    // keeps the alarm in ALARM (one notification per episode, no hourly OK and ALARM flap).
    // It clears by itself 75 to 90 minutes after the last line. A synthetic line fires it
    // within about 15 minutes (scripts/money_alarms_synthetic.sh).
    const moneyLogAlarm = (
      id: string,
      nameSuffix: string,
      metricName: string,
      label: string,
      description: string,
    ): cloudwatch.Alarm => {
      const alarm = new cloudwatch.Alarm(this, id, {
        alarmName: `${prefix}-${nameSuffix}`,
        alarmDescription: description,
        metric: new cloudwatch.Metric({
          namespace: metricNamespace,
          metricName,
          statistic: 'Sum',
          period: cdk.Duration.minutes(15),
          label,
        }),
        threshold: 0,
        evaluationPeriods: 5,
        datapointsToAlarm: 1,
        comparisonOperator:
          cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
        treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
      });
      alarm.addAlarmAction(snsAction);
      alarm.addOkAction(snsAction);
      return alarm;
    };

    const escrowSettlementFailuresAlarm = moneyLogAlarm(
      'EscrowSettlementFailures',
      'escrow-settlement-failures',
      'EscrowSettlementFailures',
      'Escrow Settlement Failures',
      'A release, refund or trucker leg did not complete, or a money sweep hit an ' +
        'exception (ESCROW_SETTLEMENT_FAILED in the escrow or escrow-cron log group; the ' +
        'line carries escrowId, action, outcome and code; the escrow-audit row has the same ' +
        'action). outcome=unknown means Telebirr may already have paid: check the merchant ' +
        'portal before any manual release or refund, and the separate ' +
        'escrow-settlement-unknown-outcomes alarm fires too (ESC-05). outcome=failed: a definite ' +
        'refusal or a failure before the settlement lock; nothing moved and the next hourly sweep ' +
        'retries. trucker_release_failed with outcome=failed: the farmer is paid and the escrow ' +
        'stays released with truckerPayoutPending; the hourly trucker retry pays the leg with the ' +
        'same reference (ESC-04). Do not pay the trucker by hand; trucker-payouts-overdue fires if ' +
        'the leg is still owed after 24 hours. A payout waiting in the manual payout queue ' +
        '(202 ESCROW_034) is not counted here.',
    );

    // ESC-05: an unknown outcome is never retried by the server. The escrow keeps its settlement
    // lock and waits in the manual payout queue (GATEWAY_OUTCOME_UNKNOWN), at once or through the
    // hourly recovery step once the lock is 15 minutes old.
    const escrowUnknownOutcomesAlarm = moneyLogAlarm(
      'EscrowSettlementUnknownOutcomes',
      'escrow-settlement-unknown-outcomes',
      'EscrowSettlementUnknownOutcomes',
      'Escrow Settlement Unknown Outcomes',
      'A release, refund or trucker leg may have moved money without the escrow recording it ' +
        '(ESCROW_SETTLEMENT_FAILED with outcome=unknown in the escrow or escrow-cron log group: a ' +
        'Telebirr timeout or lost answer, a gateway call that threw, a queue write that failed ' +
        'after the gateway answered, an exception between the settlement lock and the settled ' +
        'status write, or a stale lock ' +
        'parked by the recovery step, action=stale_lock_parked). Nothing retries it. Do NOT ' +
        'release or refund it by hand. The escrow keeps its lock and is in Manual Payouts ' +
        '(/payouts) with reason GATEWAY_OUTCOME_UNKNOWN, or will be within about 75 minutes. Search ' +
        'the Telebirr merchant portal for the gateway reference the page shows (release:<escrowId>, ' +
        'release:<escrowId>:trucker or refund:<escrowId>); record the transaction id if you find ' +
        'it, pay and record only if it is missing (ESC-05).',
    );

    const manualPayoutsOverdueAlarm = moneyLogAlarm(
      'ManualPayoutsOverdue',
      'manual-payouts-overdue',
      'ManualPayoutsOverdue',
      'Manual Payouts Overdue',
      'A manual payout leg (a release or refund the live gateway could not pay out, or ' +
        'reported paid without a reference; ESC-02) has waited more than 24 hours ' +
        '(MANUAL_PAYOUT_OVERDUE in the escrow-cron log group, one line per pending leg per ' +
        'hourly run, with escrowId, ref=<op>:<leg> and ageHours). A farmer, trucker or buyer ' +
        'is still waiting for their money. Open Manual Payouts in the admin app (/payouts, ' +
        'oldest first). PAYOUT_NOT_SUPPORTED: pay the leg in the Telebirr merchant portal and ' +
        'record its transaction id. GATEWAY_REF_MISSING and GATEWAY_OUTCOME_UNKNOWN (ESC-05): ' +
        'find the payment in the portal first and pay only if it is missing. A leg refused ' +
        'with ESCROW_029 (unverified payee) ' +
        'cannot be recorded and keeps this alarm on until the payee verifies. While the alarm ' +
        'is in ALARM a newly overdue leg is not announced again: read the lines or the page.',
    );

    // ESC-04: a trucker leg the release could not pay is retried hourly, outside the manual
    // payout queue until the gateway cannot pay it; this alarm makes sure none waits silently.
    const truckerPayoutsOverdueAlarm = moneyLogAlarm(
      'TruckerPayoutsOverdue',
      'trucker-payouts-overdue',
      'TruckerPayoutsOverdue',
      'Trucker Payouts Overdue',
      'A trucker leg is still owed more than 24 hours after the farmer was paid ' +
        '(TRUCKER_PAYOUT_OVERDUE in the escrow-cron log group, one line per escrow per hourly ' +
        'run, with escrowId, reason and ageHours; the escrow stays released and the hourly ' +
        'trucker retry keeps trying, ESC-04). reason=phone_unverified or fayda_required: ask the ' +
        'trucker to verify; the next hourly run pays the leg (with the live gateway it moves to ' +
        'Manual Payouts). reason=no_trucker, profile_missing or trucker_mismatch: the escrow has ' +
        'no payable trucker. The hourly run copies the truckerId from the order when the delivery ' +
        'agrees; if the order has none the leg has no payee until ESC-20. Escalate, and never ' +
        'edit the escrow row by hand. reason=gateway_refused or queue_failed: see ' +
        'escrow-settlement-failures. A leg in Manual Payouts is reported by manual-payouts-overdue instead.',
    );

    const disputeVerdictMismatchAlarm = moneyLogAlarm(
      'DisputeVerdictOrderMismatch',
      'dispute-verdict-order-mismatch',
      'DisputeVerdictOrderMismatch',
      'Dispute Verdict Order Mismatch',
      'A dispute verdict was not settled because its order or delivery is in a status the ' +
        'verdict cannot follow (DISPUTE_VERDICT_ORDER_MISMATCH in the escrow or escrow-cron log ' +
        'group, with escrowId, op and legs; escrow-audit row dispute_verdict_mismatch, once a ' +
        'day). Usually a dispute raised before delivery: the order is still accepted, ' +
        'pickup_confirmed or in_transit. No money moved; the escrow stays dispute_resolved and ' +
        'the hourly sweep tries again. Bring the order (and its delivery) to a status the ' +
        'verdict can follow, or settle the case by hand, then the next sweep settles it (ESC-03).',
    );

    const orderMoneyMismatchAlarm = moneyLogAlarm(
      'OrderMoneyMismatch',
      'order-money-mismatch',
      'OrderMoneyMismatch',
      'Order Money Mismatch',
      'An order does not show what its escrow\'s money says and the hourly order-money sweep ' +
        'could not fix it (ORDER_MONEY_MISMATCH in the escrow-cron log group: orderId, escrowId, ' +
        'orderStatus, escrowStatus, reason; ESC-09). write_failed: the order could follow but its ' +
        'writes keep failing (throttling, IAM); fix the cause, the next run retries. cannot_follow: ' +
        'money moved that the order cannot show (a refund behind a delivered order, a payout before ' +
        'delivery). order_ahead: the order is disbursing or settled over money that did not move. ' +
        'Both are logged once per escrow (escrow-audit order_money_mismatch or order_money_ahead, ' +
        'and a row on the order timeline): read them, never edit rows by hand, escalate (FUP-16). ' +
        'sweep_truncated: a sweep cycle, resumed from a cursor, is unfinished 24 hours after it ' +
        'started; check escrow-cron duration and throttles. sweep_failed: a page query failed.',
    );

    // Persistence alarm: one blocked run is a transient retry, a block in three of four hourly
    // periods is a stuck refund or payout. Three of four, not three of three: EscrowCronHourly is
    // rate(1 hour), so jitter near the top of the hour can leave one clock-hour with no line and
    // a 3-of-3 streak would never complete. Missing hours (the escrow left the queue) do not breach.
    const disputeSettlementBlockedAlarm = new cloudwatch.Alarm(this, 'DisputeVerdictSettlementBlocked', {
      alarmName: `${prefix}-dispute-verdict-settlement-blocked`,
      alarmDescription:
        'A dispute verdict could not be settled in three of the last four hourly periods because its ' +
        'settlement failed closed (DISPUTE_VERDICT_SETTLEMENT_BLOCKED in the escrow-cron log group, ' +
        'with escrowId, op and legs; escrow-audit row dispute_settlement_blocked, once). legs ' +
        'read:<error>: the order or delivery cannot be read (permissions, throttling, a missing ' +
        'table). cancel:<reason> or restock:<reason>: a buyer_wins order could not be cancelled, ' +
        'or its listing stock given back, before the refund. No money moved; the buyer or farmer ' +
        'is still waiting. Fix the cause (IAM, the order or listing row), then the next sweep ' +
        'settles it (ESC-03).',
      metric: new cloudwatch.Metric({
        namespace: metricNamespace,
        metricName: 'DisputeVerdictSettlementBlocked',
        statistic: 'Sum',
        period: cdk.Duration.hours(1),
        label: 'Dispute Verdict Settlement Blocked',
      }),
      threshold: 0,
      evaluationPeriods: 4,
      datapointsToAlarm: 3,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
      treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
    });
    disputeSettlementBlockedAlarm.addAlarmAction(snsAction);
    disputeSettlementBlockedAlarm.addOkAction(snsAction);

    const adminQueueOverdueAlarm = moneyLogAlarm(
      'AdminQueueOverdue',
      'admin-queue-overdue',
      'AdminQueueOverdue',
      'Admin Queue Overdue',
      'An order escrow held 21 days after funding (ESC-06) has been due in the admin ' +
        'stuck-order queue for 24 hours (ADMIN_QUEUE_OVERDUE in the escrow-cron log group, ' +
        'one line per entry per hourly run, with orderId, reason and ageHours). Nothing moves ' +
        'the money by itself. Open /transactions/<orderId> in the admin app. reason=hold_cap: ' +
        'order accepted past its pickup deadline: the buyer can cancel in the app for a full ' +
        'refund, ask them to; goods moving: the buyer or farmer opens a dispute and the verdict ' +
        'settles it. Never refund an accepted order by hand. reason=order_missing: escalate. ' +
        'Runbook: .ai/deployment.md, Monitoring.',
    );

    // ESC-01: live order payments. The Telebirr webhook (and its log group) only
    // exists when the live gateway is on, same flag as api-stack. Any payment the
    // webhook could not apply is money the platform holds for a manual refund, and
    // the webhook always answers 200, so its Lambda Errors metric never shows it.
    const telebirrLive =
      (envConfig.enableTelebirrLive ?? false) ||
      this.node.tryGetContext('enableTelebirrLive') === 'true';
    let orderPaymentProblemsAlarm: cloudwatch.Alarm | undefined;
    if (telebirrLive) {
      const webhookLogGroup = logs.LogGroup.fromLogGroupName(
        this,
        'TelebirrWebhookLogGroup',
        `/aws/lambda/${prefix}-telebirr-webhook`,
      );
      new logs.MetricFilter(this, 'OrderPaymentProblemsFilter', {
        logGroup: webhookLogGroup,
        filterPattern: logs.FilterPattern.anyTerm(
          'ORDER_PAYMENT_UNMATCHED',
          'ORDER_PAYMENT_DUPLICATE',
          'ORDER_PAYMENT_UNPROCESSED',
          'ORDER_ESCROW_CREATE_FAILED',
          'ORDER_STOCK_DECREMENT_FAILED',
        ),
        metricNamespace,
        metricName: 'OrderPaymentProblems',
        metricValue: '1',
        defaultValue: 0,
      });

      orderPaymentProblemsAlarm = new cloudwatch.Alarm(
        this,
        'OrderPaymentProblems',
        {
          alarmName: `${prefix}-order-payment-problems`,
          alarmDescription:
            'The Telebirr webhook received an order payment it could not apply ' +
            '(ORDER_PAYMENT_UNMATCHED, _DUPLICATE or _UNPROCESSED), or funded an ' +
            'order whose stock or escrow step failed (ORDER_STOCK_DECREMENT_FAILED, ' +
            'ORDER_ESCROW_CREATE_FAILED: repair these, never refund). Find the ' +
            'payment in this log group (an _UNPROCESSED payment may be recorded only ' +
            'here), the escrow audit (order_payment_unmatched, _duplicate, ' +
            '_unprocessed) or the webhook-inbox rows of the escrow idempotency table. ' +
            'Before refunding, read the order: if its telebirrPaymentOrderId is this ' +
            'payment, the payment funded the order and must not be refunded. Refund ' +
            'an _UNPROCESSED payment only once its order has left that payment attempt. ' +
            'ESC-45: a payment recorded in the inbox is replayed every 5 minutes and pages ' +
            'only as reason=replay_exhausted (the row is then in the exhausted partition of ' +
            'InboxReplayIndex; fix the cause and requeue it, or refund it, per .ai/deployment.md).',
          metric: new cloudwatch.Metric({
            namespace: metricNamespace,
            metricName: 'OrderPaymentProblems',
            statistic: 'Sum',
            period: cdk.Duration.minutes(15),
            label: 'Order Payment Problems',
          }),
          threshold: 0,
          evaluationPeriods: 1,
          comparisonOperator:
            cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
          treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
        },
      );
      orderPaymentProblemsAlarm.addAlarmAction(snsAction);
      orderPaymentProblemsAlarm.addOkAction(snsAction);

      // ESC-45: the inbox replay sweep logs one TELEBIRR_INBOX_REPLAY_RUN line per run
      // (every 5 minutes, none when its index query failed). No line for 30 minutes means
      // pending payments are not being replayed.
      new logs.MetricFilter(this, 'TelebirrInboxReplayRunsFilter', {
        logGroup: webhookLogGroup,
        filterPattern: logs.FilterPattern.literal('"TELEBIRR_INBOX_REPLAY_RUN"'),
        metricNamespace,
        metricName: 'TelebirrInboxReplayRuns',
        metricValue: '1',
        defaultValue: 0,
      });
      const inboxReplayStalledAlarm = new cloudwatch.Alarm(
        this,
        'TelebirrInboxReplayStalled',
        {
          alarmName: `${prefix}-telebirr-inbox-replay-stalled`,
          alarmDescription:
            'The Telebirr webhook inbox replay sweep (EventBridge every 5 minutes) logged no ' +
            'TELEBIRR_INBOX_REPLAY_RUN line for 30 minutes: the rule is disabled, the Lambda ' +
            'fails, or the InboxReplayIndex query fails. Verified order payments that failed ' +
            'once are not being applied. Check the webhook log group and the rule.',
          metric: new cloudwatch.Metric({
            namespace: metricNamespace,
            metricName: 'TelebirrInboxReplayRuns',
            statistic: 'Sum',
            period: cdk.Duration.minutes(10),
            label: 'Telebirr Inbox Replay Runs',
          }),
          threshold: 1,
          evaluationPeriods: 3,
          comparisonOperator: cloudwatch.ComparisonOperator.LESS_THAN_THRESHOLD,
          treatMissingData: cloudwatch.TreatMissingData.BREACHING,
        },
      );
      inboxReplayStalledAlarm.addAlarmAction(snsAction);
      inboxReplayStalledAlarm.addOkAction(snsAction);
    }

    // Alarm 7: Order Cron Errors — the scheduled unpaid-order auto-cancel must never
    // fail silently (escrow-cron has the same dedicated alarm right below, ESC-13).
    // It gets a dedicated alarm rather than joining the aggregate LambdaErrors alarm,
    // which is capped at 10 metrics.
    const orderCronErrorsAlarm = new cloudwatch.Alarm(
      this,
      'OrderCronErrors',
      {
        alarmName: `${prefix}-order-cron-errors`,
        alarmDescription:
          'The scheduled order auto-cancel (order-cron) reported errors. Unpaid ' +
          'orders may not be expiring; investigate the Lambda logs.',
        metric: new cloudwatch.Metric({
          namespace: 'AWS/Lambda',
          metricName: 'Errors',
          dimensionsMap: { FunctionName: `${prefix}-order-cron` },
          statistic: 'Sum',
          period: cdk.Duration.minutes(5),
          label: 'Order Cron Errors',
        }),
        threshold: 1,
        evaluationPeriods: 1,
        comparisonOperator:
          cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
        treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
      },
    );
    orderCronErrorsAlarm.addAlarmAction(snsAction);
    orderCronErrorsAlarm.addOkAction(snsAction);

    // ESC-13: escrow-cron killed or crashed. The handler's top-level catch answers 500
    // and logs a settlement token, so ordinary exceptions never reach this metric; only
    // the 5 minute timeout, an out-of-memory kill, a runtime crash or an event the typed
    // serializer cannot read does. A run killed after a gateway call logs nothing, so this
    // alarm is the only signal for it. The aggregate LambdaErrors alarm (> 3 in 5 minutes)
    // never sees a single crash, and the scheduled retries land in separate periods.
    // 4 periods of 5 minutes, 1 datapoint: a timeout kill and its two async retries (each up
    // to 5 minutes, 1 and 2 minutes apart) leave empty periods between errors, and a
    // 20 minute window keeps them one ALARM episode instead of flapping. It clears by itself
    // 20 to 25 minutes after the last error.
    const escrowCronErrorsAlarm = new cloudwatch.Alarm(this, 'EscrowCronErrors', {
      alarmName: `${prefix}-escrow-cron-errors`,
      alarmDescription:
        'escrow-cron (hourly auto-release, auto-refund and dispute settlement; 6-hourly ' +
        'reconciliation) was killed or crashed: 5 minute timeout, out of memory or runtime ' +
        'crash. A run killed after a Telebirr call logs no ESCROW_SETTLEMENT_FAILED line and ' +
        'the money may have moved. Find the failed RequestId in the escrow-cron log group ' +
        '(REPORT line, "Task timed out"), the last escrowId it logged and any escrow still ' +
        'holding its settlement lock; check the escrow-audit rows and the Telebirr merchant ' +
        'portal before any manual release or refund. The hourly recovery step (ESC-05) parks an ' +
        'escrow whose lock is older than 15 minutes in Manual Payouts as GATEWAY_OUTCOME_UNKNOWN ' +
        'and logs stale_lock_parked. Lambda retries a failed scheduled run twice, so one ' +
        'crash can show up to ' +
        'three errors; they stay one episode, and the alarm clears 20 to 25 minutes after the ' +
        'last one.',
      metric: new cloudwatch.Metric({
        namespace: 'AWS/Lambda',
        metricName: 'Errors',
        dimensionsMap: { FunctionName: `${prefix}-escrow-cron` },
        statistic: 'Sum',
        period: cdk.Duration.minutes(5),
        label: 'Escrow Cron Errors',
      }),
      threshold: 1,
      evaluationPeriods: 4,
      datapointsToAlarm: 1,
      comparisonOperator:
        cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
      treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
    });
    escrowCronErrorsAlarm.addAlarmAction(snsAction);
    escrowCronErrorsAlarm.addOkAction(snsAction);

    // ─── Alarm Status Widget (summary at bottom of dashboard) ────────
    dashboard.addWidgets(
      new cloudwatch.AlarmStatusWidget({
        title: 'Alarm Status',
        alarms: [
          api5xxAlarm,
          lambdaErrorsAlarm,
          dynamoThrottleAlarm,
          coldStartAlarm,
          escrowDisputeAlarm,
          authBruteForceAlarm,
          orderCronErrorsAlarm,
          // ESC-13 money alarms
          escrowSettlementFailuresAlarm,
          escrowUnknownOutcomesAlarm,
          escrowCronErrorsAlarm,
          orderEscrowProblemsAlarm,
          manualPayoutsOverdueAlarm,
          truckerPayoutsOverdueAlarm,
          adminQueueOverdueAlarm,
          disputeVerdictMismatchAlarm,
          disputeSettlementBlockedAlarm,
          orderMoneyMismatchAlarm,
          ...(orderPaymentProblemsAlarm ? [orderPaymentProblemsAlarm] : []),
        ],
        width: 24,
        height: 3,
      }),
    );

    // ─── Outputs ─────────────────────────────────────────────────────
    new cdk.CfnOutput(this, 'DashboardUrl', {
      value: `https://${this.region}.console.aws.amazon.com/cloudwatch/home?region=${this.region}#dashboards:name=wereda-connect-${envConfig.name}`,
      description: 'CloudWatch Dashboard URL',
    });

    new cdk.CfnOutput(this, 'AlarmTopicArn', {
      value: alarmTopic.topicArn,
      description:
        'SNS topic ARN for alarm notifications — subscribe your email/phone',
    });

    new cdk.CfnOutput(this, 'MetricNamespace', {
      value: metricNamespace,
      description: 'Custom CloudWatch metric namespace for audit log metrics',
    });
  }
}
