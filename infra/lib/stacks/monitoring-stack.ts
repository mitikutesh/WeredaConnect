import * as cdk from 'aws-cdk-lib';
import { Construct } from 'constructs';
import * as cloudwatch from 'aws-cdk-lib/aws-cloudwatch';
import * as cloudwatch_actions from 'aws-cdk-lib/aws-cloudwatch-actions';
import * as logs from 'aws-cdk-lib/aws-logs';
import * as sns from 'aws-cdk-lib/aws-sns';
import { EnvironmentConfig } from '../config/environments';
import { TELEBIRR_WEBHOOK_PATH } from './api-stack';

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
 * Cost per environment and month (FUP-81: counted from the synthesized template on 2026-11-07,
 * us-east-1 list prices, before the free tier): about $14.50 with the live gateway off and $15.80
 * with it on, plus usage. Fixed: the dashboard $3; the alarms at $0.10 per metric they read, 56
 * metrics over 32 alarms (63 over 37 live: the Lambda errors and DynamoDB throttle alarms read 10
 * each, the anomaly alarm 3, the API 5xx alarm 2, or 4 live); the composite alarm $0.50; and $0.30
 * for each metric a log filter publishes with a default value, 18 (20 live), because a default
 * publishes in every hour its log group logs anything. Usage: a filter metric without a default
 * (the risk lines, OutcomeLabelFailed) is billed only in the hours a line is written, and so are
 * FRD-16's per-type signal metrics ($6.30 at most for the 21 types of today; fraud spec section
 * 23.1) and the live webhook route's 6 detailed API Gateway metrics (at most $1.80; FUP-39).
 * Metric filters are free. Recount when an alarm, or a filter with a default, is added.
 * ESC-28 (not in the count above): 10 reputation alarms, one metric each ($1.00), and 6 log filter
 * metrics without a default, billed only in the hours a line is written.
 * FUP-158, FUP-201 and FUP-231 (not in the count above either): 3 alarms, one metric each ($0.30),
 * and 8 log filter metrics without a default (LabelSnapshotFailed, ReputationFraudHookFailed,
 * OrderDeadlineRefundStalled and the 5 FraudAttributedSkipped reasons), billed only in the hours a
 * line is written.
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
      { label: 'Crop Photo Processor', nameSuffix: 'crop-photo-processor' },
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

    // ESCROW: Dispute. FUP-81: only outcome "success", a dispute that froze the escrow, as the
    // sync filter below counts only fresh freezes. A refused dispute (403 step-up, 409 already
    // disputed, 503) logs "failure" or "error" and is not a new dispute. A replay of the same
    // idempotency key also logs "success" and still counts (rare: a retry after a lost answer).
    new logs.MetricFilter(this, 'EscrowDisputeFilter', {
      logGroup: escrowLogGroup,
      filterPattern: logs.FilterPattern.all(
        logs.FilterPattern.stringValue('$.category', '=', 'ESCROW'),
        logs.FilterPattern.stringValue('$.action', '=', 'DISPUTE'),
        logs.FilterPattern.stringValue('$.outcome', '=', 'success'),
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
    //     cancelled the delivery (the reverse offline race): the trucker has the goods. ESC-34 writes
    //     it too (reason=late_pickup_evidence) for a real QR scan refused after the pickup was judged;
    //   QR_GATE_UNDECIDED (FUP-42): a delivery pickup or confirm with no QR on record could not
    //     read the escrow that decides whether it needs one. It stays deferred and never fails,
    //     so a read that keeps failing would hold the delivery unseen.
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
        'QR_GATE_UNDECIDED',
      ),
      metricNamespace,
      metricName: 'OrderEscrowProblems',
      metricValue: '1',
      defaultValue: 0,
    });

    // ESC-15: the order cron's commitment walk schedules refunds too (a farmer who never
    // answered, a buyer who never chose) and writes the same ORDER_ESCROW_REFUND_SCHEDULE_FAILED
    // line when it cannot; ORDER_CRON_COMMITMENTS_UNCONFIGURED means a dependency table is
    // missing from its env, so no deadline is settled and funded orders wait unseen. Same
    // metric, so the alarm above adds both sources. The api-stack Lambda construct creates the
    // log group explicitly, so a filter on it is safe on a fresh deploy.
    const orderCronLogGroup = logs.LogGroup.fromLogGroupName(
      this,
      'OrderCronLogGroup',
      `/aws/lambda/${prefix}-order-cron`,
    );
    new logs.MetricFilter(this, 'OrderCronEscrowProblemsFilter', {
      logGroup: orderCronLogGroup,
      filterPattern: logs.FilterPattern.anyTerm(
        'ORDER_ESCROW_REFUND_SCHEDULE_FAILED',
        'ORDER_CRON_COMMITMENTS_UNCONFIGURED',
      ),
      metricNamespace,
      metricName: 'OrderEscrowProblems',
      metricValue: '1',
      defaultValue: 0,
    });

    // FUP-27: a delivery dispute through sync that froze its escrow counts toward the escrow
    // dispute rate too (before, only POST /escrow/dispute did, through the JSON filter above).
    // The sync Lambda logs DELIVERY_DISPUTE_FROZEN once per fresh freeze; an escrow already
    // disputed and a refused dispute log nothing. Same metric, so the alarm adds both sources.
    new logs.MetricFilter(this, 'SyncDeliveryDisputeFilter', {
      logGroup: syncLogGroup,
      filterPattern: logs.FilterPattern.literal('"DELIVERY_DISPUTE_FROZEN"'),
      metricNamespace,
      metricName: 'EscrowDispute',
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

    // FUP-27: the delivery or order behind a frozen dispute did not follow it. EscrowService's
    // dispute mirror logs DISPUTE_PROPAGATION_FAILED once per call with a failed leg, from every
    // Lambda that runs the mirror: escrow (POST /escrow/dispute and its repeat), sync (a delivery
    // dispute) and escrow-cron (a sweep that froze a disputed trade). Admin and telebirr-webhook
    // never reach it. Three filters publish one metric.
    const countPropagationFailures = (id: string, logGroup: logs.ILogGroup) =>
      new logs.MetricFilter(this, id, {
        logGroup,
        filterPattern: logs.FilterPattern.literal('"DISPUTE_PROPAGATION_FAILED"'),
        metricNamespace,
        metricName: 'DisputePropagationFailed',
        metricValue: '1',
        defaultValue: 0,
      });
    countPropagationFailures('EscrowDisputePropagationFilter', escrowLogGroup);
    countPropagationFailures('SyncDisputePropagationFilter', syncLogGroup);
    countPropagationFailures('EscrowCronDisputePropagationFilter', escrowCronLogGroup);

    // ESC-16: a confirmed delivery whose escrow could not be stamped deliveryConfirmedAt. The sync
    // logs ESCROW_DELIVERY_STAMP_FAILED when its inline stamp at the buyer's confirmation fails; the
    // escrow cron logs it when its own repair of that stamp fails (the refund sweep's safety net or the
    // order-money sweep). Two filters publish one metric.
    const countDeliveryStampFailures = (id: string, logGroup: logs.ILogGroup) =>
      new logs.MetricFilter(this, id, {
        logGroup,
        filterPattern: logs.FilterPattern.literal('"ESCROW_DELIVERY_STAMP_FAILED"'),
        metricNamespace,
        metricName: 'EscrowDeliveryStampFailures',
        metricValue: '1',
        defaultValue: 0,
      });
    countDeliveryStampFailures('SyncDeliveryStampFailuresFilter', syncLogGroup);
    countDeliveryStampFailures('EscrowCronDeliveryStampFailuresFilter', escrowCronLogGroup);

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
    // ESC-20: the 15 minute ledger sweep logs one LEDGER_ROW_OVERDUE line per ledger row open (pending,
    // sent or failed) for more than 24 hours, every run. escrow-cron only: only the sweep reports age.
    new logs.MetricFilter(this, 'LedgerRowsOverdueFilter', {
      logGroup: escrowCronLogGroup,
      filterPattern: logs.FilterPattern.literal('"LEDGER_ROW_OVERDUE"'),
      metricNamespace,
      metricName: 'LedgerRowsOverdue',
      metricValue: '1',
      defaultValue: 0,
    });
    // ESC-20: a ledger write that would break I1 to I4 was not sent (escrow: a release or refund; escrow-cron:
    // the sweeps, a split verdict, the ledger sweep's re-check), or a row an admin recorded by hand left the
    // escrow failing a check (admin: the manual payout queue). Any line alarms. Three filters, one metric.
    const countLedgerInvariant = (id: string, logGroup: logs.ILogGroup) =>
      new logs.MetricFilter(this, id, {
        logGroup,
        filterPattern: logs.FilterPattern.literal('"ESCROW_LEDGER_INVARIANT"'),
        metricNamespace,
        metricName: 'EscrowLedgerInvariant',
        metricValue: '1',
        defaultValue: 0,
      });
    countLedgerInvariant('EscrowLedgerInvariantFilter', escrowLogGroup);
    countLedgerInvariant('EscrowCronLedgerInvariantFilter', escrowCronLogGroup);
    // The admin Lambda's group, created by the api-stack Lambda construct like the others.
    const adminLogGroup = logs.LogGroup.fromLogGroupName(this, 'AdminLogGroup', `/aws/lambda/${prefix}-admin`);
    countLedgerInvariant('AdminLedgerInvariantFilter', adminLogGroup);

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
    // FRD-03: a settlement sweep left an escrow unsettled because of the fraud risk gate, one
    // line per escrow per hourly run: a suspended party (FRD-04: or one at risk tier CRITICAL)
    // while FRAUD_HOLD_ENABLED is off, or a party row or risk score it could not read. Counted
    // from the FRD-03 deploy, so no settlement waits unseen. FUP-29 (escrow-protocol 2.6.0): the
    // payout gate logs the same token (sweep=payout-farmer or payout-trucker) for each leg it leaves
    // unpaid, on every run that asks; escrow-cron is still the only Lambda that pays as the system.
    new logs.MetricFilter(this, 'RiskSettlementSkippedFilter', {
      logGroup: escrowCronLogGroup,
      filterPattern: logs.FilterPattern.literal('"RISK_SETTLEMENT_SKIPPED"'),
      metricNamespace,
      metricName: 'RiskSettlementSkipped',
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
          `**Region:** ${envConfig.region} | **Cost:** about $15/month plus usage (the MonitoringStack header in infra/lib/stacks/monitoring-stack.ts)`,
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

    // The live Telebirr gateway: the webhook route, its Lambda and its log group exist only
    // with this flag (same flag as api-stack).
    const telebirrLive =
      (envConfig.enableTelebirrLive ?? false) ||
      this.node.tryGetContext('enableTelebirrLive') === 'true';

    // The webhook route's own API Gateway metrics. They exist because api-stack enables
    // detailed metrics on that one route (FUP-39).
    const webhookRouteMetric = (metricName: string, period: cdk.Duration, label?: string) =>
      new cloudwatch.Metric({
        namespace: 'AWS/ApiGateway',
        metricName,
        dimensionsMap: {
          ApiId: props.apiId,
          Method: 'POST',
          Resource: TELEBIRR_WEBHOOK_PATH,
          Stage: '$default',
        },
        statistic: 'Sum',
        period,
        label,
      });

    // Alarm 1: High API Gateway 5xx Error Rate (>5% of requests for 5 min)
    // FUP-39: the webhook answers 500 on purpose when a verified payment is not yet recorded
    // or applied, so Telebirr sends it again (ESC-45 D1). At low traffic one such answer is
    // over 5 % of all requests, so with the live gateway the rate leaves that route out; the
    // webhook alarms below watch it. Metric math counts a missing datapoint as 0: with no
    // webhook traffic the rate is the plain API rate, and a period with only webhook
    // traffic gives 0.
    const api5xxMetrics: Record<string, cloudwatch.IMetric> = {
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
    };
    let api5xxExpression = 'IF(requests > 0, errors / requests * 100, 0)';
    if (telebirrLive) {
      api5xxMetrics.webhookErrors = webhookRouteMetric('5xx', cdk.Duration.minutes(5));
      api5xxMetrics.webhookRequests = webhookRouteMetric('Count', cdk.Duration.minutes(5));
      api5xxExpression =
        'IF(requests - webhookRequests > 0, ' +
        '(errors - webhookErrors) / (requests - webhookRequests) * 100, 0)';
    }
    const api5xxAlarm = new cloudwatch.Alarm(this, 'HighApiErrorRate', {
      alarmName: `${prefix}-high-api-5xx-rate`,
      alarmDescription:
        'API Gateway 5xx errors exceed 5% of total requests for 5 minutes. ' +
        'Check Lambda errors and CloudWatch logs for root cause.' +
        (telebirrLive
          ? ` The Telebirr webhook route (POST ${TELEBIRR_WEBHOOK_PATH}) is left out: it answers ` +
            '500 on purpose for retryable failures; the telebirr-webhook-5xx, -errors and ' +
            '-throttles alarms watch it (FUP-39).'
          : ''),
      metric: new cloudwatch.MathExpression({
        expression: api5xxExpression,
        usingMetrics: api5xxMetrics,
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
          'systematic delivery issues. Investigate dispute patterns. Counts disputes that froze an ' +
          'escrow: POST /escrow/dispute (the escrow Lambda\'s ESCROW/DISPUTE audit line with outcome ' +
          'success, since FUP-81; refused requests are not counted) and, since FUP-27, delivery ' +
          'disputes through sync (DELIVERY_DISPUTE_FROZEN in the sync log group, with escrowId, ' +
          'orderId and deliveryId).',
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
          // At most 1024 characters (an infra test checks it); the runbook has the detail.
          'Sync, order-cron or Telebirr webhook logged one of these (runbook: .ai/deployment.md). ' +
          'ORDER_ESCROW_CREATE_FAILED: a funded order has no escrow record; its money is invisible to ' +
          '/escrow/history and refunds; delivery linkage self-heals it (check the OrderIndex GSI). ' +
          '_LINK_FAILED: the delivery is not tied to the order escrow, so confirmation and the refund ' +
          'check miss it; link them by hand. _REFUND_SCHEDULE_FAILED: a cancelled order\'s money stays ' +
          'in escrow; expire it by hand (reason=no_escrow: no record, refund by hand). ' +
          'DISPUTE_NO_ESCROW (ESC-07): a delivery was disputed but no escrow-order row holds the money, ' +
          'so it was refused; find the payment, call the parties. ORDER_TRANSPORT_CANCEL_LATE_DELIVERY ' +
          'or _PICKUP_REFUSED (ESC-06): goods may have left the farm on an order cancelled for transport ' +
          'failure; call the parties. QR_GATE_UNDECIDED (FUP-42): a no-QR pickup or confirm could not ' +
          'read its escrow; fix the read. ORDER_CRON_COMMITMENTS_UNCONFIGURED ' +
          '(ESC-15): order-cron lacks a table env; no deadline settles.',
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

    // ESC-20: ledger rows that stay open. A row the gateway refused is retried (3 times, then the manual
    // queue), a row whose outcome is unknown is checked with the gateway first, so a row open for a day
    // means something keeps it from both.
    const ledgerRowsOverdueAlarm = moneyLogAlarm(
      'LedgerRowsOverdue',
      'ledger-rows-overdue',
      'LedgerRowsOverdue',
      'Ledger Rows Overdue',
      'An escrow ledger row (one money movement, ESC-20) has been open more than 24 hours ' +
        '(LEDGER_ROW_OVERDUE in the escrow-cron log group, every 15 minute ledger sweep, with escrowId, ' +
        'ref (# written as .), status and ageHours). status=pending: the payee fails the identity gate ' +
        '(the row waits, lastCode PAYEE_BLOCKED) or the sweep cannot call; ask the payee to verify. ' +
        'status=sent: the gateway outcome is unknown and its status check keeps failing; do NOT pay by ' +
        'hand, search the merchant portal for the row gatewayRef first. status=failed: the gateway keeps ' +
        'refusing; after 3 refusals the row moves to Manual Payouts. Rows in Manual Payouts are reported ' +
        'by manual-payouts-overdue (ref=<ledger ref>). Never edit a ledger row or the escrow by hand.',
    );

    const ledgerInvariantAlarm = moneyLogAlarm(
      'EscrowLedgerInvariant',
      'escrow-ledger-invariant',
      'EscrowLedgerInvariant',
      'Escrow Ledger Invariant',
      'A ledger money check failed (ESCROW_LEDGER_INVARIANT in the escrow, escrow-cron or admin log group, with ' +
        'escrowId, ref and check). I1: the legs plus the refunded total no longer add up to the amount paid; ' +
        'I2: the released total differs from the released legs; I3: the ledger rows differ from the totals; ' +
        'I4: a leg would leave held twice. A decision that fails a check is not written and no money moves ' +
        '(the caller got 503 ESCROW_012); a check that fails after money moved is recorded and alarmed. ' +
        'check=AMOUNTS or SPLIT: a split verdict cannot be applied as recorded. Stop: compare the escrow row ' +
        'with its escrow-ledger rows and escalate. Never edit either by hand.',
    );

    const disputeVerdictMismatchAlarm = moneyLogAlarm(
      'DisputeVerdictOrderMismatch',
      'dispute-verdict-order-mismatch',
      'DisputeVerdictOrderMismatch',
      'Dispute Verdict Order Mismatch',
      'A dispute verdict was not settled because its order or delivery is in a status the ' +
        'verdict cannot follow (DISPUTE_VERDICT_ORDER_MISMATCH in the escrow or escrow-cron log ' +
        'group, with escrowId, op and legs; escrow-audit row dispute_verdict_mismatch, once a ' +
        'day). Since FUP-16 a new verdict the order cannot follow is refused at the admin ' +
        'page, so this is a verdict recorded before then, a disposition that did not finish, or ' +
        'an order that moved since. No money moved; the escrow stays dispute_resolved and the ' +
        'hourly sweep tries again. Open Disputes > Awaiting settlement and record (or send ' +
        'again) a disposition: return_to_farmer or delivered_anyway for buyer_wins, settle_now ' +
        'or wait_for_delivery for farmer_wins. A recorded wait_for_delivery is not alarmed (it ' +
        'logs DISPUTE_VERDICT_WAITING_FOR_DELIVERY). If no disposition fits (ADMIN_016), repair ' +
        'the order by hand (ESC-03, FUP-16).',
    );

    // FUP-27: before, a failed dispute mirror leg was only an escrow-audit row. Event-driven
    // (one line per failed mirror call), so the money alarm shape holds one episode per failure.
    const disputePropagationFailedAlarm = moneyLogAlarm(
      'DisputePropagationFailed',
      'dispute-propagation-failed',
      'DisputePropagationFailed',
      'Dispute Propagation Failed',
      'The delivery or order behind a frozen dispute did not follow it (DISPUTE_PROPAGATION_FAILED ' +
        'in the escrow, sync or escrow-cron log group, with escrowId, orderId, deliveryId and legs; ' +
        'escrow-audit row dispute_propagation_failed has the raw legs). The money is frozen (escrow ' +
        'disputed), so nothing pays out, but the order may still show delivered and stay open to ' +
        'ratings. legs lookup:<error> or delivery:<error>: a read or write failed (throttling, IAM); ' +
        'order:conflict: three version conflicts in a row; order:<code>: the order write was ' +
        'refused. Fix the cause, then check the trade in /transactions/<orderId>. Only a party\'s ' +
        'repeated dispute (POST /escrow/dispute again, or the delivery dispute resent through sync) ' +
        're-runs the mirror, and not while a manual payout or a settlement lock is open. Your lever ' +
        'is the admin verdict (POST /admin/disputes/{id}/resolve): it moves the order and delivery, ' +
        'again before it settles (ESC-03). Never edit the rows by hand (FUP-27).',
    );

    // ESC-16: with the commitments on, a live order escrow is held to its 21 day cap, so a missing
    // delivery stamp is no longer caught by the refund sweep within days. Event-driven: one line per
    // failed stamp (sync) or per failed repair (escrow-cron, each hourly run until it lands).
    const escrowDeliveryStampFailuresAlarm = moneyLogAlarm(
      'EscrowDeliveryStampFailures',
      'escrow-delivery-stamp-failures',
      'EscrowDeliveryStampFailures',
      'Escrow Delivery Stamp Failures',
      'A delivery was confirmed but its escrow was not marked delivery-confirmed ' +
        '(ESCROW_DELIVERY_STAMP_FAILED in the sync or escrow-cron log group, with deliveryId, escrowId when ' +
        'known, source and reason). Until the stamp lands the auto-release never pays the farmer and the ' +
        'trucker. source=sync: the inline stamp at the buyer\'s confirmation failed; the hourly order-money ' +
        'sweep of escrow-cron retries it for the delivered order (summary deliveryStampsRepaired). ' +
        'source=escrow-cron: that retry failed too, usually throttling or IAM on the escrows table; it ' +
        'keeps retrying every hour. Fix the cause and check the trade in /transactions/<orderId>. Never ' +
        'release by hand while the delivery stamp is missing, and never edit the escrow row by hand (ESC-16).',
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

    const riskSettlementSkippedAlarm = moneyLogAlarm(
      'RiskSettlementSkipped',
      'risk-settlement-skipped',
      'RiskSettlementSkipped',
      'Risk Settlement Skipped',
      'The fraud risk gate left money unmoved (RISK_SETTLEMENT_SKIPPED escrowId=.. in the escrow-cron log ' +
        'group; FRD-03 to FRD-05, FUP-29). Two sources, one line on every run that asks: a settlement ' +
        'sweep left an escrow unsettled (sweep=auto-release, auto-refund or ' +
        'dispute), or, since escrow-protocol 2.6.0, the payout gate left a leg unpaid (sweep=payout-farmer ' +
        'or payout-trucker). reason=flag_off (a sweep, FRAUD_HOLD_ENABLED off) or payee_flagged (a payout ' +
        'leg): a party was rejected in Fraud Review (holdReason=fraud_confirmed), suspended ' +
        '(account_suspended) or is at risk tier CRITICAL (risk_critical). Find the party from the escrow. ' +
        'Exits: unsuspend the party if the suspension was not about fraud, or review the signals behind the ' +
        'tier in Fraud Review; the next run settles or pays as before. A skipped sweep can also be settled ' +
        'by hand (POST /escrow/release or /escrow/refund, admin group). reason=user_read_failed or ' +
        'score_unavailable: a party row or risk score could not be read (IAM, throttling); fix the cause.',
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

    // FUP-231: an order deadline or refund the crons keep retrying. order-cron's commitment sweep (every 15
    // minutes) logs ORDER_DEADLINE_ROW_FAILED when a due row's decision throws (the row stays due) and, since
    // FUP-210, ORDER_DEADLINE_REFUND_WAITING when a paid order's deadline cancel waits because its refund cannot
    // be armed with it (looked at again an hour later). escrow-cron logs ORDER_STOCK_RESTORE_FAILED when a
    // cancelled order's stock could not go back on its listing; before the refund of a transport-failure cancel
    // (FUP-211: and of the refund's own cancel) the hourly sweep then defers the refund. One line is a retry
    // that usually lands; the line in three of four hourly periods is a cancel or refund that is stuck. One
    // metric from two filters, the persistence shape above. No default: billed only in the hours a line appears.
    new logs.MetricFilter(this, 'OrderCronDeadlineRefundStalledFilter', {
      logGroup: orderCronLogGroup,
      filterPattern: logs.FilterPattern.anyTerm('ORDER_DEADLINE_REFUND_WAITING', 'ORDER_DEADLINE_ROW_FAILED'),
      metricNamespace,
      metricName: 'OrderDeadlineRefundStalled',
      metricValue: '1',
    });
    new logs.MetricFilter(this, 'EscrowCronDeadlineRefundStalledFilter', {
      logGroup: escrowCronLogGroup,
      filterPattern: logs.FilterPattern.literal('"ORDER_STOCK_RESTORE_FAILED"'),
      metricNamespace,
      metricName: 'OrderDeadlineRefundStalled',
      metricValue: '1',
    });
    const orderDeadlineRefundStalledAlarm = new cloudwatch.Alarm(this, 'OrderDeadlineRefundStalled', {
      alarmName: `${prefix}-order-deadline-refund-stalled`,
      alarmDescription:
        'An order cancel or refund kept failing in three of the last four hourly periods (FUP-231; runbook: ' +
        '.ai/deployment.md). ORDER_DEADLINE_ROW_FAILED orderId=.. reason=[<exception>] (order-cron, every 15 ' +
        'minute run): a due order\'s deadline decision throws and the row stays due. ' +
        'ORDER_DEADLINE_REFUND_WAITING orderId=.. cancelReason=.. reason=escrow_busy or escrow_changed ' +
        '(order-cron): a paid order\'s deadline cancel waits because its refund cannot be armed with it (the ' +
        'money is disputed, being settled or in Manual Payouts); nothing is written and it is tried hourly. ' +
        'ORDER_STOCK_RESTORE_FAILED orderId=.. listingId=.. (escrow-cron): a cancelled order\'s stock could not go ' +
        'back on its listing, so the hourly sweep defers its refund. The buyer is still waiting. The lines may ' +
        'name different orders: read them. Fix the cause (IAM, throttling, the order, escrow or listing row) and ' +
        'the next run goes on. Never edit the rows by hand.',
      metric: new cloudwatch.Metric({
        namespace: metricNamespace,
        metricName: 'OrderDeadlineRefundStalled',
        statistic: 'Sum',
        period: cdk.Duration.hours(1),
        label: 'Order Deadline Refund Stalled',
      }),
      threshold: 0,
      evaluationPeriods: 4,
      datapointsToAlarm: 3,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
      treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
    });
    orderDeadlineRefundStalledAlarm.addAlarmAction(snsAction);
    orderDeadlineRefundStalledAlarm.addOkAction(snsAction);

    // ESC-36 (spec 4.5 T8: "alarm"): a delivery still on the road 72 hours after its pickup with no drop-off
    // check-in from the trucker. order-cron logs ORDER_DELIVERY_OVERDUE orderId=.. deliveryId=..
    // outcome=disputed|queued once per order it judges: disputed (the escrow frozen, the order and delivery
    // disputed as delivery_overdue, the trucker restricted pending review when reputationPenalties is on) or
    // queued (the money could not be frozen, so the order waits in the admin queue). Each line is an order an
    // admin must look at, so any line alarms. Behind dropoffClassification (off everywhere): no line today.
    // No default: billed only in the hours a line appears.
    new logs.MetricFilter(this, 'OrderCronDeliveryOverdueFilter', {
      logGroup: orderCronLogGroup,
      filterPattern: logs.FilterPattern.literal('"ORDER_DELIVERY_OVERDUE"'),
      metricNamespace,
      metricName: 'DeliveryOverdue',
      metricValue: '1',
    });
    const deliveryOverdueAlarm = new cloudwatch.Alarm(this, 'DeliveryOverdue', {
      alarmName: `${prefix}-delivery-overdue`,
      alarmDescription:
        'A delivery was not made by its deadline (ESC-36, T8; runbook: .ai/deployment.md). ORDER_DELIVERY_OVERDUE ' +
        'orderId=.. deliveryId=.. outcome=disputed: the trucker sent no drop-off check-in within 72 hours of the ' +
        'pickup; the escrow is frozen and the order is disputed as delivery_overdue. Decide the dispute in Disputes ' +
        'and the trucker\'s DELIVERY_OVERDUE review in Appeals. outcome=queued: the money could not be frozen (no ' +
        'escrow, a refund armed, Manual Payouts or a settlement in flight), so nothing moved and the order waits in ' +
        'Stuck Orders. Never edit the rows by hand.',
      metric: new cloudwatch.Metric({
        namespace: metricNamespace,
        metricName: 'DeliveryOverdue',
        statistic: 'Sum',
        period: cdk.Duration.hours(1),
        label: 'Delivery Overdue',
      }),
      threshold: 0,
      evaluationPeriods: 1,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
      treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
    });
    deliveryOverdueAlarm.addAlarmAction(snsAction);
    deliveryOverdueAlarm.addOkAction(snsAction);

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
    // exists when the live gateway is on (telebirrLive above). Any payment the
    // webhook could not apply is money the platform holds for a manual refund, and
    // the webhook answers it with a normal response (200, or ESC-45's 500), never a
    // Lambda error, so its Lambda Errors metric never shows it.
    let orderPaymentProblemsAlarm: cloudwatch.Alarm | undefined;
    let webhook5xxAlarm: cloudwatch.Alarm | undefined;
    let webhookErrorsAlarm: cloudwatch.Alarm | undefined;
    let webhookThrottlesAlarm: cloudwatch.Alarm | undefined;
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
            'ORDER_ESCROW_CREATE_FAILED: repair these, never refund; except ' +
            'reason=insufficient_stock, ESC-18: the stock is gone, so find stock or ' +
            'cancel the order and refund). Find the ' +
            'payment in this log group (an _UNPROCESSED payment may be recorded only ' +
            'here), the escrow audit (order_payment_unmatched, _duplicate, ' +
            '_unprocessed) or the webhook-inbox rows of the escrow idempotency table. ' +
            'Before refunding, read the order: if its telebirrPaymentOrderId is this ' +
            'payment, the payment funded the order and must not be refunded. Refund ' +
            'an _UNPROCESSED payment only once its order has left that payment attempt. ' +
            'ESC-45: an inbox payment is replayed every 5 minutes and pages ' +
            'only as reason=replay_exhausted (then in the exhausted partition of ' +
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

      // ESC-18: a funding that lost the listing's stock cancels the order here and arms its
      // refund; when that cannot be armed (or no escrow record exists, reason=no_escrow) the
      // webhook logs the same ORDER_ESCROW_REFUND_SCHEDULE_FAILED line as sync and order-cron.
      // Same metric, so the order-escrow-problems alarm adds this source too.
      new logs.MetricFilter(this, 'WebhookEscrowProblemsFilter', {
        logGroup: webhookLogGroup,
        filterPattern: logs.FilterPattern.anyTerm('ORDER_ESCROW_REFUND_SCHEDULE_FAILED'),
        metricNamespace,
        metricName: 'OrderEscrowProblems',
        metricValue: '1',
        defaultValue: 0,
      });

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

      // FUP-39: the webhook route is outside the API-wide 5xx rate, so these two page for it.
      // Volume: one payment's retryable 500 (ESC-45 D1) is replayed by the inbox sweep within
      // about 7 minutes (due 2 minutes after receipt, sweep every 5) and its redeliveries then
      // get 200, so it leaves a few 500s at most. Five in 15 minutes means several payments
      // failing, or one failing past its replays. It catches what has no log token: API Gateway
      // unable to invoke the Lambda, throttling, the catch-all after verification. Revisit the
      // count once the sandbox run shows Telebirr's retry interval (decision record, item 2).
      webhook5xxAlarm = new cloudwatch.Alarm(this, 'TelebirrWebhook5xx', {
        alarmName: `${prefix}-telebirr-webhook-5xx`,
        alarmDescription:
          `The Telebirr webhook route (POST ${TELEBIRR_WEBHOOK_PATH}) answered 5 or more 5xx in ` +
          '15 minutes (FUP-39; the API-wide high-api-5xx-rate alarm leaves this route out). One ' +
          '500 is normal: the webhook answers 500 on purpose when a verified payment is not yet ' +
          'recorded or applied, so Telebirr sends it again, and the inbox sweep applies it ' +
          '(ESC-45). Five or more: several payments failing, or one failing again and again. Look ' +
          'in the webhook log group: ' +
          'ORDER_PAYMENT_UNPROCESSED (no inbox row; order-payment-problems fires too), ' +
          'ORDER_PAYMENT_REPLAY_PENDING (the sweep retries; find the shared cause), "answered 500 ' +
          'for a retry" (the /escrow/initiate path, no inbox row), or no line for the request ' +
          '(API Gateway could not invoke the Lambda: integration, permission or throttling; see ' +
          'telebirr-webhook-errors and the API access log). A payment with no inbox row is ' +
          'recovered only by Telebirr\'s retries, whose schedule is undocumented: act quickly.',
        metric: webhookRouteMetric('5xx', cdk.Duration.minutes(15), 'Telebirr Webhook 5xx'),
        threshold: 5,
        evaluationPeriods: 1,
        comparisonOperator:
          cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
        treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
      });
      webhook5xxAlarm.addAlarmAction(snsAction);
      webhook5xxAlarm.addOkAction(snsAction);

      // A crash, timeout or out-of-memory kill of the webhook Lambda, on the public route or
      // the replay sweep. A deliberate 500 is a normal return, never a Lambda error, so this
      // fires only on real failures, at any traffic (the API-wide rate did that before FUP-39
      // only at low traffic). The aggregate LambdaErrors alarm is full (10 metrics). 3 periods,
      // 1 datapoint: a sweep crash and Lambda's two async retries stay one episode.
      webhookErrorsAlarm = new cloudwatch.Alarm(this, 'TelebirrWebhookErrors', {
        alarmName: `${prefix}-telebirr-webhook-errors`,
        alarmDescription:
          'The Telebirr webhook Lambda failed (FUP-39): a crash, the 15 second timeout or an ' +
          'out-of-memory kill, on the public notification route or on the 5 minute inbox replay ' +
          'sweep. A deliberate 500 never counts here. A notification that failed before its inbox ' +
          'write is recorded nowhere and only Telebirr\'s retries can bring it back. Find the ' +
          'RequestId in the webhook log group (REPORT line, "Task timed out"), fix the cause, then ' +
          'check order-payment-problems and the exhausted partition of InboxReplayIndex. Clears ' +
          '10 to 15 minutes after the last error.',
        metric: new cloudwatch.Metric({
          namespace: 'AWS/Lambda',
          metricName: 'Errors',
          dimensionsMap: { FunctionName: `${prefix}-telebirr-webhook` },
          statistic: 'Sum',
          period: cdk.Duration.minutes(5),
          label: 'Telebirr Webhook Errors',
        }),
        threshold: 1,
        evaluationPeriods: 3,
        datapointsToAlarm: 1,
        comparisonOperator:
          cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
        treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
      });
      webhookErrorsAlarm.addAlarmAction(snsAction);
      webhookErrorsAlarm.addOkAction(snsAction);

      // A throttled invocation is not a Lambda error, so the alarm above never counts it, and at
      // one or two payments per 15 minutes the route's 5xx stays under the volume alarm's count.
      // Any throttle pages: a throttled notification is recorded nowhere. The limit is the reserved
      // concurrency of 10 outside dev and the account's concurrency in dev (lambda-construct skips
      // reserved concurrency there). Same window as the errors alarm, so a burst stays one episode.
      webhookThrottlesAlarm = new cloudwatch.Alarm(this, 'TelebirrWebhookThrottles', {
        alarmName: `${prefix}-telebirr-webhook-throttles`,
        alarmDescription:
          'The Telebirr webhook Lambda was throttled (FUP-39): more invocations at once than its ' +
          'concurrency allows (reserved 10 outside dev, the account limit in dev), on the public ' +
          'notification route or the 5 minute inbox replay sweep. A throttled notification never ' +
          'ran, so it is recorded nowhere: Telebirr ' +
          'gets an error and only its retries can bring the payment back. A throttled sweep run is ' +
          'retried by Lambda. Check the ConcurrentExecutions of the webhook Lambda and the API ' +
          'access log for a burst on POST /webhooks/telebirr (a flood is refused by the signature ' +
          'check but still uses concurrency); raise the reserved concurrency only for real payment ' +
          'volume. Then check order-payment-problems. Clears 10 to 15 minutes after the last throttle.',
        metric: new cloudwatch.Metric({
          namespace: 'AWS/Lambda',
          metricName: 'Throttles',
          dimensionsMap: { FunctionName: `${prefix}-telebirr-webhook` },
          statistic: 'Sum',
          period: cdk.Duration.minutes(5),
          label: 'Telebirr Webhook Throttles',
        }),
        threshold: 1,
        evaluationPeriods: 3,
        datapointsToAlarm: 1,
        comparisonOperator:
          cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
        treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
      });
      webhookThrottlesAlarm.addAlarmAction(snsAction);
      webhookThrottlesAlarm.addOkAction(snsAction);
    }

    // Alarm 7: Order Cron Errors — the scheduled unpaid-order auto-cancel must never
    // fail silently (escrow-cron has the same dedicated alarm right below, ESC-13).
    // It gets a dedicated alarm rather than joining the aggregate LambdaErrors alarm,
    // which is capped at 10 metrics. FRD-15: the same Lambda runs the hourly fraud
    // relationship scan, so a crashed or timed-out scan lands here too.
    const orderCronErrorsAlarm = new cloudwatch.Alarm(
      this,
      'OrderCronErrors',
      {
        alarmName: `${prefix}-order-cron-errors`,
        alarmDescription:
          'The scheduled order jobs (order-cron: auto-cancel, commitment deadlines, ' +
          'relationship patterns) reported errors. Unpaid orders may not be expiring and ' +
          'fraud relationship signals may be missing; investigate the Lambda logs.',
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

    // ACC-02: every started account deletion must finish or abort within 24 hours. The 6 hourly
    // sweep logs ACCOUNT_DELETION_STUCK when it fails on a deletion requested at least 7 hours
    // ago (its second run on that row). 7 one-hour periods, 1 datapoint: the next run's line
    // lands inside the window, so a row that stays stuck keeps one ALARM episode.
    const accountDeletionSweepLogGroup = logs.LogGroup.fromLogGroupName(
      this,
      'AccountDeletionSweepLogGroup',
      `/aws/lambda/${prefix}-account-deletion-sweep`,
    );
    new logs.MetricFilter(this, 'AccountDeletionStuckFilter', {
      logGroup: accountDeletionSweepLogGroup,
      filterPattern: logs.FilterPattern.literal('"ACCOUNT_DELETION_STUCK"'),
      metricNamespace,
      metricName: 'AccountDeletionStuck',
      metricValue: '1',
      defaultValue: 0,
    });
    const accountDeletionStuckAlarm = new cloudwatch.Alarm(this, 'AccountDeletionStuck', {
      alarmName: `${prefix}-account-deletion-stuck`,
      alarmDescription:
        'A started account deletion failed in two sweep runs in a row and may miss its 24 hour bound ' +
        '(ACCOUNT_DELETION_STUCK in the account-deletion-sweep log group, with userId, state and ' +
        'requestedAt; account-deletion.md). state=in_progress: personal data may still be in the ' +
        'users row; cognito_pending: the Cognito user may still exist. Read the ' +
        'ACCOUNT_DELETION_INCOMPLETE lines for the failing step (Cognito, SNS or DynamoDB error) and ' +
        'fix its cause; every step is idempotent, so the next run or a manual invoke of the sweep ' +
        'with {"body": "{\\"action\\":\\"sweep\\"}"} resumes it. Never edit the users row by hand.',
      metric: new cloudwatch.Metric({
        namespace: metricNamespace,
        metricName: 'AccountDeletionStuck',
        statistic: 'Sum',
        period: cdk.Duration.hours(1),
        label: 'Account Deletion Stuck',
      }),
      threshold: 0,
      evaluationPeriods: 7,
      datapointsToAlarm: 1,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
      treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
    });
    accountDeletionStuckAlarm.addAlarmAction(snsAction);
    accountDeletionStuckAlarm.addOkAction(snsAction);

    // ESC-21: the order notification stream consumer (orders, deliveries and escrows streams,
    // and since ESC-28 the reputation-events stream, into the inbox table and FCM). Three
    // standalone alarms: the aggregate LambdaErrors expression is already at its 10 metric
    // cap. Names follow the api-stack resources
    // (`${prefix}-notify-dlq`, `${prefix}-order-notify-stream`); no cross-stack refs.
    const notifyStreamFunctionName = `${prefix}-order-notify-stream`;

    // A batch that failed every retry, or a record older than one day, is dropped from the
    // stream and its metadata lands in the DLQ. Messages stay visible until purged (14 day
    // retention), so the alarm holds until someone drains the queue.
    const notifyDlqAlarm = new cloudwatch.Alarm(this, 'NotifyDlqMessages', {
      alarmName: `${prefix}-notify-dlq-messages`,
      alarmDescription:
        'Order notifications were dropped: a stream batch from the orders, deliveries, escrows or ' +
        'reputation-events table failed all its retries or was older than one day, so its inbox rows ' +
        'and pushes were never written (the source rows themselves are unaffected). Each message ' +
        'in the notify-dlq queue holds the stream ARN, shard and sequence range of the dropped ' +
        'records, not the records; the table stream keeps them for 24 hours. Find the derive or ' +
        'inbox write error in the order-notify-stream log group, fix it, then purge the queue to ' +
        'clear this alarm.',
      metric: new cloudwatch.Metric({
        namespace: 'AWS/SQS',
        metricName: 'ApproximateNumberOfMessagesVisible',
        dimensionsMap: { QueueName: `${prefix}-notify-dlq` },
        statistic: 'Maximum',
        period: cdk.Duration.minutes(5),
        label: 'Notify DLQ Messages',
      }),
      threshold: 1,
      evaluationPeriods: 1,
      comparisonOperator:
        cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
      treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
    });
    notifyDlqAlarm.addAlarmAction(snsAction);
    notifyDlqAlarm.addOkAction(snsAction);

    // The consumer is falling behind its streams: notifications arrive late, and a record that
    // reaches one day of age is dropped to the DLQ instead of delivered.
    const notifyIteratorAgeAlarm = new cloudwatch.Alarm(this, 'NotifyStreamIteratorAge', {
      alarmName: `${prefix}-notify-stream-iterator-age`,
      alarmDescription:
        'order-notify-stream is more than 15 minutes behind the orders, deliveries, escrows or ' +
        'reputation-events stream, so notifications and pushes are late. Usual causes: a record failing and ' +
        'retrying (see the log group), throttling at its reserved concurrency, or slow SNS or ' +
        'DynamoDB calls. A record that reaches one day of age is dropped to the notify-dlq queue.',
      metric: new cloudwatch.Metric({
        namespace: 'AWS/Lambda',
        metricName: 'IteratorAge',
        dimensionsMap: { FunctionName: notifyStreamFunctionName },
        statistic: 'Maximum',
        period: cdk.Duration.minutes(5),
        label: 'Notify Stream Iterator Age',
      }),
      threshold: 900000,
      evaluationPeriods: 1,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
      treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
    });
    notifyIteratorAgeAlarm.addAlarmAction(snsAction);
    notifyIteratorAgeAlarm.addOkAction(snsAction);

    // The handler reports a failed record through the partial batch response, which is not a
    // Lambda error. Errors here mean the invocation itself died.
    const notifyStreamErrorsAlarm = new cloudwatch.Alarm(this, 'NotifyStreamErrors', {
      alarmName: `${prefix}-notify-stream-errors`,
      alarmDescription:
        'order-notify-stream invocations failed: timeout, out of memory, a runtime crash or a ' +
        'missing environment variable. A failed record alone never counts here (it is retried ' +
        'through the partial batch response). The whole batch is retried, bisected, and dropped ' +
        'to the notify-dlq queue if it keeps failing. Check the order-notify-stream log group.',
      metric: new cloudwatch.Metric({
        namespace: 'AWS/Lambda',
        metricName: 'Errors',
        dimensionsMap: { FunctionName: notifyStreamFunctionName },
        statistic: 'Sum',
        period: cdk.Duration.minutes(5),
        label: 'Notify Stream Errors',
      }),
      threshold: 0,
      evaluationPeriods: 1,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
      treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
    });
    notifyStreamErrorsAlarm.addAlarmAction(snsAction);
    notifyStreamErrorsAlarm.addOkAction(snsAction);

    // ─── ESC-28: reputation stream, parked records and reputation log lines ─────
    // reputation-architecture.md section 14. Names follow the api-stack resources
    // (`${prefix}-reputation-dlq`, `${prefix}-reputation-parked-dlq`, `${prefix}-reputation-stream`);
    // no cross-stack refs. Standalone alarms, like the notify stream's: the aggregate LambdaErrors
    // expression is at its 10 metric cap.
    const reputationStreamFunctionName = `${prefix}-reputation-stream`;

    // Metadata only: a record that failed and could not be parked either, after its 10 retries or
    // 24 hours of age. Messages stay visible until purged (14 day retention).
    const reputationDlqAlarm = new cloudwatch.Alarm(this, 'ReputationDlqMessages', {
      alarmName: `${prefix}-reputation-dlq-messages`,
      alarmDescription:
        'Reputation events may be lost: an orders stream record failed in reputation-stream and could not ' +
        'be parked either, or reached 24 hours of age, so only its metadata reached the reputation-dlq queue ' +
        '(stream ARN, shard and sequence range, not the record). The orders stream keeps a record for 24 ' +
        'hours; after that the event it carried is gone (no reconcile tool yet). Find the error ' +
        '(REPUTATION_PARK_FAILED and the exception) in the reputation-stream log group, fix it, then purge ' +
        'the queue to clear this alarm.',
      metric: new cloudwatch.Metric({
        namespace: 'AWS/SQS',
        metricName: 'ApproximateNumberOfMessagesVisible',
        dimensionsMap: { QueueName: `${prefix}-reputation-dlq` },
        statistic: 'Maximum',
        period: cdk.Duration.minutes(5),
        label: 'Reputation DLQ Messages',
      }),
      threshold: 1,
      evaluationPeriods: 1,
      comparisonOperator:
        cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
      treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
    });
    reputationDlqAlarm.addAlarmAction(snsAction);
    reputationDlqAlarm.addOkAction(snsAction);

    // A parked record that failed 5 hourly replays (the parked queue's redrive policy).
    const reputationParkedDlqAlarm = new cloudwatch.Alarm(this, 'ReputationParkedDlqMessages', {
      alarmName: `${prefix}-reputation-parked-dlq-messages`,
      alarmDescription:
        'A parked reputation record failed 5 replays (order-cron reputation_unpark, hourly) and moved to the ' +
        'reputation-parked-dlq queue, so its reputation event is not recorded yet. Each message holds the ' +
        'order keys and the projected attributes the derivation reads. Find the replay error in the ' +
        'order-cron log group and fix it, then move the messages back to the reputation-parked queue (SQS ' +
        'dead-letter redrive); the next unpark replays them, and a replay is safe because each event has a ' +
        'fixed slot id.',
      metric: new cloudwatch.Metric({
        namespace: 'AWS/SQS',
        metricName: 'ApproximateNumberOfMessagesVisible',
        dimensionsMap: { QueueName: `${prefix}-reputation-parked-dlq` },
        statistic: 'Maximum',
        period: cdk.Duration.minutes(5),
        label: 'Reputation Parked DLQ Messages',
      }),
      threshold: 1,
      evaluationPeriods: 1,
      comparisonOperator:
        cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
      treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
    });
    reputationParkedDlqAlarm.addAlarmAction(snsAction);
    reputationParkedDlqAlarm.addOkAction(snsAction);

    // The consumer is falling behind the orders stream; at 24 hours a record goes to the DLQ.
    const reputationIteratorAgeAlarm = new cloudwatch.Alarm(this, 'ReputationStreamIteratorAge', {
      alarmName: `${prefix}-reputation-stream-iterator-age`,
      alarmDescription:
        'reputation-stream is more than 1 hour behind the orders stream, so reputation events land late. ' +
        'Usual causes: more stream shards than its reserved concurrency (10), a record retrying (see the ' +
        'log group), or slow DynamoDB calls. A record that reaches 24 hours of age goes to the ' +
        'reputation-dlq queue and its event is lost.',
      metric: new cloudwatch.Metric({
        namespace: 'AWS/Lambda',
        metricName: 'IteratorAge',
        dimensionsMap: { FunctionName: reputationStreamFunctionName },
        statistic: 'Maximum',
        period: cdk.Duration.minutes(5),
        label: 'Reputation Stream Iterator Age',
      }),
      threshold: 3600000,
      evaluationPeriods: 1,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
      treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
    });
    reputationIteratorAgeAlarm.addAlarmAction(snsAction);
    reputationIteratorAgeAlarm.addOkAction(snsAction);

    // As for the notify stream: a failed record is parked or reported through the partial batch
    // response, never a Lambda error. Errors here mean the invocation itself died.
    const reputationStreamErrorsAlarm = new cloudwatch.Alarm(this, 'ReputationStreamErrors', {
      alarmName: `${prefix}-reputation-stream-errors`,
      alarmDescription:
        'reputation-stream invocations failed: timeout, out of memory, a runtime crash or a missing ' +
        'environment variable. A failed record alone never counts here (it is parked, or retried through ' +
        'the partial batch response). The whole batch is retried, bisected, and its metadata sent to the ' +
        'reputation-dlq queue if it keeps failing. Check the reputation-stream log group.',
      metric: new cloudwatch.Metric({
        namespace: 'AWS/Lambda',
        metricName: 'Errors',
        dimensionsMap: { FunctionName: reputationStreamFunctionName },
        statistic: 'Sum',
        period: cdk.Duration.minutes(5),
        label: 'Reputation Stream Errors',
      }),
      threshold: 0,
      evaluationPeriods: 1,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_THRESHOLD,
      treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
    });
    reputationStreamErrorsAlarm.addAlarmAction(snsAction);
    reputationStreamErrorsAlarm.addOkAction(snsAction);

    // The reputation log tokens (ReputationService, the stream and the order-cron actions), one metric
    // each, counted on every log group that can write the line. No defaultValue: the lines are rare,
    // and a metric published only when one appears is billed only for those hours (the risk filters'
    // choice). The api-stack Lambda construct creates each log group explicitly, so a filter on it is
    // safe on a fresh deploy.
    const reputationStreamLogGroup = logs.LogGroup.fromLogGroupName(
      this,
      'ReputationStreamLogGroup',
      `/aws/lambda/${reputationStreamFunctionName}`,
    );
    const reputationLogGroups = {
      Stream: reputationStreamLogGroup,
      OrderCron: orderCronLogGroup,
      Sync: syncLogGroup,
      // FUP-163: an admin suspend or unsuspend writes the heal marker and refreshes the copies.
      Admin: adminLogGroup,
    };
    const reputationLogAlarm = (
      token: string,
      metricName: string,
      sources: Array<keyof typeof reputationLogGroups>,
      alarm: {
        nameSuffix: string;
        label: string;
        description: string;
        threshold: number;
        period: cdk.Duration;
      },
    ): cloudwatch.Alarm => {
      for (const source of sources) {
        new logs.MetricFilter(this, `${metricName}${source}Filter`, {
          logGroup: reputationLogGroups[source],
          filterPattern: logs.FilterPattern.literal(`"${token}"`),
          metricNamespace,
          metricName,
          metricValue: '1',
        });
      }
      const created = new cloudwatch.Alarm(this, metricName, {
        alarmName: `${prefix}-${alarm.nameSuffix}`,
        alarmDescription: alarm.description,
        metric: new cloudwatch.Metric({
          namespace: metricNamespace,
          metricName,
          statistic: 'Sum',
          period: alarm.period,
          label: alarm.label,
        }),
        threshold: alarm.threshold,
        evaluationPeriods: 1,
        comparisonOperator:
          cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
        treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
      });
      created.addAlarmAction(snsAction);
      created.addOkAction(snsAction);
      return created;
    };

    const reputationPolicyInvalidAlarm = reputationLogAlarm(
      'REPUTATION_POLICY_INVALID',
      'ReputationPolicyInvalid',
      ['Stream', 'OrderCron', 'Sync'],
      {
        nameSuffix: 'reputation-policy-invalid',
        label: 'Reputation Policy Invalid',
        threshold: 1,
        period: cdk.Duration.minutes(5),
        description:
          `The reputation policy parameter /${prefix}/reputation/policy is missing or not JSON, or a key is ` +
          'missing, not a number or out of range (REPUTATION_POLICY_INVALID key=<k> in the reputation-stream, ' +
          'order-cron or sync log group). Each such key falls back to its default, so reputation keeps ' +
          'working on the defaults. Fix the parameter (keys and ranges: reputation-architecture.md 5.9); ' +
          'each Lambda container reads it once, so the fix applies as containers recycle.',
      },
    );

    const reputationEventsTruncatedAlarm = reputationLogAlarm(
      'REPUTATION_EVENTS_TRUNCATED',
      'ReputationEventsTruncated',
      ['Stream', 'OrderCron', 'Sync'],
      {
        nameSuffix: 'reputation-events-truncated',
        label: 'Reputation Events Truncated',
        threshold: 1,
        period: cdk.Duration.minutes(5),
        description:
          'A user has more reputation event rows than eventsReadCap (REPUTATION_EVENTS_TRUNCATED in the ' +
          'reputation-stream, order-cron or sync log group, with the userId), so their points and counts ' +
          'were computed from the rows read and may be low. Check the partition (a runaway writer, or TTL ' +
          'not purging on purgeAt) and raise eventsReadCap in the policy parameter if the volume is genuine.',
      },
    );

    const reputationReviewOverdueAlarm = reputationLogAlarm(
      'REPUTATION_REVIEW_OVERDUE',
      'ReputationReviewOverdue',
      ['OrderCron'],
      {
        nameSuffix: 'reputation-review-overdue',
        label: 'Reputation Review Overdue',
        threshold: 1,
        period: cdk.Duration.minutes(5),
        description:
          'An open DELIVERY_OVERDUE review has waited more than 7 days in the admin reputation queue ' +
          '(REPUTATION_REVIEW_OVERDUE from the daily order-cron job). The trucker stays restricted, with no ' +
          'end date, until an admin decides it. Decide the oldest reviews in the admin console.',
      },
    );

    const reputationAppealOverdueAlarm = reputationLogAlarm(
      'REPUTATION_APPEAL_OVERDUE',
      'ReputationAppealOverdue',
      ['OrderCron'],
      {
        nameSuffix: 'reputation-appeal-overdue',
        label: 'Reputation Appeal Overdue',
        threshold: 1,
        period: cdk.Duration.minutes(5),
        description:
          'A reputation appeal has waited more than 7 days for an admin decision (REPUTATION_APPEAL_OVERDUE ' +
          'from the daily order-cron job). The penalty keeps counting until it is excused or upheld. Decide ' +
          'the oldest pending appeals in the admin console.',
      },
    );

    // A rate alarm: a single failed refresh is retried by the daily job (the heal marker), so only a
    // steady rate needs a person. FUP-163: the admin Lambda logs the token too, for a suspend or unsuspend
    // whose heal marker (step=admin_<action>) or refresh in the request (step=admin_<action>_refresh) failed.
    const reputationCopyFailedAlarm = reputationLogAlarm(
      'REPUTATION_COPY_FAILED',
      'ReputationCopyFailed',
      ['Stream', 'OrderCron', 'Sync', 'Admin'],
      {
        nameSuffix: 'reputation-copy-failed',
        label: 'Reputation Copy Failed',
        threshold: 5,
        period: cdk.Duration.hours(1),
        description:
          'At least 5 reputation copy refreshes failed in an hour (REPUTATION_COPY_FAILED in the ' +
          'reputation-stream, order-cron, sync or admin log group), so the owner tier and rating copied onto ' +
          'some crop and trip rows are stale. Each failure left a heal marker and the daily job retries it ' +
          'within a day; a steady rate means DynamoDB throttling or a bug in the refresh. Read the logged ' +
          'errors. step=admin_suspend or admin_unsuspend (admin, FUP-163): the heal marker itself was not ' +
          'written; if the refresh of that request failed too, nothing retries that user until their next ' +
          'reputation change: once the cause is fixed, run order-cron reputation_stamp_owner_copies (safe to rerun).',
      },
    );

    const reputationParkFailedAlarm = reputationLogAlarm(
      'REPUTATION_PARK_FAILED',
      'ReputationParkFailed',
      ['Stream', 'OrderCron'],
      {
        nameSuffix: 'reputation-park-failed',
        label: 'Reputation Park Failed',
        threshold: 1,
        period: cdk.Duration.minutes(5),
        description:
          'A reputation record could not be parked in the reputation-parked queue (REPUTATION_PARK_FAILED in ' +
          'the reputation-stream or order-cron log group). The stream retries it, and after 10 retries or 24 ' +
          'hours only its metadata reaches reputation-dlq and the event is lost. Check the SQS error: the ' +
          'send permission, the REPUTATION_PARKED_QUEUE_URL value, or a message over the 256 KB limit.',
      },
    );

    // FUP-201: ESC-33's fraud hooks run once per recorded event (ReputationService.RecordAsync, in the stream
    // and in order-cron's unpark replay). A failed hook is never retried, so each line is a signal lost for
    // good: any line alarms, like a failed park.
    const reputationFraudHookFailedAlarm = reputationLogAlarm(
      'REPUTATION_FRAUD_HOOK_FAILED',
      'ReputationFraudHookFailed',
      ['Stream', 'OrderCron'],
      {
        nameSuffix: 'reputation-fraud-hook-failed',
        label: 'Reputation Fraud Hook Failed',
        threshold: 1,
        period: cdk.Duration.minutes(5),
        description:
          'A fraud signal that a recorded reputation event should have written was lost ' +
          '(REPUTATION_FRAUD_HOOK_FAILED hook=<reliability|buyer_decline> code=<code> [<exception type>] in the ' +
          'reputation-stream or order-cron log group; ESC-33, fraud spec section 15.3). The event and its points ' +
          'stand, but the hook never runs again for it. hook=reliability: the NO_SHOW or DELIVERY_OVERDUE signal ' +
          'on the penalised party. hook=buyer_decline: the buyer decline pattern (the read of the buyer\'s orders ' +
          'on BuyerIdCreatedAtIndex, or the BUYER_DECLINE_PATTERN write). Usually throttling or IAM on the ' +
          'fraud-signals table (PutItem, Query) or the orders index, or FRAUD_SIGNALS_TABLE or ORDERS_TABLE ' +
          'missing from the Lambda. Fix the cause. The line has no ids: if the party matters, find the event by ' +
          'its code and time in the admin reputation history and review the party in Fraud Review by hand.',
      },
    );

    // ─── FRD-16: fraud signal metrics and the risk alarms ────────────
    // Fraud spec section 23.1. Synthetic test: scripts/fraud_alarms_synthetic.sh.
    //
    // Every Lambda whose handler builds a FraudDetectionService, the one writer of signal rows and
    // reader of risk scores (an infra test checks this list against server/src/Functions).
    // RecordFraudSignalAsync logs one line per row it writes:
    //   {"event":"FRAUD_SIGNAL_RECORDED","signalType":"<type>","entityType":"<kind>"}
    // A refusal the hourly per-user, per-type write limit absorbs logs nothing, so the metrics count
    // the rows Fraud Review shows. Today sync, escrow, auth-refresh and the webhook write signals;
    // sync, escrow, escrow-cron and admin read scores; order-cron gets FRD-15's relationship scan.
    // Filters are free and a log group without the line publishes nothing new.
    const fraudLogGroupOf = (nameSuffix: string): logs.ILogGroup =>
      logs.LogGroup.fromLogGroupName(this, `FraudLogGroup-${nameSuffix}`, `/aws/lambda/${prefix}-${nameSuffix}`);
    const fraudLogGroups: Array<[string, logs.ILogGroup]> = [
      ['Sync', syncLogGroup],
      ['Escrow', escrowLogGroup],
      ['EscrowCron', escrowCronLogGroup],
      ['OrderCron', orderCronLogGroup],
      ['AuthRefresh', fraudLogGroupOf('auth-refresh')],
      ['Admin', fraudLogGroupOf('admin')],
      // FRD-19: the AI advisory cron scores users and writes AI_ADVISORY rows (one signal line each).
      ['FraudAdvisory', fraudLogGroupOf('fraud-advisory')],
      // ESC-28: the reputation stream builds the fraud service for the earn-back check (it reads
      // SELF_TRADE and SHARED_DEVICE_TRADE rows); ESC-33 makes it write NO_SHOW and DELIVERY_OVERDUE.
      ['ReputationStream', fraudLogGroupOf('reputation-stream')],
      // The webhook log group exists only with the live gateway.
      ...(telebirrLive ? [['TelebirrWebhook', fraudLogGroupOf('telebirr-webhook')] as [string, logs.ILogGroup]] : []),
    ];
    for (const [key, logGroup] of fraudLogGroups) {
      // Total volume: a plain term, so it matches with or without the Lambda runtime's line prefix
      // (the alarm below depends only on this one). defaultValue 0 gives the anomaly model its quiet
      // baseline; one metric, however many log groups publish it.
      new logs.MetricFilter(this, `FraudSignals${key}Filter`, {
        logGroup,
        filterPattern: logs.FilterPattern.literal('"FRAUD_SIGNAL_RECORDED"'),
        metricNamespace,
        metricName: 'FraudSignalsRecorded',
        metricValue: '1',
        defaultValue: 0,
      });
      // Per type: a JSON filter with the type as a dimension, one filter per log group whatever the
      // number of types, and new types (FRD-15, ESC-33) need no infra change. Each type is its own
      // custom metric, billed only in the hours it is written (CloudWatch does not allow a
      // defaultValue with dimensions, and one metric filter per type would need a filter per type
      // and log group, about 150, with the same cost).
      new logs.MetricFilter(this, `FraudSignalsByType${key}Filter`, {
        logGroup,
        filterPattern: logs.FilterPattern.stringValue('$.event', '=', 'FRAUD_SIGNAL_RECORDED'),
        metricNamespace,
        metricName: 'FraudSignalsByType',
        metricValue: '1',
        dimensions: { SignalType: '$.signalType' },
      });
    }

    // The risk lines (fraud spec sections 14.4 and 18; MoneyAlarmLog and FraudDetectionService). No
    // defaultValue: these lines are rare, a metric published only when one appears is billed only
    // for those hours, and the alarms treat missing data as not breaching, so they behave the same.
    // The score lines come from FraudDetectionService.GetUserRiskScoreAsync, so they are counted on
    // every log group above.
    const riskLogFilter = (id: string, logGroup: logs.ILogGroup, token: string, metricName: string) =>
      new logs.MetricFilter(this, id, {
        logGroup,
        filterPattern: logs.FilterPattern.literal(`"${token}"`),
        metricNamespace,
        metricName,
        metricValue: '1',
      });
    const riskScoreTokens: Array<[string, string]> = [
      ['RISK_SCORE_UNAVAILABLE', 'RiskScoreUnavailable'],
      ['RISK_SCORE_TRUNCATED', 'RiskScoreTruncated'],
      ['RISK_SCORE_UNKNOWN_RECORD', 'RiskScoreUnknownRecord'],
    ];
    for (const [token, metricName] of riskScoreTokens) {
      for (const [key, logGroup] of fraudLogGroups) {
        riskLogFilter(`${metricName}${key}Filter`, logGroup, token, metricName);
      }
    }
    // An admin refund of a held escrow is refused in the escrow Lambda only (EscrowService
    // RefuseHeldRefundAsync; the sweeps never select a held escrow).
    riskLogFilter('RiskHoldOrderMismatchFilter', escrowLogGroup, 'RISK_HOLD_ORDER_MISMATCH', 'RiskHoldOrderMismatch');
    // escrow-cron logs it once per hourly settlement run while its gate reads no risk tier.
    riskLogFilter('RiskTableUnconfiguredFilter', escrowCronLogGroup, 'RISK_TABLE_UNCONFIGURED', 'RiskTableUnconfigured');
    // FRD-10: a clean-delivery credit that could not be written. Not alarmed by design (a lost
    // credit only leaves a score higher); a metric for the dashboard.
    riskLogFilter('CleanDeliveryCreditFailedFilter', escrowCronLogGroup, 'CLEAN_DELIVERY_CREDIT_FAILED', 'CleanDeliveryCreditFailed');

    const riskScoreUnavailableAlarm = moneyLogAlarm(
      'RiskScoreUnavailable',
      'risk-score-unavailable',
      'RiskScoreUnavailable',
      'Risk Score Unavailable',
      'A risk score read failed (RISK_SCORE_UNAVAILABLE userId=.. error=<exception type> in the sync, ' +
        'escrow, escrow-cron or admin log group; fraud spec section 14.4). At a user gate (sync, escrow) ' +
        'the user was scored LOW and only the baseline step-up applied, so a risky user may have passed. ' +
        'In a settlement sweep the escrow was skipped and retried next hour (risk-settlement-skipped ' +
        'fires too). On the admin Fraud Review page the score shows as unavailable. error=EmptyUserId is ' +
        'a caller bug. Otherwise the fraud-signals Query failed: check throttling on the fraud-signals ' +
        'table, the Lambda role (dynamodb:Query on it) and its FRAUD_SIGNALS_TABLE variable. One line per ' +
        'failed read; it clears 75 to 90 minutes after the last line (FRD-16).',
    );

    const riskScoreTruncatedAlarm = moneyLogAlarm(
      'RiskScoreTruncated',
      'risk-score-truncated',
      'RiskScoreTruncated',
      'Risk Score Truncated',
      'A user\'s signal rows did not fit in RISK_MAX_PAGES pages, so the user was scored CRITICAL ' +
        '(RISK_SCORE_TRUNCATED userId=.. pages=.. in the sync, escrow, escrow-cron or admin log group; ' +
        'fraud spec section 14.3): payments and creates are refused (FRAUD_ACCOUNT_HELD) and their ' +
        'escrows held or skipped. It fails closed on purpose. With the hourly write limit an honest user ' +
        'cannot reach about 3,000 rows in 30 days, so this is abuse across many types and hours (a ' +
        'line with probe=<error> is a failed check for rows left, retried on the next read). Open the ' +
        'user in Fraud Review: reject the signals if it is abuse. If the rows are honest (a client ' +
        'bug), RISK_MAX_PAGES (at most 10) ' +
        'on the sync, escrow, escrow-cron and admin Lambdas reads further; otherwise the tier stays ' +
        'CRITICAL until rows leave the 30 day window. Logged on every read of that user. Never delete ' +
        'signal rows by hand (FRD-16).',
    );

    const riskScoreUnknownRecordAlarm = moneyLogAlarm(
      'RiskScoreUnknownRecord',
      'risk-score-unknown-record',
      'RiskScoreUnknownRecord',
      'Risk Score Unknown Record',
      'A risk score read met fraud-signals rows whose recordType it does not know and counted them as ' +
        'signals, which can raise the user\'s tier (RISK_SCORE_UNKNOWN_RECORD userId=.. rows=.. ' +
        'recordType=<first value> in the sync, escrow, escrow-cron or admin log group; fraud spec ' +
        'sections 14.3 and 20). Usually a newer server wrote a new row kind that a Lambda still on the ' +
        'older build reads (a deploy in progress or a partial rollback), or a row was edited by hand. ' +
        'Deploy one server build to every Lambda. If the recordType is not in section 20 of the fraud ' +
        'spec, find who wrote it. Never delete the rows by hand (FRD-16).',
    );

    const riskHoldOrderMismatchAlarm = moneyLogAlarm(
      'RiskHoldOrderMismatch',
      'risk-hold-order-mismatch',
      'RiskHoldOrderMismatch',
      'Risk Hold Order Mismatch',
      'An admin refund of an escrow held for fraud review was refused because its order cannot follow ' +
        'the refund (RISK_HOLD_ORDER_MISMATCH escrowId=.. heldFor=<release|refund> legs=order:<status> in ' +
        'the escrow log group, answered 422; escrow-audit row admin_settlement_order_refused; FRD-03, ' +
        'fraud spec section 18.3). No money moved and the escrow stays held. legs names the order status ' +
        'that blocks it: a progressing, missing or unexpected order. Open /transactions/<orderId> in the ' +
        'admin app and reconcile the order with the parties: release instead if the goods were delivered, ' +
        'or refund again once the order can be cancelled. Never edit the escrow or order row by hand ' +
        '(FRD-16).',
    );

    const riskTableUnconfiguredAlarm = moneyLogAlarm(
      'RiskTableUnconfigured',
      'risk-table-unconfigured',
      'RiskTableUnconfigured',
      'Risk Table Unconfigured',
      'escrow-cron\'s settlement risk gate reads no risk tier because FRAUD_SIGNALS_TABLE is empty ' +
        '(RISK_TABLE_UNCONFIGURED action=auto_release_and_refund, once per hourly run, FRAUD_HOLD_ENABLED ' +
        'on or off; fraud spec section 14.4). Suspended parties are still stopped, but a party at risk ' +
        'tier CRITICAL is paid or refunded as if LOW, without a trace. A deploy or a stack without the ' +
        'variable: check the escrow-cron Lambda configuration and deploy the api stack (escrow-cron needs ' +
        'FRAUD_SIGNALS_TABLE with read and PutItem on the fraud-signals table, FRD-04 and FRD-10). Stays ' +
        'in ALARM while the hourly runs log it and clears 75 to 90 minutes after the last one (FRD-16).',
    );

    // Total signal volume: an anomaly band AND a floor, both on one 15 minute period, joined by a
    // composite alarm that alone notifies. The band learns the daily and weekly pattern of the
    // metric; the floor keeps a quiet environment (dev, or prod at night) from paging on a few
    // signals that sit above a band learned from almost only zeros. Missing data (no log events in
    // any signal log group) is not breaching. The two children have no actions.
    const signalVolume = new cloudwatch.Metric({
      namespace: metricNamespace,
      metricName: 'FraudSignalsRecorded',
      statistic: 'Sum',
      period: cdk.Duration.minutes(15),
      label: 'Fraud Signals Recorded',
    });
    const signalVolumeStdDevs = 3;
    const signalVolumeFloor = 10;
    const signalVolumeAnomalyAlarm = new cloudwatch.AnomalyDetectionAlarm(this, 'FraudSignalVolumeAnomaly', {
      alarmName: `${prefix}-fraud-signal-volume-anomaly`,
      alarmDescription:
        'Part of fraud-signal-volume-spike, which notifies; this one has no actions. The fraud signal ' +
        `rows written in 15 minutes (FraudSignalsRecorded) are above the anomaly band (${signalVolumeStdDevs} ` +
        'standard deviations) the model learned from the metric\'s history (FRD-16).',
      metric: signalVolume,
      stdDevs: signalVolumeStdDevs,
      evaluationPeriods: 1,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_UPPER_THRESHOLD,
      treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
    });
    const signalVolumeFloorAlarm = new cloudwatch.Alarm(this, 'FraudSignalVolumeFloor', {
      alarmName: `${prefix}-fraud-signal-volume-floor`,
      alarmDescription:
        'Part of fraud-signal-volume-spike, which notifies; this one has no actions. At least ' +
        `${signalVolumeFloor} fraud signal rows were written in 15 minutes (FraudSignalsRecorded), so a ` +
        'spike above the anomaly band is more than a few signals on a quiet day (FRD-16).',
      metric: signalVolume,
      threshold: signalVolumeFloor,
      evaluationPeriods: 1,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
      treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
    });
    const signalVolumeSpikeAlarm = new cloudwatch.CompositeAlarm(this, 'FraudSignalVolumeSpike', {
      compositeAlarmName: `${prefix}-fraud-signal-volume-spike`,
      alarmDescription:
        'Fraud signal volume spiked: the rows written to the fraud-signals table in one 15 minute period ' +
        `(FRAUD_SIGNAL_RECORDED lines) were above the anomaly band and at least ${signalVolumeFloor}. Each ` +
        'row is one user, one type, one hour. Find the type in the dashboard graph Fraud Signals by Type ' +
        '(metric FraudSignalsByType, dimension SignalType). One type from many users after a release ' +
        '(ROLE_FORBIDDEN, IMMUTABLE_FIELD_CHANGE, STALE_VERSION_BURST, QR_HANDOFF_FAILURE): suspect a client ' +
        'bug and roll it back. New accounts, one relationship type, or IDOR_CREATE_CONFLICT probes: open ' +
        'Fraud Review and look for a ring. This alarm only tells: the per-user risk tiers act on their ' +
        'own. A run of scripts/e2e_fraud_personas.py or scripts/fraud_alarms_synthetic.sh fires it ' +
        'too. Fraud spec section 23.1 (FRD-16).',
      alarmRule: cloudwatch.AlarmRule.allOf(
        cloudwatch.AlarmRule.fromAlarm(signalVolumeAnomalyAlarm, cloudwatch.AlarmState.ALARM),
        cloudwatch.AlarmRule.fromAlarm(signalVolumeFloorAlarm, cloudwatch.AlarmState.ALARM),
      ),
    });
    signalVolumeSpikeAlarm.addAlarmAction(snsAction);
    signalVolumeSpikeAlarm.addOkAction(snsAction);

    const hourlySum = (metricName: string, label: string) =>
      new cloudwatch.Metric({
        namespace: metricNamespace,
        metricName,
        statistic: 'Sum',
        period: cdk.Duration.hours(1),
        label,
      });
    dashboard.addWidgets(
      new cloudwatch.GraphWidget({
        title: 'Fraud Signals Recorded (15 min, anomaly band)',
        left: [
          signalVolume,
          cloudwatch.Metric.anomalyDetectionFor({
            metric: signalVolume,
            stdDevs: signalVolumeStdDevs,
            label: 'Expected band',
            period: cdk.Duration.minutes(15),
          }),
        ],
        width: 8,
        height: 6,
        leftYAxis: { label: 'Rows', showUnits: false },
      }),
      new cloudwatch.GraphWidget({
        title: 'Fraud Signals by Type (hourly)',
        left: [
          new cloudwatch.MathExpression({
            expression: `SEARCH('{${metricNamespace},SignalType} MetricName="FraudSignalsByType"', 'Sum', 3600)`,
            label: '',
            period: cdk.Duration.hours(1),
          }),
        ],
        width: 8,
        height: 6,
        leftYAxis: { label: 'Rows', showUnits: false },
      }),
      new cloudwatch.GraphWidget({
        title: 'Risk Score and Credit Problems (hourly)',
        left: [
          hourlySum('RiskScoreUnavailable', 'Score unavailable'),
          hourlySum('RiskScoreTruncated', 'Score truncated'),
          hourlySum('RiskScoreUnknownRecord', 'Unknown record'),
          hourlySum('RiskHoldOrderMismatch', 'Hold order mismatch'),
          hourlySum('RiskTableUnconfigured', 'Table unconfigured'),
          hourlySum('CleanDeliveryCreditFailed', 'Clean delivery credit failed'),
        ],
        width: 8,
        height: 6,
        leftYAxis: { label: 'Lines', showUnits: false },
      }),
    );

    // ─── FRD-19: AI advisory reviewer errors ─────────────────────────
    // The hourly cron (fraud-advisory) and the Bedrock caller (fraud-ai-reviewer) in one alarm: a crashed or
    // timed-out run, or a review Bedrock refused at the API level (throttling, no model access, a wrong model id).
    // Model refusals and answers outside the schema are not errors (FRAUD_AI_REVIEW_FAILED in the cron's log).
    // Both are idle while the kill switch is off, so the alarm only speaks once aiReviewEnabled is on.
    const lambdaErrorsOf = (name: string, label: string) =>
      new cloudwatch.Metric({
        namespace: 'AWS/Lambda',
        metricName: 'Errors',
        dimensionsMap: { FunctionName: `${prefix}-${name}` },
        statistic: 'Sum',
        period: cdk.Duration.hours(1),
        label,
      });
    const fraudAdvisoryErrorsAlarm = new cloudwatch.Alarm(this, 'FraudAdvisoryErrors', {
      alarmName: `${prefix}-fraud-advisory-errors`,
      alarmDescription:
        'The AI advisory reviewer failed (fraud-advisory cron or fraud-ai-reviewer, FRD-19). Advice only: no money ' +
        'or gate depends on it. Read FRAUD_AI_REVIEW_FAILED in /aws/lambda/' + prefix + '-fraud-advisory; an ' +
        'AccessDeniedException means model access is not granted in the Bedrock console. To stop it at once, set ' +
        'the reviewer\'s reserved concurrency to 0 or turn aiReviewEnabled off (specs/decisions/frd-19-ai-advisory-reviewer.md).',
      metric: new cloudwatch.MathExpression({
        expression: 'cron + reviewer',
        usingMetrics: {
          cron: lambdaErrorsOf('fraud-advisory', 'Fraud advisory cron errors'),
          reviewer: lambdaErrorsOf('fraud-ai-reviewer', 'Fraud AI reviewer errors'),
        },
        period: cdk.Duration.hours(1),
        label: 'Fraud advisory errors',
      }),
      threshold: 1,
      evaluationPeriods: 1,
      comparisonOperator: cloudwatch.ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
      treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
    });
    fraudAdvisoryErrorsAlarm.addAlarmAction(snsAction);
    fraudAdvisoryErrorsAlarm.addOkAction(snsAction);

    // ─── FUP-142: outcome label write failures (FRD-18) ──────────────
    // OutcomeLabelWriter logs OUTCOME_LABEL_FAILED when a label row could not be written: from the
    // admin Lambda (a Fraud Review decision, a dispute verdict) and from the escrow Lambda (an admin
    // exit of a held escrow, EscrowService.LabelHeldExitAsync, which also logs a short form without
    // caseId). escrow-cron runs the same EscrowService settlements as the system actor, which writes
    // no label today; it is counted too, so a later change cannot fail there unseen. A plain term
    // with an underscore, so it matches behind the runtime prefix and client text cannot spell it.
    // No default: the line is rare and the metric is billed only in the hours it appears.
    const outcomeLabelLogGroups: Array<[string, logs.ILogGroup]> = [
      ['Admin', logs.LogGroup.fromLogGroupName(this, 'OutcomeLabelAdminLogGroup', `/aws/lambda/${prefix}-admin`)],
      ['Escrow', escrowLogGroup],
      ['EscrowCron', escrowCronLogGroup],
    ];
    for (const [key, logGroup] of outcomeLabelLogGroups) {
      riskLogFilter(`OutcomeLabelFailed${key}Filter`, logGroup, 'OUTCOME_LABEL_FAILED', 'OutcomeLabelFailed');
    }
    const outcomeLabelFailedAlarm = moneyLogAlarm(
      'OutcomeLabelFailed',
      'outcome-label-failed',
      'OutcomeLabelFailed',
      'Outcome Label Failed',
      'An outcome label row could not be written (OUTCOME_LABEL_FAILED case=<case> outcome=<outcome> ' +
        'caseId=<id> error=<exception type> in the admin or escrow log group; FRD-18, fraud spec section ' +
        '20.1). The decision stands: a Fraud Review decision, a dispute verdict or an admin exit of a held ' +
        'escrow is never failed or undone by its label. Only that case is missing from the outcome label ' +
        'export (scripts/export_outcome_labels.py). Usually throttling or IAM on the transaction-audit ' +
        'table: check dynamodb:PutItem on it and the TRANSACTION_AUDIT_TABLE variable of the admin and ' +
        'escrow Lambdas. A dispute verdict heals when the admin sends the same verdict again; a Fraud ' +
        'Review decision or a held exit is not repeated and has no backfill yet, so note the caseId and ' +
        'the time (FUP-142).',
    );

    // ─── FUP-158: feature snapshot failures (FRD-20 P1, FUP-144) ─────
    // Only a Fraud Review decision carries a snapshot, so only the admin Lambda reads one
    // (LabelFeatureSnapshotReader) or writes a label with one (OutcomeLabelWriter). Both log
    // LABEL_SNAPSHOT_FAILED and the label is written without it. The money alarm shape of outcome-label-failed.
    riskLogFilter('LabelSnapshotFailedAdminFilter', adminLogGroup, 'LABEL_SNAPSHOT_FAILED', 'LabelSnapshotFailed');
    const labelSnapshotFailedAlarm = moneyLogAlarm(
      'LabelSnapshotFailed',
      'label-snapshot-failed',
      'LabelSnapshotFailed',
      'Label Snapshot Failed',
      'A Fraud Review decision was labelled without its feature snapshot (LABEL_SNAPSHOT_FAILED in the admin log ' +
        'group; FRD-20 P1, fraud spec section 20.1). userId=<id> error=<type>: a snapshot read failed (the ' +
        'user\'s fraud-signals rows, the users row or the role\'s escrow index). caseId=<id> stage=write ' +
        'error=<type>: the label row was refused with its snapshot and written again without it. The decision ' +
        'and its label stand; only that FRD-20 training example lacks its features, and nothing backfills it. ' +
        'Usually throttling or IAM on those reads (dynamodb:Query on fraud-signals and the escrows party ' +
        'indexes, GetItem on users) or, for stage=write, the row\'s size. Fix the cause; a steady rate thins ' +
        'the FRD-20 dataset (FUP-158).',
    );

    // ─── FUP-201: attributed signals the guard held back (ESC-33) ────
    // FraudDetectionService.RecordAttributedSignalAsync logs FRAUD_ATTRIBUTED_SIGNAL_SKIPPED signalType=<type>
    // reason=<outcome> for each row onto another user it did not write (the trucker's GPS_ANOMALY, from sync;
    // since FUP-204 a farmer's NO_SHOW that rests on the trucker's or the self-pickup buyer's check-in, from
    // the reputation hooks in reputation-stream and order-cron's unpark replay; all behind reputationPenalties).
    // Not alarmed: a count per reason for the dashboard, where a rising SourceCapped or TargetCapped is one
    // party, or a ring, hammering another. A metric dimension needs a JSON or space-delimited pattern, which the
    // line behind the runtime prefix may not match (FUP-54), so one plain-text filter per reason and log group,
    // each reason its own metric without a default (billed only in the hours it appears). The reasons are the
    // AttributedSignalOutcome values the guard logs (an infra test reads them).
    const attributedSkipReasons = ['AlreadyRecorded', 'TargetUnread', 'SourceCapped', 'TargetCapped', 'HourLimited'];
    const attributedSkipLogGroups: Array<[string, logs.ILogGroup]> = [
      ['', syncLogGroup],
      ['Stream', reputationStreamLogGroup],
      ['OrderCron', orderCronLogGroup],
    ];
    for (const reason of attributedSkipReasons) {
      for (const [source, logGroup] of attributedSkipLogGroups) {
        new logs.MetricFilter(this, `FraudAttributedSkipped${reason}${source}Filter`, {
          logGroup,
          filterPattern: logs.FilterPattern.literal(`"FRAUD_ATTRIBUTED_SIGNAL_SKIPPED" "reason=${reason}"`),
          metricNamespace,
          metricName: `FraudAttributedSkipped${reason}`,
          metricValue: '1',
        });
      }
    }
    dashboard.addWidgets(
      new cloudwatch.GraphWidget({
        title: 'Attributed Signals Skipped by Reason (hourly)',
        left: attributedSkipReasons.map((reason) => hourlySum(`FraudAttributedSkipped${reason}`, reason)),
        width: 12,
        height: 6,
        leftYAxis: { label: 'Lines', showUnits: false },
      }),
    );

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
          // ESC-20 escrow ledger
          ledgerRowsOverdueAlarm,
          ledgerInvariantAlarm,
          adminQueueOverdueAlarm,
          disputeVerdictMismatchAlarm,
          disputeSettlementBlockedAlarm,
          disputePropagationFailedAlarm,
          escrowDeliveryStampFailuresAlarm,
          orderMoneyMismatchAlarm,
          riskSettlementSkippedAlarm,
          // FUP-231 order cancels and refunds the crons keep retrying
          orderDeadlineRefundStalledAlarm,
          // ESC-36 a delivery not made by its deadline (T8)
          deliveryOverdueAlarm,
          accountDeletionStuckAlarm,
          // ESC-21 order notification stream
          notifyDlqAlarm,
          notifyIteratorAgeAlarm,
          notifyStreamErrorsAlarm,
          // ESC-28 reputation stream, parked records and log lines
          reputationDlqAlarm,
          reputationParkedDlqAlarm,
          reputationIteratorAgeAlarm,
          reputationStreamErrorsAlarm,
          reputationPolicyInvalidAlarm,
          reputationEventsTruncatedAlarm,
          reputationReviewOverdueAlarm,
          reputationAppealOverdueAlarm,
          reputationCopyFailedAlarm,
          reputationParkFailedAlarm,
          // FUP-201 ESC-33 fraud hooks
          reputationFraudHookFailedAlarm,
          // FRD-16 fraud signal volume and risk alarms
          signalVolumeSpikeAlarm,
          riskScoreUnavailableAlarm,
          riskScoreTruncatedAlarm,
          riskScoreUnknownRecordAlarm,
          riskHoldOrderMismatchAlarm,
          riskTableUnconfiguredAlarm,
          // FRD-19 AI advisory reviewer
          fraudAdvisoryErrorsAlarm,
          // FUP-142 outcome label write failures, FUP-158 their feature snapshots
          outcomeLabelFailedAlarm,
          labelSnapshotFailedAlarm,
          ...(orderPaymentProblemsAlarm ? [orderPaymentProblemsAlarm] : []),
          ...(webhook5xxAlarm ? [webhook5xxAlarm] : []),
          ...(webhookErrorsAlarm ? [webhookErrorsAlarm] : []),
          ...(webhookThrottlesAlarm ? [webhookThrottlesAlarm] : []),
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
