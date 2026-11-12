import * as fs from 'fs';
import * as path from 'path';
import * as cdk from 'aws-cdk-lib';
import { Template, Match } from 'aws-cdk-lib/assertions';
import { MonitoringStack } from '../../lib/stacks/monitoring-stack';
import { environments } from '../../lib/config/environments';

// FUP-158, FUP-163, FUP-176, FUP-201 and FUP-231: log lines that nothing counted, and one alarm text.
// The helpers of stack.test.ts are repeated here on purpose (sibling branches edit that file). Every
// filter is a plain-text term (FUP-54: a JSON or space-delimited pattern may not match a line behind the
// Lambda runtime prefix), checked here against the server's exact lines and against server source.

const devConfig = environments['dev'];
const lg = (suffix: string) => `/aws/lambda/wereda-connect-dev-${suffix}`;

// The Lambda runtime's prefix: time, request id and level, tab separated.
const prefixed = (level: string, line: string) => `2026-11-12T10:00:00.000Z\t4f1c7a2e-req\t${level}\t${line}`;

type MetricFilterResource = {
  Properties: {
    LogGroupName: string;
    FilterPattern: string;
    MetricTransformations: Array<{ MetricName: string; MetricNamespace: string; MetricValue: string; DefaultValue?: number }>;
  };
};
type AlarmResource = { Properties: Record<string, unknown> & { AlarmName: string; AlarmDescription?: string } };

function monitoringTemplate(enableTelebirrLive: boolean): Template {
  const app = new cdk.App();
  const stack = new MonitoringStack(app, 'ObsAlarmsMonitoring', {
    env: { account: '123456789012', region: devConfig.region },
    envConfig: { ...devConfig, enableTelebirrLive },
    apiUrl: `https://test.execute-api.${devConfig.region}.amazonaws.com`,
    apiId: 'testapi123',
  });
  return Template.fromStack(stack);
}

function allFilters(template: Template): MetricFilterResource[] {
  return Object.values(template.findResources('AWS::Logs::MetricFilter')) as MetricFilterResource[];
}

function filtersPublishing(template: Template, metricName: string): MetricFilterResource[] {
  return allFilters(template).filter((f) => f.Properties.MetricTransformations[0].MetricName === metricName);
}

function alarmNamed(template: Template, name: string): AlarmResource {
  const hit = (Object.values(template.findResources('AWS::CloudWatch::Alarm')) as AlarmResource[]).find(
    (a) => a.Properties.AlarmName === name,
  );
  if (!hit) {
    throw new Error(`no alarm ${name}`);
  }
  return hit;
}

// The same minimal model of CloudWatch Logs term matching as stack.test.ts: a bare or quoted term
// matches a whole token of the line, `?` makes the terms alternatives, a quoted term with other
// characters matches as a substring. A JSON pattern matches no plain-text line.
function cloudWatchTermsMatch(pattern: string, line: string): boolean {
  if (pattern.startsWith('{')) {
    return false;
  }
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
type DashboardWidget = { type: string; properties: { title?: string; metrics?: unknown[]; alarms?: unknown[] } };
function dashboardWidgets(template: Template): DashboardWidget[] {
  const dashboards = Object.values(template.findResources('AWS::CloudWatch::Dashboard')) as Array<{
    Properties: { DashboardBody: string | { 'Fn::Join': [string, unknown[]] } };
  }>;
  expect(dashboards).toHaveLength(1);
  const body = dashboards[0].Properties.DashboardBody;
  const text = typeof body === 'string' ? body : body['Fn::Join'][1].map((p) => (typeof p === 'string' ? p : 'TOKEN')).join('');
  return (JSON.parse(text) as { widgets: DashboardWidget[] }).widgets;
}

function onAlarmStatus(template: Template, logicalIdPrefix: string): boolean {
  const body = JSON.stringify(template.findResources('AWS::CloudWatch::Dashboard'));
  return new RegExp(`"Fn::GetAtt":\\["${logicalIdPrefix}[0-9A-F]+","Arn"\\]`).test(body);
}

const notifiesTopic = Match.arrayWith([Match.objectLike({ Ref: Match.stringLikeRegexp('^AlarmTopic') })]);

const serverDir = path.join(__dirname, '../../../server/src');
const serverSource = (rel: string) => fs.readFileSync(path.join(serverDir, rel), 'utf8');

/** The server/src files (relative, with forward slashes) whose text matches, sorted. */
function serverFilesMatching(pattern: RegExp, exclude: string[] = []): string[] {
  return fs
    .readdirSync(serverDir, { recursive: true, encoding: 'utf8' })
    .filter((f) => f.endsWith('.cs'))
    .map((f) => f.split(path.sep).join('/'))
    .filter((f) => !exclude.includes(f))
    .filter((f) => pattern.test(serverSource(f)))
    .sort();
}

/** The log groups of the Lambdas that run these handler files (Functions/<Handler>[.<part>].cs), through api-stack.ts. */
function logGroupsOfHandlerFiles(files: string[]): string[] {
  const apiStack = fs.readFileSync(path.join(__dirname, '../../lib/stacks/api-stack.ts'), 'utf8');
  const pairs = [...apiStack.matchAll(/functionName:\s*`\$\{prefix\}-([a-z0-9-]+)`[^}]*?handlerName:\s*"(\w+)"/g)];
  const groups = new Set<string>();
  for (const file of files) {
    const handler = path.basename(file).split('.')[0];
    const names = pairs.filter((p) => p[2] === handler).map((p) => p[1]);
    if (names.length === 0) {
      throw new Error(`no Lambda in api-stack.ts runs ${handler} (${file})`);
    }
    names.forEach((name) => groups.add(lg(name)));
  }
  return [...groups].sort();
}

/** One filter per log group for the metric, the plain pattern, no default: billed only in the hours a line appears. */
function expectRareTokenFilters(template: Template, metricName: string, pattern: string, logGroups: string[]): MetricFilterResource[] {
  const filters = filtersPublishing(template, metricName);
  expect([metricName, filters.map((f) => f.Properties.LogGroupName).sort()]).toEqual([metricName, [...logGroups].sort()]);
  for (const filter of filters) {
    expect([metricName, filter.Properties.FilterPattern]).toEqual([metricName, pattern]);
    expect(filter.Properties.MetricTransformations[0]).toEqual({
      MetricName: metricName,
      MetricNamespace: 'WeredaConnect/dev',
      MetricValue: '1',
    });
  }
  return filters;
}

function expectMatches(pattern: string, lines: string[], neighbours: string[]): void {
  for (const line of lines) {
    for (const event of [line, prefixed('warn', line), prefixed('error', line), prefixed('info', line)]) {
      expect([pattern, event, cloudWatchTermsMatch(pattern, event)]).toEqual([pattern, event, true]);
    }
  }
  for (const line of neighbours) {
    expect([pattern, line, cloudWatchTermsMatch(pattern, line)]).toEqual([pattern, line, false]);
  }
}

/** Client text that spells every term, defanged the way the server logs ids and text (_ becomes -), matches nothing. */
function expectDefangedTextMatchesNothing(patterns: string[]): void {
  const hostile = [...new Set(patterns.flatMap(filterTerms))].join('-');
  const defanged = hostile.replace(/_/g, '-');
  for (const pattern of patterns) {
    expect([pattern, filterTerms(pattern).some((t) => t.includes('_'))]).toEqual([pattern, true]);
    for (const line of [
      `Op ${defanged}: status=error, msg=Invalid action: '${defanged}'.`,
      `Commitment deadlines: order ${defanged} changed since it was read (OrderChanged); ${defanged} not applied this run.`,
      `Error processing operation ${defanged}: [FormatException] The input string '${defanged}' was not in a correct format.`,
    ]) {
      expect([pattern, line, cloudWatchTermsMatch(pattern, line)]).toEqual([pattern, line, false]);
    }
  }
}

describe('FUP-158: a fraud label written without its feature snapshot', () => {
  // OutcomeLabelWriter.WriteAsync (stage=write) and LabelFeatureSnapshotReader.ReadAsync.
  const LINES = [
    'LABEL_SNAPSHOT_FAILED userId=3f2a-user error=ProvisionedThroughputExceededException',
    'LABEL_SNAPSHOT_FAILED caseId=sig-1 stage=write error=AmazonDynamoDBException',
  ];
  const NEIGHBOURS = [
    'OUTCOME_LABEL_FAILED case=fraud_signal outcome=fraud_confirmed caseId=sig-1 error=TimeoutException',
    'OUTCOME_LABEL_WRITTEN case=fraud_signal outcome=false_positive caseId=sig-1',
    'OUTCOME_LABEL_EXISTS case=fraud_signal outcome=whitelisted caseId=sig-1',
    'RISK_SCORE_UNAVAILABLE userId=3f2a-user error=ProvisionedThroughputExceededException',
    'ESCROW_LEDGER_INVARIANT escrowId=escrow-order-ord-1 ref=release#farmer check=I1',
  ];

  test.each([false, true])('one filter, on the admin log group, bare and prefixed (live %s)', (live) => {
    const template = monitoringTemplate(live);
    const [filter] = expectRareTokenFilters(template, 'LabelSnapshotFailed', '"LABEL_SNAPSHOT_FAILED"', [lg('admin')]);
    expectMatches(filter.Properties.FilterPattern, LINES, NEIGHBOURS);
    // No other filter of the stack counts a snapshot line.
    for (const other of allFilters(template).filter((f) => f.Properties.MetricTransformations[0].MetricName !== 'LabelSnapshotFailed')) {
      const metric = other.Properties.MetricTransformations[0].MetricName;
      expect([metric, LINES.some((line) => cloudWatchTermsMatch(other.Properties.FilterPattern, line))]).toEqual([metric, false]);
    }
    expectDefangedTextMatchesNothing([filter.Properties.FilterPattern]);
  });

  test.each([false, true])('alarm: the shape of outcome-label-failed, to the alarm topic, on the dashboard (live %s)', (live) => {
    const template = monitoringTemplate(live);
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'wereda-connect-dev-label-snapshot-failed',
      Metrics: [customMetric('LabelSnapshotFailed', 900)],
      Threshold: 0,
      ComparisonOperator: 'GreaterThanThreshold',
      EvaluationPeriods: 5,
      DatapointsToAlarm: 1,
      TreatMissingData: 'notBreaching',
      AlarmActions: notifiesTopic,
      OKActions: notifiesTopic,
      AlarmDescription: Match.stringLikeRegexp('LABEL_SNAPSHOT_FAILED'),
    });
    const description = alarmNamed(template, 'wereda-connect-dev-label-snapshot-failed').Properties.AlarmDescription ?? '';
    expect(description).toContain('stage=write');
    expect(description.length).toBeLessThanOrEqual(1024);
    expect(onAlarmStatus(template, 'LabelSnapshotFailed')).toBe(true);
  });

  test('the server writes the token, and only the admin Lambda takes a snapshot', () => {
    expect(serverSource('Helpers/OutcomeLabelWriter.cs')).toContain('SnapshotFailedToken = "LABEL_SNAPSHOT_FAILED"');
    expect(serverSource('Helpers/OutcomeLabelWriter.cs')).toContain(
      '$"{OutcomeLabels.SnapshotFailedToken} caseId={MoneyAlarmLog.LogId(label.CaseId)} stage=write error={ex.GetType().Name}"',
    );
    const reader = serverSource('Services/LabelFeatureSnapshotReader.cs');
    expect(reader).toContain('FailedToken = OutcomeLabels.SnapshotFailedToken');
    expect(reader).toContain('$"{FailedToken} userId={MoneyAlarmLog.LogId(userId)} error={MoneyAlarmLog.LogId(ex.GetType().Name)}"');
    // The reader is built and a snapshot is put on a label only in the admin Lambda. A new caller elsewhere
    // needs its log group added to the filter.
    expect(serverFilesMatching(/new LabelFeatureSnapshotReader\(|Snapshot = snapshot/)).toEqual(['Functions/AdminFunction.cs']);
    expect(logGroupsOfHandlerFiles(['Functions/AdminFunction.cs'])).toEqual([lg('admin')]);
  });
});

describe('FUP-163: REPUTATION_COPY_FAILED from the admin Lambda', () => {
  const ADMIN_LINES = [
    'REPUTATION_COPY_FAILED step=admin_suspend user=3f2a-user [ProvisionedThroughputExceededException]',
    'REPUTATION_COPY_FAILED step=admin_unsuspend_refresh user=3f2a-user [TimeoutException]',
    'REPUTATION_COPY_FAILED step=refresh user=3f2a-user [AmazonDynamoDBException]',
  ];

  test('the filter covers every Lambda that refreshes the copies, the admin Lambda included', () => {
    const template = monitoringTemplate(false);
    // Every handler that builds a ReputationService with the copy repository, or logs the token itself.
    const writers = serverFilesMatching(/copies:\s*\S|ReputationService\.CopyFailedToken|ReputationCopyFailedToken/).filter((f) =>
      f.startsWith('Functions/'),
    );
    expect(writers).toEqual([
      'Functions/AdminFunction.Reputation.cs',
      'Functions/OrderCronFunction.cs',
      'Functions/ReputationStreamFunction.cs',
      'Functions/SyncFunction.Reputation.cs',
    ]);
    const groups = logGroupsOfHandlerFiles(writers);
    expect(groups).toEqual([lg('admin'), lg('order-cron'), lg('reputation-stream'), lg('sync')]);
    const filters = expectRareTokenFilters(template, 'ReputationCopyFailed', '"REPUTATION_COPY_FAILED"', groups);
    const admin = filters.find((f) => f.Properties.LogGroupName === lg('admin'))!;
    expectMatches(admin.Properties.FilterPattern, ADMIN_LINES, [
      'REPUTATION_PARK_FAILED reason=send [AmazonSQSException]',
      'RATING_AGGREGATE_HEAL_FAILED user=3f2a-user [TimeoutException]',
    ]);
  });

  test('the alarm keeps its rate shape and names the admin log group and its steps', () => {
    const template = monitoringTemplate(false);
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'wereda-connect-dev-reputation-copy-failed',
      Metrics: [customMetric('ReputationCopyFailed', 3600)],
      Threshold: 5,
      EvaluationPeriods: 1,
      ComparisonOperator: 'GreaterThanOrEqualToThreshold',
      AlarmActions: notifiesTopic,
      OKActions: notifiesTopic,
    });
    const description = alarmNamed(template, 'wereda-connect-dev-reputation-copy-failed').Properties.AlarmDescription ?? '';
    expect(description).toContain('sync or admin log group');
    expect(description).toContain('step=admin_suspend or admin_unsuspend');
    expect(description).toContain('reputation_stamp_owner_copies');
    expect(description.length).toBeLessThanOrEqual(1024);
  });

  test('the admin Lambda writes the token through ReputationService.CopyFailedToken', () => {
    expect(serverSource('Services/ReputationService.cs')).toContain('CopyFailedToken = "REPUTATION_COPY_FAILED"');
    const admin = serverSource('Functions/AdminFunction.Reputation.cs');
    expect(admin).toContain('$"{ReputationService.CopyFailedToken} step=admin_{action} user=');
    expect(admin).toContain('$"{ReputationService.CopyFailedToken} step=admin_{action}_refresh user=');
    // The order-cron action the alarm text names.
    expect(serverSource('Services/ReputationJobs.cs')).toContain('Action = "reputation_stamp_owner_copies"');
  });
});

describe('FUP-176: risk-settlement-skipped names both sources and the exits', () => {
  // MoneyAlarmLog.RiskSettlementSkipped from the settlement sweeps and (escrow-protocol 2.6.0) the payout gate.
  const PAYOUT_LINES = [
    'RISK_SETTLEMENT_SKIPPED escrowId=escrow-order-ord-1 sweep=payout-farmer reason=payee_flagged holdReason=account_suspended',
    'RISK_SETTLEMENT_SKIPPED escrowId=escrow-order-ord-1 sweep=payout-trucker reason=payee_flagged holdReason=risk_critical',
    'RISK_SETTLEMENT_SKIPPED escrowId=escrow-order-ord-1 sweep=payout-trucker reason=score_unavailable holdReason=-',
  ];

  test('the escrow-cron filter counts the payout gate lines too', () => {
    const template = monitoringTemplate(false);
    const filters = filtersPublishing(template, 'RiskSettlementSkipped');
    expect(filters.map((f) => f.Properties.LogGroupName)).toEqual([lg('escrow-cron')]);
    expectMatches(filters[0].Properties.FilterPattern, PAYOUT_LINES, [
      'ESCROW_FRAUD_HOLD escrowId=escrow-order-ord-1 holdReason=risk_critical heldFor=refund sweep=auto-refund',
      'TRUCKER_PAYOUT_OVERDUE escrowId=escrow-order-ord-1 reason=risk_critical ageHours=26',
    ]);
  });

  test('the description names the sweeps, the payout gate and the exits, within the CloudWatch limit', () => {
    const template = monitoringTemplate(false);
    const description = alarmNamed(template, 'wereda-connect-dev-risk-settlement-skipped').Properties.AlarmDescription ?? '';
    for (const text of [
      'RISK_SETTLEMENT_SKIPPED',
      'sweep=auto-release, auto-refund or dispute',
      'escrow-protocol 2.6.0',
      'sweep=payout-farmer or payout-trucker',
      'reason=flag_off',
      'payee_flagged',
      'holdReason=fraud_confirmed',
      'account_suspended',
      'risk_critical',
      'unsuspend the party',
      'review the signals behind the tier in Fraud Review',
      'user_read_failed or score_unavailable',
    ]) {
      expect([text, description.includes(text)]).toEqual([text, true]);
    }
    expect(description).not.toContain('one line per escrow per hourly run');
    expect(description.length).toBeLessThanOrEqual(1024);
  });

  test('the server logs both sources with the same token, and only escrow-cron pays as the system', () => {
    const moneyAlarmLog = serverSource('Helpers/MoneyAlarmLog.cs');
    expect(moneyAlarmLog).toContain('RiskSettlementSkippedToken = "RISK_SETTLEMENT_SKIPPED"');
    expect(moneyAlarmLog).toContain('$"{RiskSettlementSkippedToken} escrowId={LogId(escrowId)} sweep={Safe(sweep)} reason={Safe(reason)} holdReason={Safe(holdReason)}"');
    const gate = serverSource('Services/EscrowService.PayoutRiskGate.cs');
    expect(gate).toContain('PayeeFlaggedSkipReason = "payee_flagged"');
    expect(gate).toContain('AlarmLog(MoneyAlarmLog.RiskSettlementSkipped(');
    expect(gate).toContain('$"payout-{leg}"');
    // The risk half of the gate is wired in escrow-cron only, so its log group is the one to count.
    expect(serverFilesMatching(/PayoutRiskService = /)).toEqual(['Functions/EscrowCronFunction.cs']);
  });
});

describe('FUP-201: the ESC-33 lines nothing counted', () => {
  const HOOK_LINES = [
    'REPUTATION_FRAUD_HOOK_FAILED hook=reliability code=NO_SHOW [InvalidOperationException]',
    'REPUTATION_FRAUD_HOOK_FAILED hook=buyer_decline code=DECLINED_REQUEST [ProvisionedThroughputExceededException]',
  ];
  const REASONS = ['AlreadyRecorded', 'TargetUnread', 'SourceCapped', 'TargetCapped', 'HourLimited'];
  const skippedLine = (reason: string) => `FRAUD_ATTRIBUTED_SIGNAL_SKIPPED signalType=GPS_ANOMALY reason=${reason}`;
  const NEIGHBOURS = [
    'REPUTATION_COPY_FAILED step=record user=3f2a-user [TimeoutException]',
    'REPUTATION_PARK_FAILED reason=send [AmazonSQSException]',
    '{"event":"FRAUD_SIGNAL_RECORDED","signalType":"GPS_ANOMALY","entityType":"delivery"}',
    'Failed to record fraud signal GPS_ANOMALY for delivery del-1: [TimeoutException]',
  ];

  test('REPUTATION_FRAUD_HOOK_FAILED: a filter on each Lambda that records reputation events', () => {
    const template = monitoringTemplate(false);
    // The hooks run in ReputationService.RecordAsync, which only ReputationRecordProcessor calls.
    expect(serverFilesMatching(/ReputationFraudHooks\.AfterRecordedAsync\(/)).toEqual(['Services/ReputationService.cs']);
    const recorders = serverFilesMatching(/new ReputationRecordProcessor\(/);
    expect(recorders).toEqual(['Functions/OrderCronFunction.cs', 'Functions/ReputationStreamFunction.cs']);
    const groups = logGroupsOfHandlerFiles(recorders);
    expect(groups).toEqual([lg('order-cron'), lg('reputation-stream')]);
    const filters = expectRareTokenFilters(template, 'ReputationFraudHookFailed', '"REPUTATION_FRAUD_HOOK_FAILED"', groups);
    for (const filter of filters) {
      expectMatches(filter.Properties.FilterPattern, HOOK_LINES, [...NEIGHBOURS, ...REASONS.map(skippedLine)]);
    }
    expectDefangedTextMatchesNothing([filters[0].Properties.FilterPattern]);
  });

  test.each([false, true])('REPUTATION_FRAUD_HOOK_FAILED: any line alarms, like a failed park, to the topic (live %s)', (live) => {
    const template = monitoringTemplate(live);
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'wereda-connect-dev-reputation-fraud-hook-failed',
      Metrics: [customMetric('ReputationFraudHookFailed', 300)],
      Threshold: 1,
      EvaluationPeriods: 1,
      ComparisonOperator: 'GreaterThanOrEqualToThreshold',
      TreatMissingData: 'notBreaching',
      AlarmActions: notifiesTopic,
      OKActions: notifiesTopic,
      AlarmDescription: Match.stringLikeRegexp('REPUTATION_FRAUD_HOOK_FAILED'),
    });
    const description = alarmNamed(template, 'wereda-connect-dev-reputation-fraud-hook-failed').Properties.AlarmDescription ?? '';
    expect(description).toContain('hook=reliability');
    expect(description).toContain('hook=buyer_decline');
    expect(description.length).toBeLessThanOrEqual(1024);
    expect(onAlarmStatus(template, 'ReputationFraudHookFailed')).toBe(true);
  });

  test('FRAUD_ATTRIBUTED_SIGNAL_SKIPPED: one metric per reason on the log groups of its callers, each its own reason only', () => {
    const template = monitoringTemplate(false);
    for (const reason of REASONS) {
      const metric = `FraudAttributedSkipped${reason}`;
      // FUP-204: the reputation hooks write attributed rows too, in reputation-stream and order-cron's unpark.
      const [filter] = expectRareTokenFilters(template, metric, `"FRAUD_ATTRIBUTED_SIGNAL_SKIPPED" "reason=${reason}"`,
        [lg('sync'), lg('reputation-stream'), lg('order-cron')]);
      expectMatches(filter.Properties.FilterPattern, [skippedLine(reason)], [
        ...REASONS.filter((r) => r !== reason).map(skippedLine),
        ...NEIGHBOURS,
        ...HOOK_LINES,
        // The reason alone, without the token, is not the line.
        `GPS anomaly on delivery del-1 reason=${reason}`,
      ]);
    }
    expectDefangedTextMatchesNothing(REASONS.map((r) => `"FRAUD_ATTRIBUTED_SIGNAL_SKIPPED" "reason=${r}"`));
    // Not alarmed: a count for the dashboard.
    const alarms = JSON.stringify(template.findResources('AWS::CloudWatch::Alarm'));
    expect(alarms).not.toContain('FraudAttributedSkipped');
  });

  test('FRAUD_ATTRIBUTED_SIGNAL_SKIPPED: the dashboard counts the reasons hourly', () => {
    const template = monitoringTemplate(false);
    const widget = dashboardWidgets(template).find((w) => w.properties.title === 'Attributed Signals Skipped by Reason (hourly)');
    expect(widget).toBeDefined();
    expect(widget!.properties.metrics).toEqual(
      REASONS.map((reason) => [
        'WeredaConnect/dev',
        `FraudAttributedSkipped${reason}`,
        expect.objectContaining({ label: reason, period: 3600, stat: 'Sum' }),
      ]),
    );
  });

  test('the server writes both tokens as the filters read them', () => {
    const hooks = serverSource('Services/ReputationFraudHooks.cs');
    expect(hooks).toContain('FailedToken = "REPUTATION_FRAUD_HOOK_FAILED"');
    expect(hooks).toContain('$"{FailedToken} hook=reliability code={recorded.Code} [{ex.GetType().Name}]"');
    expect(hooks).toContain('$"{FailedToken} hook=buyer_decline code={recorded.Code} [{ex.GetType().Name}]"');

    const fraud = serverSource('Services/FraudDetectionService.cs');
    expect(fraud).toContain('AttributedSignalSkippedToken = "FRAUD_ATTRIBUTED_SIGNAL_SKIPPED"');
    expect(fraud).toContain('AlarmLog($"{AttributedSignalSkippedToken} signalType={MoneyAlarmLog.Safe(signalType)} reason={outcome}");');
    // The reasons the guard logs are exactly the metrics the stack publishes.
    const logged = [...new Set([...fraud.matchAll(/Skipped\(AttributedSignalOutcome\.(\w+)\)/g)].map((m) => m[1]))].sort();
    expect(logged).toEqual([...REASONS].sort());
    // The handlers that write attributed signals: sync (the trucker's GPS_ANOMALY, FUP-203 moved it into a
    // partial file of the sync handler) and, through the reputation hooks (FUP-204), every handler that
    // records a reputation event: the stream and order-cron's unpark replay.
    const callers = serverFilesMatching(/\.RecordAttributedSignalAsync\(/);
    expect(callers.filter((f) => f.startsWith('Functions/'))).toEqual(['Functions/SyncFunction.TruckerGpsEvidence.cs']);
    expect(callers.filter((f) => f.startsWith('Services/'))).toEqual(['Services/ReputationFraudHooks.cs']);
    const recorders = serverFilesMatching(/new ReputationRecordProcessor\(|new ReputationUnparkJob\(/).filter((f) => f.startsWith('Functions/'));
    expect(logGroupsOfHandlerFiles(['Functions/SyncFunction.TruckerGpsEvidence.cs', ...recorders]))
      .toEqual([lg('order-cron'), lg('reputation-stream'), lg('sync')]);
  });
});

describe('FUP-231: order cancels and refunds the crons keep retrying', () => {
  // OrderCronFunction.Deadlines.cs (DecideDueOrderAsync); FUP-210's OrderCronFunction.RefundWaiting (PR #359, not on
  // main yet: until it lands the term counts nothing); EscrowCronFunction (the restore after the sweep's own cancel,
  // and EnsureListingStockRestoredAsync before a refund) and DisputeVerdictMirror.RestoreStockAsync (escrow-cron runs it).
  const ORDER_CRON_LINES = [
    'ORDER_DEADLINE_ROW_FAILED orderId=ord-1 reason=[ProvisionedThroughputExceededException]; the rest of the run goes on.',
    'ORDER_DEADLINE_REFUND_WAITING orderId=ord-1 cancelReason=farmer_response_expired reason=escrow_busy: '
      + 'the refund could not be armed with the cancel, nothing written; looked at again in 1h.',
    'ORDER_DEADLINE_REFUND_WAITING orderId=ord-1 cancelReason=transport_not_arranged reason=escrow_changed: '
      + 'the refund could not be armed with the cancel, nothing written; looked at again in 1h.',
  ];
  const ESCROW_CRON_LINES = [
    'ORDER_STOCK_RESTORE_FAILED orderId=ord-1 listingId=listing-1 reason=restock:conflict; refund deferred to the next sweep.',
    'ORDER_STOCK_RESTORE_FAILED orderId=ord-1 listingId=listing-1: [TimeoutException]',
    'ORDER_STOCK_RESTORE_FAILED orderId=ord-1 listingId=listing-1 reason=restock:[AmazonDynamoDBException]',
  ];
  const NEIGHBOURS = [
    'ORDER_DEADLINE_STUCK orderId=ord-1 kind=farmer_response at=2026-11-12T09:00:00Z: due but nothing applied; looked at again in 1h.',
    'ORDER_DEADLINE_REPAIR_FAILED reason=[TimeoutException]; the next run tries again.',
    'ORDER_DEADLINE_STAMP_FAILED orderId=ord-1 reason=[TimeoutException]',
    'ORDER_ESCROW_REFUND_SCHEDULE_FAILED orderId=ord-1 reason=[TimeoutException]',
    'ORDER_STOCK_DECREMENT_FAILED orderId=ord-1 reason=insufficient_stock',
    'Listing listing-1: +2.5 quintals restored (order ord-1 cancelled by escrow refund)',
    'DISPUTE_VERDICT_SETTLEMENT_BLOCKED escrowId=escrow-order-ord-1 op=refund legs=restock:conflict',
    'Commitment deadlines complete: 1 farmer windows expired, 0 trucker windows expired, 0 conflicts, 1 trip reads.',
  ];

  test.each([false, true])('one metric from the order-cron and escrow-cron filters, bare and prefixed (live %s)', (live) => {
    const template = monitoringTemplate(live);
    const filters = filtersPublishing(template, 'OrderDeadlineRefundStalled');
    expect(filters.map((f) => f.Properties.LogGroupName).sort()).toEqual([lg('escrow-cron'), lg('order-cron')]);
    const patternOf = (group: string) => filters.find((f) => f.Properties.LogGroupName === group)!.Properties.FilterPattern;
    expect(patternOf(lg('order-cron'))).toBe('?"ORDER_DEADLINE_REFUND_WAITING" ?"ORDER_DEADLINE_ROW_FAILED"');
    expect(patternOf(lg('escrow-cron'))).toBe('"ORDER_STOCK_RESTORE_FAILED"');
    for (const filter of filters) {
      // No default: billed only in the hours a line appears; the alarm treats missing data as OK.
      expect(filter.Properties.MetricTransformations[0]).toEqual({
        MetricName: 'OrderDeadlineRefundStalled',
        MetricNamespace: 'WeredaConnect/dev',
        MetricValue: '1',
      });
    }
    expectMatches(patternOf(lg('order-cron')), ORDER_CRON_LINES, [...NEIGHBOURS, ...ESCROW_CRON_LINES]);
    expectMatches(patternOf(lg('escrow-cron')), ESCROW_CRON_LINES, [...NEIGHBOURS, ...ORDER_CRON_LINES]);
    expectDefangedTextMatchesNothing(filters.map((f) => f.Properties.FilterPattern));
    // No other filter of the stack counts these lines.
    for (const other of allFilters(template).filter((f) => f.Properties.MetricTransformations[0].MetricName !== 'OrderDeadlineRefundStalled')) {
      const metric = other.Properties.MetricTransformations[0].MetricName;
      const counted = [...ORDER_CRON_LINES, ...ESCROW_CRON_LINES].some((line) => cloudWatchTermsMatch(other.Properties.FilterPattern, line));
      expect([metric, counted]).toEqual([metric, false]);
    }
  });

  test.each([false, true])('alarm: a line in three of four hourly periods, to the alarm topic, on the dashboard (live %s)', (live) => {
    const template = monitoringTemplate(live);
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'wereda-connect-dev-order-deadline-refund-stalled',
      Metrics: [customMetric('OrderDeadlineRefundStalled', 3600)],
      Threshold: 0,
      ComparisonOperator: 'GreaterThanThreshold',
      // A retried row logs again within 75 minutes (the 15 minute sweep, the 1 hour retry, the hourly escrow-cron),
      // so a stuck one fills at least 3 of any 4 clock-hours; one failed run fills one.
      EvaluationPeriods: 4,
      DatapointsToAlarm: 3,
      TreatMissingData: 'notBreaching',
      AlarmActions: notifiesTopic,
      OKActions: notifiesTopic,
    });
    const description = alarmNamed(template, 'wereda-connect-dev-order-deadline-refund-stalled').Properties.AlarmDescription ?? '';
    for (const token of ['ORDER_DEADLINE_ROW_FAILED', 'ORDER_DEADLINE_REFUND_WAITING', 'ORDER_STOCK_RESTORE_FAILED', '.ai/deployment.md']) {
      expect([token, description.includes(token)]).toEqual([token, true]);
    }
    expect(description.length).toBeLessThanOrEqual(1024);
    expect(onAlarmStatus(template, 'OrderDeadlineRefundStalled')).toBe(true);
  });

  test('the server writes the tokens the filters read', () => {
    const deadlines = serverSource('Functions/OrderCronFunction.Deadlines.cs');
    expect(deadlines).toContain('DeadlineRowFailedToken = "ORDER_DEADLINE_ROW_FAILED"');
    expect(deadlines).toContain('$"{DeadlineRowFailedToken} orderId={MoneyAlarmLog.LogId(orderId)} reason=[{ex.GetType().Name}]; the rest of the run goes on."');
    // FUP-210 (PR #359) adds ORDER_DEADLINE_REFUND_WAITING to order-cron. Once it is in the source, it must be
    // spelled as the filter reads it.
    const orderCron = serverFilesMatching(/DeadlineRefundWaitingToken/).map(serverSource).join('\n');
    if (orderCron.length > 0) {
      expect(orderCron).toContain('DeadlineRefundWaitingToken = "ORDER_DEADLINE_REFUND_WAITING"');
    }
    // ORDER_STOCK_RESTORE_FAILED: escrow-cron's two restores and the dispute verdict's, which escrow-cron runs.
    const restoreWriters = serverFilesMatching(/ORDER_STOCK_RESTORE_FAILED orderId=/);
    expect(restoreWriters).toEqual(['Functions/EscrowCronFunction.cs', 'Services/DisputeVerdictMirror.cs']);
    expect(logGroupsOfHandlerFiles(['Functions/EscrowCronFunction.cs'])).toEqual([lg('escrow-cron')]);
  });
});
