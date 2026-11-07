import * as fs from 'fs';
import * as path from 'path';
import * as cdk from 'aws-cdk-lib';
import { Template, Match } from 'aws-cdk-lib/assertions';
import { MonitoringStack } from '../../lib/stacks/monitoring-stack';
import { environments } from '../../lib/config/environments';

// FRD-16: a metric per fraud signal type, an anomaly alarm on the total signal volume, and the risk
// alarms of FUP-62 (fraud spec section 23.1). The helpers of stack.test.ts are repeated here on
// purpose (sibling branches edit that file). The deployed patterns are checked against the real
// service by scripts/fraud_alarms_synthetic.sh.

const devConfig = environments['dev'];
const lg = (suffix: string) => `/aws/lambda/wereda-connect-dev-${suffix}`;
const WEBHOOK_LG = lg('telebirr-webhook');

// The exact lines the server writes (FraudDetectionService.SignalRecordedLine, its risk score lines,
// MoneyAlarmLog.RiskHoldOrderMismatch and RiskTableUnconfigured, EscrowCronFunction's credit lines).
const SIGNAL_LINE = '{"event":"FRAUD_SIGNAL_RECORDED","signalType":"ROLE_FORBIDDEN","entityType":"crop"}';
const UNAVAILABLE_LINES = [
  'RISK_SCORE_UNAVAILABLE userId=3f2a-user error=ProvisionedThroughputExceededException',
  'RISK_SCORE_UNAVAILABLE userId=- error=EmptyUserId',
];
const TRUNCATED_LINE = 'RISK_SCORE_TRUNCATED userId=3f2a-user pages=2';
const UNKNOWN_RECORD_LINE = 'RISK_SCORE_UNKNOWN_RECORD userId=3f2a-user rows=3 recordType=badge';
const HOLD_MISMATCH_LINE = 'RISK_HOLD_ORDER_MISMATCH escrowId=escrow-order-ord-1 heldFor=refund legs=order:in-transit';
const TABLE_UNCONFIGURED_LINE = 'RISK_TABLE_UNCONFIGURED action=auto_release_and_refund';
const CREDIT_FAILED_LINES = [
  'CLEAN_DELIVERY_CREDIT_FAILED escrowId=escrow-order-ord-1 userId=3f2a-user error=TimeoutException',
  'CLEAN_DELIVERY_CREDIT_FAILED escrowId=escrow-order-ord-1 error=AmazonDynamoDBException',
];
// The Lambda runtime's prefix: time, request id and level, tab separated.
const prefixed = (level: string, line: string) => `2026-11-07T10:00:00.000Z\t4f1c7a2e-req\t${level}\t${line}`;

// Neighbouring lines of the same log groups that no FRD-16 filter may count.
const NEIGHBOUR_LINES = [
  'RISK_SETTLEMENT_SKIPPED escrowId=escrow-order-ord-1 sweep=auto-refund reason=score_unavailable holdReason=-',
  'ESCROW_FRAUD_HOLD escrowId=escrow-order-ord-1 holdReason=risk_critical heldFor=refund sweep=auto-refund',
  'ESCROW_FRAUD_HOLD_LOST escrowId=escrow-order-ord-1 outcome=OrderChanged sweep=auto-refund; the next run decides again.',
  'CLEAN_DELIVERY_CREDITED escrowId=escrow-order-ord-1 credited=3 alreadyCredited=0 failed=0',
  'CLEAN_DELIVERY_NOT_CREDITED escrowId=escrow-order-ord-1 reason=signal-on-order',
  'Failed to record fraud signal SUSPENDED_TOKEN_USE for user 3f2a-user: [TimeoutException]',
  'Failed to record fraud signal DISPUTE_RATE for escrow escrow-order-ord-1: [TimeoutException]',
  'ESCROW_HELD_REFUND_VERDICT_WRITE_FAILED escrowId=escrow-order-ord-1 error=TimeoutException',
  '{"timestamp":"2026-11-07T10:00:00Z","category":"SECURITY","action":"REFRESH_BLOCKED","userId":"3f2a-user","outcome":"failure"}',
  'ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-1 action=release_failed outcome=failed code=TELEBIRR_TIMEOUT',
];

type MetricFilterResource = {
  Properties: {
    LogGroupName: string;
    FilterPattern: string;
    MetricTransformations: Array<{
      MetricName: string;
      MetricNamespace: string;
      MetricValue: string;
      DefaultValue?: number;
      Dimensions?: Array<{ Key: string; Value: string }>;
    }>;
  };
};

function monitoringTemplate(enableTelebirrLive: boolean): Template {
  const app = new cdk.App();
  const stack = new MonitoringStack(app, 'FraudAlarmsMonitoring', {
    env: { account: '123456789012', region: devConfig.region },
    envConfig: { ...devConfig, enableTelebirrLive },
    apiUrl: `https://test.execute-api.${devConfig.region}.amazonaws.com`,
    apiId: 'testapi123',
  });
  return Template.fromStack(stack);
}

function filtersPublishing(template: Template, metricName: string): MetricFilterResource[] {
  return (Object.values(template.findResources('AWS::Logs::MetricFilter')) as MetricFilterResource[]).filter(
    (f) => f.Properties.MetricTransformations[0].MetricName === metricName,
  );
}

function logGroupsOf(filters: MetricFilterResource[]): string[] {
  return filters.map((f) => f.Properties.LogGroupName).sort();
}

// The same minimal model of CloudWatch Logs term matching as stack.test.ts: a bare or quoted term
// matches a whole token of the line, `?` makes the terms alternatives, a quoted term with other
// characters matches as a substring.
function cloudWatchTermsMatch(pattern: string, line: string): boolean {
  const tokens = pattern.match(/\??"[^"]*"|\??\S+/g) ?? [];
  const words = new Set(line.split(/[^A-Za-z0-9_]+/));
  const anyOf = tokens.some((t) => t.startsWith('?'));
  const hits = tokens.map((t) => {
    const term = t.replace(/^\?/, '').replace(/^"|"$/g, '');
    return /^[A-Za-z0-9_]+$/.test(term) ? words.has(term) : line.includes(term);
  });
  return anyOf ? hits.some(Boolean) : hits.every(Boolean);
}

function filterTerms(pattern: string): string[] {
  return (pattern.match(/\??"[^"]*"|\??\S+/g) ?? []).map((t) => t.replace(/^\?/, '').replace(/^"|"$/g, ''));
}

// A minimal model of a CloudWatch JSON filter of the form { $.a = "v" }: the event is a JSON object
// whose field a equals v. It reads the bare line only; whether the service also reads a JSON line
// behind the runtime prefix is checked against the real service by the synthetic script, and the
// alarm does not depend on it (the total is a plain term).
function jsonFilterMatch(pattern: string, line: string): Record<string, unknown> | undefined {
  const m = /^\{ \$\.(\w+) = "([^"]*)" \}$/.exec(pattern);
  if (!m) {
    throw new Error(`not a single-field JSON pattern: ${pattern}`);
  }
  try {
    const event = JSON.parse(line) as Record<string, unknown>;
    return event[m[1]] === m[2] ? event : undefined;
  } catch {
    return undefined;
  }
}

// An alarm whose metric carries a label renders as Metrics[0].MetricStat.
function customMetric(metricName: string, periodSeconds: number) {
  return Match.objectLike({
    MetricStat: Match.objectLike({
      Metric: Match.objectLike({ MetricName: metricName, Namespace: 'WeredaConnect/dev' }),
      Period: periodSeconds,
      Stat: 'Sum',
    }),
  });
}

// The dashboard body is a Fn::Join of JSON text around the region token; join it back and parse it.
type DashboardWidget = { properties: { title?: string; metrics?: unknown[] } };
function dashboardWidgets(template: Template): DashboardWidget[] {
  const dashboards = Object.values(template.findResources('AWS::CloudWatch::Dashboard')) as Array<{
    Properties: { DashboardBody: string | { 'Fn::Join': [string, unknown[]] } };
  }>;
  expect(dashboards).toHaveLength(1);
  const body = dashboards[0].Properties.DashboardBody;
  const text = typeof body === 'string' ? body : body['Fn::Join'][1].map((p) => (typeof p === 'string' ? p : 'TOKEN')).join('');
  return (JSON.parse(text) as { widgets: DashboardWidget[] }).widgets;
}

const notifiesTopic = Match.arrayWith([Match.objectLike({ Ref: Match.stringLikeRegexp('^AlarmTopic') })]);
const serverSource = (rel: string) => fs.readFileSync(path.join(__dirname, '../../../server/src', rel), 'utf8');

// The Lambdas whose handler builds a FraudDetectionService (the one writer of signal rows and reader
// of risk scores), mapped to their function names through api-stack.ts.
function fraudServiceLambdaLogGroups(): { always: string[]; liveOnly: string[] } {
  const functionsDir = path.join(__dirname, '../../../server/src/Functions');
  const handlers = new Set(
    fs
      .readdirSync(functionsDir)
      .filter((f) => f.endsWith('.cs'))
      .filter((f) => /new FraudDetectionService\(|\.RecordFraudSignalAsync\(/.test(fs.readFileSync(path.join(functionsDir, f), 'utf8')))
      .map((f) => f.split('.')[0]),
  );
  const apiStack = fs.readFileSync(path.join(__dirname, '../../lib/stacks/api-stack.ts'), 'utf8');
  const pairs = [...apiStack.matchAll(/functionName:\s*`\$\{prefix\}-([a-z0-9-]+)`[^}]*?handlerName:\s*"(\w+)"/g)];
  const always: string[] = [];
  const liveOnly: string[] = [];
  for (const handler of handlers) {
    const names = pairs.filter((p) => p[2] === handler).map((p) => p[1]);
    if (names.length === 0) {
      throw new Error(`no Lambda in api-stack.ts runs ${handler}`);
    }
    for (const name of names) {
      (name === 'telebirr-webhook' ? liveOnly : always).push(lg(name));
    }
  }
  return { always: always.sort(), liveOnly };
}

describe('FRD-16: fraud signal metrics', () => {
  test('every Lambda that builds a FraudDetectionService has both signal filters, the webhook only when live', () => {
    const { always, liveOnly } = fraudServiceLambdaLogGroups();
    // Today: sync, escrow, escrow-cron, auth-refresh, admin, webhook (and order-cron for FRD-15).
    expect(always).toEqual(expect.arrayContaining([lg('sync'), lg('escrow'), lg('escrow-cron'), lg('auth-refresh'), lg('admin')]));
    expect(liveOnly).toEqual([WEBHOOK_LG]);
    for (const live of [false, true]) {
      const template = monitoringTemplate(live);
      for (const metric of ['FraudSignalsRecorded', 'FraudSignalsByType']) {
        const groups = logGroupsOf(filtersPublishing(template, metric));
        // order-cron is counted ahead of FRD-15's relationship scan; nothing else.
        const expected = [...new Set([...always, lg('order-cron'), ...(live ? liveOnly : [])])].sort();
        expect([metric, live, groups]).toEqual([metric, live, expected]);
      }
    }
  });

  test('the total is a plain term with a zero default, so it counts the line bare or behind the runtime prefix', () => {
    const template = monitoringTemplate(true);
    const filters = filtersPublishing(template, 'FraudSignalsRecorded');
    expect(filters.length).toBe(7);
    for (const filter of filters) {
      const pattern = filter.Properties.FilterPattern;
      expect(pattern).toBe('"FRAUD_SIGNAL_RECORDED"');
      expect(filter.Properties.MetricTransformations[0]).toEqual(
        expect.objectContaining({ MetricValue: '1', DefaultValue: 0, MetricNamespace: 'WeredaConnect/dev' }),
      );
      expect(filter.Properties.MetricTransformations[0].Dimensions).toBeUndefined();
      for (const event of [SIGNAL_LINE, prefixed('info', SIGNAL_LINE)]) {
        expect([event, cloudWatchTermsMatch(pattern, event)]).toEqual([event, true]);
      }
      for (const line of [...NEIGHBOUR_LINES, ...UNAVAILABLE_LINES, TRUNCATED_LINE, UNKNOWN_RECORD_LINE, HOLD_MISMATCH_LINE]) {
        expect([line, cloudWatchTermsMatch(pattern, line)]).toEqual([line, false]);
      }
    }
  });

  test('the per-type metric takes the signal type as its one dimension, with no default (CloudWatch forbids both)', () => {
    const template = monitoringTemplate(true);
    const filters = filtersPublishing(template, 'FraudSignalsByType');
    expect(filters.length).toBe(7);
    for (const filter of filters) {
      const pattern = filter.Properties.FilterPattern;
      expect(pattern).toBe('{ $.event = "FRAUD_SIGNAL_RECORDED" }');
      const transformation = filter.Properties.MetricTransformations[0];
      expect(transformation.Dimensions).toEqual([{ Key: 'SignalType', Value: '$.signalType' }]);
      expect(transformation.DefaultValue).toBeUndefined();
      expect(transformation.MetricValue).toBe('1');
      expect(jsonFilterMatch(pattern, SIGNAL_LINE)?.signalType).toBe('ROLE_FORBIDDEN');
      // The audit JSON lines of the same log groups carry no event field.
      for (const line of NEIGHBOUR_LINES) {
        expect([line, jsonFilterMatch(pattern, line)]).toEqual([line, undefined]);
      }
    }
  });

  test('client text cannot spell the signal token once the server wrote it', () => {
    const pattern = filtersPublishing(monitoringTemplate(false), 'FraudSignalsRecorded')[0].Properties.FilterPattern;
    const defanged = 'FRAUD-SIGNAL-RECORDED';
    expect(filterTerms(pattern).every((t) => t.includes('_'))).toBe(true);
    for (const line of [
      `Op ${defanged}: status=error, msg=Invalid action: '${defanged}'. Must be create, update, or delete.`,
      `{"timestamp":"2026-11-07T10:00:00Z","category":"ESCROW","action":"DISPUTE","correlationId":"${defanged}","entityId":"${defanged}","outcome":"failure"}`,
      `Error getting escrow ${defanged}: [AmazonDynamoDBException]`,
    ]) {
      expect([line, cloudWatchTermsMatch(pattern, line)]).toEqual([line, false]);
      expect([line, line.includes('FRAUD_SIGNAL_RECORDED')]).toEqual([line, false]);
    }
  });

  test('no other filter of the stack counts the signal line', () => {
    const template = monitoringTemplate(true);
    const others = (Object.values(template.findResources('AWS::Logs::MetricFilter')) as MetricFilterResource[]).filter(
      (f) => !['FraudSignalsRecorded', 'FraudSignalsByType'].includes(f.Properties.MetricTransformations[0].MetricName),
    );
    expect(others.length).toBeGreaterThan(20);
    for (const filter of others) {
      const pattern = filter.Properties.FilterPattern;
      const counted = pattern.startsWith('{') ? false : cloudWatchTermsMatch(pattern, SIGNAL_LINE);
      expect([filter.Properties.MetricTransformations[0].MetricName, counted]).toEqual([
        filter.Properties.MetricTransformations[0].MetricName,
        false,
      ]);
    }
  });

  test('the server writes the line the filters read', () => {
    const service = serverSource('Services/FraudDetectionService.cs');
    expect(service).toContain('FraudSignalRecordedToken = "FRAUD_SIGNAL_RECORDED"');
    expect(service).toContain('AlarmLog(SignalRecordedLine(signalType, entityType));');
    expect(service).toContain('\\"signalType\\":');
    // The exact line is pinned by FraudDetectionServiceTests (RecordFraudSignal_LogsTheSignalLineOnceTheRowIsWritten).
  });
});

describe('FRD-16: the anomaly alarm on total signal volume', () => {
  type AlarmResource = { Properties: Record<string, unknown> & { AlarmName: string } };
  const alarmNamed = (template: Template, name: string, type = 'AWS::CloudWatch::Alarm'): AlarmResource => {
    const hit = (Object.values(template.findResources(type)) as AlarmResource[]).find((a) => a.Properties.AlarmName === name);
    if (!hit) {
      throw new Error(`no ${type} ${name}`);
    }
    return hit;
  };

  test('the anomaly child: band of 3 standard deviations on the 15 minute total, missing data not breaching, no actions', () => {
    for (const live of [false, true]) {
      const template = monitoringTemplate(live);
      template.hasResourceProperties('AWS::CloudWatch::Alarm', {
        AlarmName: 'wereda-connect-dev-fraud-signal-volume-anomaly',
        ComparisonOperator: 'GreaterThanUpperThreshold',
        EvaluationPeriods: 1,
        TreatMissingData: 'notBreaching',
        ThresholdMetricId: 'expr_1',
        Metrics: Match.arrayWith([
          Match.objectLike({ Id: 'expr_1', Expression: 'ANOMALY_DETECTION_BAND(m0, 3)', ReturnData: true }),
          Match.objectLike({
            Id: 'm0',
            ReturnData: true,
            MetricStat: Match.objectLike({
              Metric: Match.objectLike({ MetricName: 'FraudSignalsRecorded', Namespace: 'WeredaConnect/dev' }),
              Period: 900,
              Stat: 'Sum',
            }),
          }),
        ]),
      });
      const anomaly = alarmNamed(template, 'wereda-connect-dev-fraud-signal-volume-anomaly');
      expect(anomaly.Properties.AlarmActions).toBeUndefined();
      expect(anomaly.Properties.OKActions).toBeUndefined();
      expect(anomaly.Properties.Threshold).toBeUndefined();
    }
  });

  test('the floor child: at least 10 rows in the same 15 minutes, no actions', () => {
    const template = monitoringTemplate(false);
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'wereda-connect-dev-fraud-signal-volume-floor',
      Metrics: [customMetric('FraudSignalsRecorded', 900)],
      Threshold: 10,
      ComparisonOperator: 'GreaterThanOrEqualToThreshold',
      EvaluationPeriods: 1,
      TreatMissingData: 'notBreaching',
    });
    const floor = alarmNamed(template, 'wereda-connect-dev-fraud-signal-volume-floor');
    expect(floor.Properties.AlarmActions).toBeUndefined();
    expect(floor.Properties.OKActions).toBeUndefined();
  });

  test('the composite pages: both children in ALARM, to the existing alarm topic, ALARM and OK', () => {
    const template = monitoringTemplate(false);
    const composite = alarmNamed(template, 'wereda-connect-dev-fraud-signal-volume-spike', 'AWS::CloudWatch::CompositeAlarm');
    expect(composite.Properties.AlarmActions).toEqual([{ Ref: expect.stringMatching(/^AlarmTopic/) }]);
    expect(composite.Properties.OKActions).toEqual([{ Ref: expect.stringMatching(/^AlarmTopic/) }]);
    const rule = JSON.stringify(composite.Properties.AlarmRule);
    expect(rule).toMatch(/^\{"Fn::Join":\["",\["\(ALARM\(\\"",\{"Fn::GetAtt":\["FraudSignalVolumeAnomaly[0-9A-F]+","Arn"\]\},"\\"\) AND ALARM\(\\"",\{"Fn::GetAtt":\["FraudSignalVolumeFloor[0-9A-F]+","Arn"\]\},"\\"\)\)"\]\]\}$/);
    const description = String(composite.Properties.AlarmDescription ?? '');
    expect(description).toMatch(/FraudSignalsByType/);
    expect(description.length).toBeLessThanOrEqual(1024);
    // One composite: the spike is the only paging signal-volume alarm.
    template.resourceCountIs('AWS::CloudWatch::CompositeAlarm', 1);
  });

  test('the dashboard shows the total with its 15 minute band, the types by SEARCH, and the spike in the alarm status', () => {
    const template = monitoringTemplate(false);
    const metricsOf = (title: string): unknown[] => {
      const widget = dashboardWidgets(template).find((w) => w.properties.title === title);
      if (!widget) {
        throw new Error(`no dashboard widget ${title}`);
      }
      return widget.properties.metrics ?? [];
    };
    expect(metricsOf('Fraud Signals Recorded (15 min, anomaly band)')).toEqual([
      ['WeredaConnect/dev', 'FraudSignalsRecorded', expect.objectContaining({ id: 'm0', period: 900, stat: 'Sum' })],
      [expect.objectContaining({ expression: 'ANOMALY_DETECTION_BAND(m0, 3)', period: 900 })],
    ]);
    expect(metricsOf('Fraud Signals by Type (hourly)')).toEqual([
      [{ expression: 'SEARCH(\'{WeredaConnect/dev,SignalType} MetricName="FraudSignalsByType"\', \'Sum\', 3600)', period: 3600 }],
    ]);
    expect(metricsOf('Risk Score and Credit Problems (hourly)').map((m) => (m as unknown[])[1])).toEqual([
      'RiskScoreUnavailable', 'RiskScoreTruncated', 'RiskScoreUnknownRecord', 'RiskHoldOrderMismatch',
      'RiskTableUnconfigured', 'CleanDeliveryCreditFailed',
    ]);
    const body = JSON.stringify(template.findResources('AWS::CloudWatch::Dashboard'));
    expect(body).toMatch(/"Fn::GetAtt":\["FraudSignalVolumeSpike[0-9A-F]+","Arn"\]/);
  });
});

describe('FRD-16: risk alarms (FUP-62)', () => {
  // token | metric | alarm | the log groups that count it | its sample lines
  const riskScoreGroups = (live: boolean) => [
    ...fraudServiceLambdaLogGroups().always,
    lg('order-cron'),
    ...(live ? [WEBHOOK_LG] : []),
  ];
  const cases = (live: boolean): Array<[string, string, string, string[], string[]]> => [
    ['RISK_SCORE_UNAVAILABLE', 'RiskScoreUnavailable', 'risk-score-unavailable', riskScoreGroups(live), UNAVAILABLE_LINES],
    ['RISK_SCORE_TRUNCATED', 'RiskScoreTruncated', 'risk-score-truncated', riskScoreGroups(live), [TRUNCATED_LINE]],
    ['RISK_SCORE_UNKNOWN_RECORD', 'RiskScoreUnknownRecord', 'risk-score-unknown-record', riskScoreGroups(live), [UNKNOWN_RECORD_LINE]],
    ['RISK_HOLD_ORDER_MISMATCH', 'RiskHoldOrderMismatch', 'risk-hold-order-mismatch', [lg('escrow')], [HOLD_MISMATCH_LINE]],
    ['RISK_TABLE_UNCONFIGURED', 'RiskTableUnconfigured', 'risk-table-unconfigured', [lg('escrow-cron')], [TABLE_UNCONFIGURED_LINE]],
  ];
  const allRiskLines = [
    ...UNAVAILABLE_LINES,
    TRUNCATED_LINE,
    UNKNOWN_RECORD_LINE,
    HOLD_MISMATCH_LINE,
    TABLE_UNCONFIGURED_LINE,
    ...CREDIT_FAILED_LINES,
  ];

  test.each([false, true])('each token is counted on its log groups, bare and prefixed, and nowhere else (live %s)', (live) => {
    const template = monitoringTemplate(live);
    for (const [token, metric, , groups, lines] of cases(live)) {
      const filters = filtersPublishing(template, metric);
      expect([metric, logGroupsOf(filters)]).toEqual([metric, [...new Set(groups)].sort()]);
      for (const filter of filters) {
        const pattern = filter.Properties.FilterPattern;
        expect(pattern).toBe(`"${token}"`);
        // Rare lines: no default, so the metric is billed only in the hours one appears.
        expect(filter.Properties.MetricTransformations[0]).toEqual(
          expect.not.objectContaining({ DefaultValue: expect.anything() }),
        );
        for (const line of lines) {
          for (const event of [line, prefixed('info', line), prefixed('error', line)]) {
            expect([metric, event, cloudWatchTermsMatch(pattern, event)]).toEqual([metric, event, true]);
          }
        }
        for (const line of [...allRiskLines.filter((l) => !lines.includes(l)), ...NEIGHBOUR_LINES, SIGNAL_LINE]) {
          expect([metric, line, cloudWatchTermsMatch(pattern, line)]).toEqual([metric, line, false]);
        }
      }
    }
  });

  test('a credit that could not be written is a metric on escrow-cron, not an alarm (FRD-10)', () => {
    const template = monitoringTemplate(false);
    const filters = filtersPublishing(template, 'CleanDeliveryCreditFailed');
    expect(logGroupsOf(filters)).toEqual([lg('escrow-cron')]);
    const pattern = filters[0].Properties.FilterPattern;
    for (const line of CREDIT_FAILED_LINES) {
      expect(cloudWatchTermsMatch(pattern, prefixed('error', line))).toBe(true);
    }
    for (const line of NEIGHBOUR_LINES) {
      expect([line, cloudWatchTermsMatch(pattern, line)]).toEqual([line, false]);
    }
    const alarms = JSON.stringify(template.findResources('AWS::CloudWatch::Alarm'));
    expect(alarms).not.toContain('CleanDeliveryCreditFailed');
  });

  test.each([false, true])('each risk alarm: any line in 15 minutes, held 5 periods, notifies the topic, on the dashboard (live %s)', (live) => {
    const template = monitoringTemplate(live);
    const dashboard = JSON.stringify(template.findResources('AWS::CloudWatch::Dashboard'));
    for (const [token, metric, alarm] of cases(live)) {
      template.hasResourceProperties('AWS::CloudWatch::Alarm', {
        AlarmName: `wereda-connect-dev-${alarm}`,
        Metrics: [customMetric(metric, 900)],
        Threshold: 0,
        ComparisonOperator: 'GreaterThanThreshold',
        EvaluationPeriods: 5,
        DatapointsToAlarm: 1,
        TreatMissingData: 'notBreaching',
        AlarmActions: notifiesTopic,
        OKActions: notifiesTopic,
        AlarmDescription: Match.stringLikeRegexp(token),
      });
      expect(dashboard).toMatch(new RegExp(`"Fn::GetAtt":\\["${metric}[0-9A-F]+","Arn"\\]`));
    }
  });

  test('every alarm description of the stack still fits the CloudWatch limit', () => {
    for (const live of [false, true]) {
      const template = monitoringTemplate(live);
      for (const type of ['AWS::CloudWatch::Alarm', 'AWS::CloudWatch::CompositeAlarm']) {
        for (const alarm of Object.values(template.findResources(type)) as Array<{ Properties: { AlarmName: string; AlarmDescription?: string } }>) {
          expect([alarm.Properties.AlarmName, (alarm.Properties.AlarmDescription ?? '').length <= 1024]).toEqual([
            alarm.Properties.AlarmName,
            true,
          ]);
        }
      }
    }
  });

  test('every risk term carries an underscore, and defanged client text matches none', () => {
    const template = monitoringTemplate(true);
    const patterns = cases(true).flatMap(([, metric]) => filtersPublishing(template, metric).map((f) => f.Properties.FilterPattern));
    const hostile = [...new Set(patterns.flatMap(filterTerms))].join('-');
    expect(patterns.every((p) => cloudWatchTermsMatch(p, `Op ${hostile}: status=error`))).toBe(true);
    const defanged = hostile.replace(/_/g, '-');
    for (const pattern of patterns) {
      expect(filterTerms(pattern).every((t) => t.includes('_'))).toBe(true);
      for (const line of [
        `Op ${defanged}: status=error, msg=Invalid action: '${defanged}'. Must be create, update, or delete.`,
        `ESCROW_FRAUD_HOLD escrowId=escrow-order-${defanged} holdReason=risk_critical heldFor=refund sweep=auto-refund`,
        `RISK_SCORE_UNAVAILABLE`.replace(/_/g, '-') + ` userId=${defanged} error=-`,
      ]) {
        expect([pattern, line, cloudWatchTermsMatch(pattern, line)]).toEqual([pattern, line, false]);
      }
    }
  });

  test('the tokens are server constants and the producers write them', () => {
    const service = serverSource('Services/FraudDetectionService.cs');
    expect(service).toContain('RiskScoreUnavailableToken = "RISK_SCORE_UNAVAILABLE"');
    expect(service).toContain('RiskScoreTruncatedToken = "RISK_SCORE_TRUNCATED"');
    expect(service).toContain('RiskScoreUnknownRecordToken = "RISK_SCORE_UNKNOWN_RECORD"');
    const moneyAlarmLog = serverSource('Helpers/MoneyAlarmLog.cs');
    expect(moneyAlarmLog).toContain('RiskHoldOrderMismatchToken = "RISK_HOLD_ORDER_MISMATCH"');
    expect(moneyAlarmLog).toContain('RiskTableUnconfiguredToken = "RISK_TABLE_UNCONFIGURED"');
    expect(serverSource('Services/EscrowService.cs')).toContain('AlarmLog(MoneyAlarmLog.RiskHoldOrderMismatch(');
    const cron = serverSource('Functions/EscrowCronFunction.cs');
    expect(cron).toContain('MoneyAlarmLog.RiskTableUnconfigured(');
    expect(cron).toContain('CleanDeliveryCreditFailedToken = "CLEAN_DELIVERY_CREDIT_FAILED"');
  });
});
