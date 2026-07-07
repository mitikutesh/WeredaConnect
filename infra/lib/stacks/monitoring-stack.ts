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
 * Cost: ~$4/month (1 dashboard @ $3 + 6 alarms @ $0.10 each + metric filters free).
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
          `**Region:** ${envConfig.region} | **Cost:** ~$4/month | **Updated:** S2-04 Phase 2`,
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
