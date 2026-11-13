import * as fs from 'fs';
import * as path from 'path';
import * as cdk from 'aws-cdk-lib';
import { Template, Match, Annotations } from 'aws-cdk-lib/assertions';
import { MonitoringStack } from '../../lib/stacks/monitoring-stack';
import { EnvironmentConfig, environments } from '../../lib/config/environments';

// FUP-06: MANUAL_PAYOUT_OVERDUE repeats every run, so manual-payouts-overdue stays in ALARM while any leg is
// overdue and a leg that became overdue meanwhile told nobody. The escrow-cron reports now log
// MANUAL_PAYOUT_NEWLY_OVERDUE once per leg (a conditional marker on the escrow row or the ledger row), and an
// edge-triggered alarm counts it. Also: the alarm topic's subscribers in code (envConfig.alarmEmails). The
// helpers of release-blocked-alarm.test.ts are repeated here on purpose (sibling branches edit that file).

const devConfig = environments['dev'];

const ESCROW_LG = '/aws/lambda/wereda-connect-dev-escrow';
const ESCROW_CRON_LG = '/aws/lambda/wereda-connect-dev-escrow-cron';
const ALARM_NAME = 'wereda-connect-dev-manual-payouts-newly-overdue';

// The exact lines the server writes (server/src/Helpers/MoneyAlarmLog.cs, ManualPayoutNewlyOverdue): the hourly
// report's 1.x leg and the ledger sweep's manual row.
const NEWLY_LINE = 'MANUAL_PAYOUT_NEWLY_OVERDUE escrowId=escrow-order-ord-1 ref=release:farmer ageHours=24';
const NEWLY_LEDGER_LINE = 'MANUAL_PAYOUT_NEWLY_OVERDUE escrowId=escrow-order-ord-2 ref=release.trucker.trucker-1 ageHours=25';
const OVERDUE_LINE = 'MANUAL_PAYOUT_OVERDUE escrowId=escrow-order-ord-1 ref=release:farmer ageHours=24';
const prefixed = (level: string, line: string) => `2026-11-13T10:00:00.000Z\t4f1c7a2e-req\t${level}\t${line}`;

// Lines of the same paths that are not this token: the level-triggered line, the queue's information lines, the
// report's marker warning, the other overdue lines.
const OTHER_LINES = [
  OVERDUE_LINE,
  'ESCROW_MANUAL_PAYOUT_QUEUED escrowId=escrow-order-ord-1 op=release legs=farmer,trucker reason=PAYOUT_NOT_SUPPORTED',
  'ESCROW_MANUAL_PAYOUT_WAITING escrowId=escrow-order-ord-1 sweep=auto-release',
  'Manual payout report: the newly overdue marker of escrow-order-ord-1 was not written: [ProvisionedThroughputExceededException]. Announced anyway; the next run may announce it again.',
  'TRUCKER_PAYOUT_OVERDUE escrowId=escrow-order-ord-1 reason=phone_unverified ageHours=26',
  'LEDGER_ROW_OVERDUE escrowId=escrow-order-ord-1 ref=release.farmer status=pending ageHours=26',
  'RELEASE_BLOCKED_OVERDUE escrowId=escrow-order-ord-1 leg=farmer reason=phone_unverified ageHours=30',
];

type MetricFilterResource = {
  Properties: {
    LogGroupName: string;
    FilterPattern: string;
    MetricTransformations: Array<{ MetricName: string; MetricValue: string; DefaultValue?: number }>;
  };
};

function monitoringStack(envConfig: EnvironmentConfig, enableTelebirrLive = false): MonitoringStack {
  const app = new cdk.App();
  return new MonitoringStack(app, 'NewlyOverdueMonitoring', {
    env: { account: '123456789012', region: envConfig.region },
    envConfig: { ...envConfig, enableTelebirrLive },
    apiUrl: `https://test.execute-api.${envConfig.region}.amazonaws.com`,
    apiId: 'testapi123',
  });
}

function monitoringTemplate(enableTelebirrLive: boolean): Template {
  return Template.fromStack(monitoringStack(devConfig, enableTelebirrLive));
}

function filtersPublishing(template: Template, metricName: string): MetricFilterResource[] {
  return (Object.values(template.findResources('AWS::Logs::MetricFilter')) as MetricFilterResource[]).filter(
    (f) => f.Properties.MetricTransformations[0].MetricName === metricName,
  );
}

// The minimal model of CloudWatch Logs term matching of stack.test.ts.
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

function customMetric(metricName: string, periodSeconds: number) {
  return Match.objectLike({
    MetricStat: Match.objectLike({
      Metric: Match.objectLike({ MetricName: metricName, Namespace: 'WeredaConnect/dev' }),
      Period: periodSeconds,
      Stat: 'Sum',
    }),
  });
}

const notifiesTopic = Match.arrayWith([Match.objectLike({ Ref: Match.stringLikeRegexp('^AlarmTopic') })]);

describe('FUP-06: manual payouts newly overdue', () => {
  test('one filter, on escrow-cron only, counts both reports\' lines and nothing else', () => {
    const template = monitoringTemplate(true);
    const filters = filtersPublishing(template, 'ManualPayoutsNewlyOverdue');
    expect(filters.map((f) => f.Properties.LogGroupName)).toEqual([ESCROW_CRON_LG]);
    const [filter] = filters;
    // No default value: an hour without a line publishes nothing (not billed), and the alarm reads it as OK.
    expect(filter.Properties.MetricTransformations[0]).toEqual({ MetricName: 'ManualPayoutsNewlyOverdue', MetricNamespace: 'WeredaConnect/dev', MetricValue: '1' });
    for (const line of [NEWLY_LINE, NEWLY_LEDGER_LINE]) {
      for (const event of [line, prefixed('warn', line), prefixed('info', line)]) {
        expect([event, cloudWatchTermsMatch(filter.Properties.FilterPattern, event)]).toEqual([event, true]);
      }
    }
    for (const line of OTHER_LINES) {
      expect([line, cloudWatchTermsMatch(filter.Properties.FilterPattern, line)]).toEqual([line, false]);
    }
    expect(filtersPublishing(template, 'ManualPayoutsNewlyOverdue').some((f) => f.Properties.LogGroupName === ESCROW_LG)).toBe(false);
  });

  test('the level-triggered filter keeps counting only its own line, and no other money filter counts the new one', () => {
    const template = monitoringTemplate(true);
    const [overdue] = filtersPublishing(template, 'ManualPayoutsOverdue');
    expect(cloudWatchTermsMatch(overdue.Properties.FilterPattern, OVERDUE_LINE)).toBe(true);
    expect(cloudWatchTermsMatch(overdue.Properties.FilterPattern, prefixed('warn', OVERDUE_LINE))).toBe(true);
    for (const metric of [
      'EscrowSettlementFailures', 'EscrowSettlementUnknownOutcomes', 'OrderEscrowProblems', 'ManualPayoutsOverdue',
      'AdminQueueOverdue', 'TruckerPayoutsOverdue', 'DisputeVerdictOrderMismatch', 'DisputeVerdictSettlementBlocked',
      'OrderMoneyMismatch', 'RiskSettlementSkipped', 'LedgerRowsOverdue', 'DisputePropagationFailed',
      'ReleasesBlockedOverdue', 'DisputeVerdictWaitOverdue', 'EscrowLedgerInvariant',
    ]) {
      for (const filter of filtersPublishing(template, metric)) {
        for (const line of [NEWLY_LINE, NEWLY_LEDGER_LINE]) {
          expect([metric, line, cloudWatchTermsMatch(filter.Properties.FilterPattern, line)]).toEqual([metric, line, false]);
        }
      }
    }
  });

  test('the term carries an underscore, and an id that spells it, as the server logs it, matches nothing', () => {
    const [filter] = filtersPublishing(monitoringTemplate(true), 'ManualPayoutsNewlyOverdue');
    const terms = filterTerms(filter.Properties.FilterPattern);
    expect(terms).toEqual(['MANUAL_PAYOUT_NEWLY_OVERDUE']);
    const defanged = terms.join('-').replace(/_/g, '-');
    const line = `MANUAL_PAYOUT_OVERDUE escrowId=escrow-order-${defanged} ref=refund:buyer ageHours=25`;
    expect(cloudWatchTermsMatch(filter.Properties.FilterPattern, line)).toBe(false);
  });

  test('alarm: edge-triggered (two 5 minute periods, one datapoint), notifies the topic, on the dashboard', () => {
    for (const live of [false, true]) {
      const template = monitoringTemplate(live);
      template.hasResourceProperties('AWS::CloudWatch::Alarm', {
        AlarmName: ALARM_NAME,
        Metrics: [customMetric('ManualPayoutsNewlyOverdue', 300)],
        Threshold: 0,
        ComparisonOperator: 'GreaterThanThreshold',
        EvaluationPeriods: 2,
        DatapointsToAlarm: 1,
        TreatMissingData: 'notBreaching',
        AlarmActions: notifiesTopic,
        OKActions: notifiesTopic,
        AlarmDescription: Match.stringLikeRegexp('MANUAL_PAYOUT_NEWLY_OVERDUE'),
      });
      const alarms = Object.values(template.findResources('AWS::CloudWatch::Alarm')) as Array<{
        Properties: { AlarmName: string; AlarmDescription?: string };
      }>;
      const description = alarms.find((a) => a.Properties.AlarmName === ALARM_NAME)?.Properties.AlarmDescription ?? '';
      expect(description.length).toBeLessThanOrEqual(1024);
      expect(description).toContain('/payouts');
      expect(description).not.toMatch(/[\u2013\u2014]/);
      // The level-triggered alarm now points at it instead of "read the lines".
      const overdue = alarms.find((a) => a.Properties.AlarmName === 'wereda-connect-dev-manual-payouts-overdue');
      expect(overdue?.Properties.AlarmDescription).toContain('manual-payouts-newly-overdue');
      expect((overdue?.Properties.AlarmDescription ?? '').length).toBeLessThanOrEqual(1024);
      const dashboard = JSON.stringify(template.findResources('AWS::CloudWatch::Dashboard'));
      expect(dashboard).toMatch(/"Fn::GetAtt":\["ManualPayoutsNewlyOverdue[0-9A-F]+","Arn"\]/);
    }
  });
});

describe('FUP-06: the filter and the server agree on the token', () => {
  const read = (rel: string) => fs.readFileSync(path.join(__dirname, '../../../server/src', rel), 'utf8');

  test('the token is a MoneyAlarmLog constant with the overdue line\'s fields', () => {
    const moneyAlarmLog = read('Helpers/MoneyAlarmLog.cs');
    expect(moneyAlarmLog).toContain('ManualPayoutNewlyOverdueToken = "MANUAL_PAYOUT_NEWLY_OVERDUE"');
    expect(moneyAlarmLog).toContain(
      '$"{ManualPayoutNewlyOverdueToken} escrowId={LogId(escrowId)} ref={Safe(reference)} ageHours={Math.Max(0, ageHours)}"',
    );
  });

  test('both escrow-cron reports log it: the hourly 1.x report and the ledger sweep through the money alarm sink', () => {
    const cron = read('Functions/EscrowCronFunction.cs');
    expect(cron).toContain('context.Logger.LogWarning(MoneyAlarmLog.ManualPayoutNewlyOverdue(');
    expect(cron).toContain('_escrowRepo.TryMarkManualPayoutOverdueLoggedAsync(');
    const ledger = read('Services/EscrowService.Ledger.cs');
    expect(ledger).toContain('AlarmLog(MoneyAlarmLog.ManualPayoutNewlyOverdue(');
    expect(ledger).toContain('LedgerRepo!.TryMarkManualOverdueLoggedAsync(');
  });
});

describe('FUP-06: alarm topic subscribers in code', () => {
  test.each(['dev', 'staging', 'prod'])('%s: no subscription unless alarmEmails is set', (envName) => {
    const config = environments[envName];
    const template = Template.fromStack(monitoringStack(config));
    template.resourceCountIs('AWS::SNS::Topic', 1);
    template.resourceCountIs('AWS::SNS::Subscription', (config.alarmEmails ?? []).length);
  });

  test('each alarmEmails address subscribes once to the alarm topic, trimmed and deduplicated', () => {
    const template = Template.fromStack(monitoringStack({
      ...environments['staging'],
      alarmEmails: ['ops-alarms@example.org', ' ops-alarms@example.org ', 'oncall@example.org'],
    }));
    template.resourceCountIs('AWS::SNS::Subscription', 2);
    for (const endpoint of ['ops-alarms@example.org', 'oncall@example.org']) {
      template.hasResourceProperties('AWS::SNS::Subscription', {
        Protocol: 'email',
        Endpoint: endpoint,
        TopicArn: { Ref: Match.stringLikeRegexp('^AlarmTopic') },
      });
    }
  });

  test('a malformed address fails the synth', () => {
    const stack = monitoringStack({ ...environments['prod'], alarmEmails: ['not-an-address'] });
    Annotations.fromStack(stack).hasError('*', Match.stringLikeRegexp("alarmEmails: 'not-an-address' is not an email address"));
    Template.fromStack(stack).resourceCountIs('AWS::SNS::Subscription', 0);
  });
});
