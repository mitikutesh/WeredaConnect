import * as fs from 'fs';
import * as path from 'path';
import * as cdk from 'aws-cdk-lib';
import { Template, Match } from 'aws-cdk-lib/assertions';
import { MonitoringStack } from '../../lib/stacks/monitoring-stack';
import { environments } from '../../lib/config/environments';

// FUP-436: the level-triggered overdue alarms trucker-payouts-overdue, releases-blocked-overdue and
// ledger-rows-overdue stay in ALARM while any item is overdue, so an item that became overdue meanwhile told nobody.
// escrow-cron now logs an edge twin of each line once per item (a conditional marker keyed on the item's own
// since-time), and an edge-triggered alarm counts it, the shape of FUP-06's manual-payouts-newly-overdue. The
// helpers of newly-overdue-alarm.test.ts are repeated here on purpose (sibling branches edit that file).

const devConfig = environments['dev'];

const ESCROW_LG = '/aws/lambda/wereda-connect-dev-escrow';
const ESCROW_CRON_LG = '/aws/lambda/wereda-connect-dev-escrow-cron';
const prefixed = (level: string, line: string) => `2026-11-13T10:00:00.000Z\t4f1c7a2e-req\t${level}\t${line}`;

type Twin = {
  name: string;
  token: string;
  metric: string;
  levelMetric: string;
  levelAlarm: string;
  // The exact lines the server writes (server/src/Helpers/MoneyAlarmLog.cs).
  lines: string[];
  levelLine: string;
  // The warning the producer logs when the marker write fails (never counted).
  warning: string;
  descriptionMentions: string;
};

const TWINS: Twin[] = [
  {
    name: 'trucker-payouts-newly-overdue',
    token: 'TRUCKER_PAYOUT_NEWLY_OVERDUE',
    metric: 'TruckerPayoutsNewlyOverdue',
    levelMetric: 'TruckerPayoutsOverdue',
    levelAlarm: 'trucker-payouts-overdue',
    lines: [
      // The 1.x trucker retry and the ledger transport retry.
      'TRUCKER_PAYOUT_NEWLY_OVERDUE escrowId=escrow-order-ord-1 reason=phone_unverified ageHours=26',
      'TRUCKER_PAYOUT_NEWLY_OVERDUE escrowId=escrow-order-ord-2 reason=blocked ageHours=30',
    ],
    levelLine: 'TRUCKER_PAYOUT_OVERDUE escrowId=escrow-order-ord-1 reason=phone_unverified ageHours=26',
    warning:
      'Trucker retry: the newly overdue marker of escrow-order-ord-1 was not written: [ProvisionedThroughputExceededException]. Announced anyway; the next run may announce it again.',
    descriptionMentions: 'reason=phone_unverified',
  },
  {
    name: 'releases-blocked-newly-overdue',
    token: 'RELEASE_BLOCKED_NEWLY_OVERDUE',
    metric: 'ReleasesBlockedNewlyOverdue',
    levelMetric: 'ReleasesBlockedOverdue',
    levelAlarm: 'releases-blocked-overdue',
    lines: [
      'RELEASE_BLOCKED_NEWLY_OVERDUE escrowId=escrow-order-ord-1 leg=farmer reason=phone_unverified ageHours=30',
      'RELEASE_BLOCKED_NEWLY_OVERDUE escrowId=escrow-order-ord-2 leg=trucker reason=fayda_required ageHours=25',
    ],
    levelLine: 'RELEASE_BLOCKED_OVERDUE escrowId=escrow-order-ord-1 leg=farmer reason=phone_unverified ageHours=30',
    warning:
      'Release blocked report: the newly overdue marker of escrow-order-ord-1 was not written: [ProvisionedThroughputExceededException]. Announced anyway; the next run may announce it again.',
    descriptionMentions: '/escrows/<escrowId>',
  },
  {
    name: 'ledger-rows-newly-overdue',
    token: 'LEDGER_ROW_NEWLY_OVERDUE',
    metric: 'LedgerRowsNewlyOverdue',
    levelMetric: 'LedgerRowsOverdue',
    levelAlarm: 'ledger-rows-overdue',
    lines: [
      'LEDGER_ROW_NEWLY_OVERDUE escrowId=escrow-order-ord-1 ref=release.farmer status=pending ageHours=26',
      'LEDGER_ROW_NEWLY_OVERDUE escrowId=escrow-order-ord-2 ref=refund.all status=sent ageHours=40',
    ],
    levelLine: 'LEDGER_ROW_OVERDUE escrowId=escrow-order-ord-1 ref=release.farmer status=pending ageHours=26',
    warning:
      'Ledger overdue report: the newly overdue marker of escrow-order-ord-1 release.farmer was not written: [ProvisionedThroughputExceededException]. Announced anyway; the next sweep may announce it again.',
    descriptionMentions: 'gatewayRef',
  },
];

const FUP06_LINES = [
  'MANUAL_PAYOUT_OVERDUE escrowId=escrow-order-ord-1 ref=release:farmer ageHours=24',
  'MANUAL_PAYOUT_NEWLY_OVERDUE escrowId=escrow-order-ord-1 ref=release:farmer ageHours=24',
];

type MetricFilterResource = {
  Properties: {
    LogGroupName: string;
    FilterPattern: string;
    MetricTransformations: Array<{ MetricName: string; MetricNamespace: string; MetricValue: string; DefaultValue?: number }>;
  };
};

function monitoringTemplate(enableTelebirrLive: boolean): Template {
  const app = new cdk.App();
  return Template.fromStack(new MonitoringStack(app, 'EdgeOverdueMonitoring', {
    env: { account: '123456789012', region: devConfig.region },
    envConfig: { ...devConfig, enableTelebirrLive },
    apiUrl: `https://test.execute-api.${devConfig.region}.amazonaws.com`,
    apiId: 'testapi123',
  }));
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

type AlarmResource = { Properties: { AlarmName: string; AlarmDescription?: string } };

describe('FUP-436: edge twins of the level-triggered overdue alarms', () => {
  const template = monitoringTemplate(true);
  const otherTwinLines = (twin: Twin) => TWINS.filter((t) => t !== twin).flatMap((t) => t.lines);

  test.each(TWINS)('$metric: one filter, on escrow-cron only, without a default, counts only its own lines', (twin) => {
    const filters = filtersPublishing(template, twin.metric);
    expect(filters.map((f) => f.Properties.LogGroupName)).toEqual([ESCROW_CRON_LG]);
    const [filter] = filters;
    expect(filter.Properties.MetricTransformations[0]).toEqual({ MetricName: twin.metric, MetricNamespace: 'WeredaConnect/dev', MetricValue: '1' });
    for (const line of twin.lines) {
      for (const event of [line, prefixed('warn', line), prefixed('info', line)]) {
        expect([event, cloudWatchTermsMatch(filter.Properties.FilterPattern, event)]).toEqual([event, true]);
      }
    }
    for (const line of [twin.levelLine, twin.warning, ...otherTwinLines(twin), ...FUP06_LINES]) {
      expect([line, cloudWatchTermsMatch(filter.Properties.FilterPattern, line)]).toEqual([line, false]);
    }
    expect(filtersPublishing(template, twin.metric).some((f) => f.Properties.LogGroupName === ESCROW_LG)).toBe(false);
  });

  test.each(TWINS)('$levelMetric keeps counting only its level line, and no other money filter counts $token', (twin) => {
    const [level] = filtersPublishing(template, twin.levelMetric).filter((f) => f.Properties.LogGroupName === ESCROW_CRON_LG);
    expect(cloudWatchTermsMatch(level.Properties.FilterPattern, twin.levelLine)).toBe(true);
    expect(cloudWatchTermsMatch(level.Properties.FilterPattern, prefixed('warn', twin.levelLine))).toBe(true);
    for (const metric of [
      'EscrowSettlementFailures', 'EscrowSettlementUnknownOutcomes', 'OrderEscrowProblems', 'ManualPayoutsOverdue',
      'ManualPayoutsNewlyOverdue', 'AdminQueueOverdue', 'TruckerPayoutsOverdue', 'DisputeVerdictOrderMismatch',
      'DisputeVerdictSettlementBlocked', 'OrderMoneyMismatch', 'RiskSettlementSkipped', 'LedgerRowsOverdue',
      'DisputePropagationFailed', 'ReleasesBlockedOverdue', 'DisputeVerdictWaitOverdue', 'EscrowLedgerInvariant',
      ...TWINS.filter((t) => t !== twin).map((t) => t.metric),
    ]) {
      for (const filter of filtersPublishing(template, metric)) {
        for (const line of twin.lines) {
          expect([metric, line, cloudWatchTermsMatch(filter.Properties.FilterPattern, line)]).toEqual([metric, line, false]);
        }
      }
    }
  });

  test.each(TWINS)('$token: the term carries an underscore, and an id that spells it, as the server logs it, matches nothing', (twin) => {
    const [filter] = filtersPublishing(template, twin.metric);
    const terms = filterTerms(filter.Properties.FilterPattern);
    expect(terms).toEqual([twin.token]);
    const defanged = terms.join('-').replace(/_/g, '-');
    const line = twin.levelLine.replace('escrowId=escrow-order-ord-1', `escrowId=escrow-order-${defanged}`);
    expect(cloudWatchTermsMatch(filter.Properties.FilterPattern, line)).toBe(false);
  });

  test.each(TWINS)('$name: edge-triggered (two 5 minute periods, one datapoint), notifies the topic, on the dashboard', (twin) => {
    for (const live of [false, true]) {
      const t = monitoringTemplate(live);
      const alarmName = `wereda-connect-dev-${twin.name}`;
      t.hasResourceProperties('AWS::CloudWatch::Alarm', {
        AlarmName: alarmName,
        Metrics: [customMetric(twin.metric, 300)],
        Threshold: 0,
        ComparisonOperator: 'GreaterThanThreshold',
        EvaluationPeriods: 2,
        DatapointsToAlarm: 1,
        TreatMissingData: 'notBreaching',
        AlarmActions: notifiesTopic,
        OKActions: notifiesTopic,
        AlarmDescription: Match.stringLikeRegexp(twin.token),
      });
      const alarms = Object.values(t.findResources('AWS::CloudWatch::Alarm')) as AlarmResource[];
      const description = alarms.find((a) => a.Properties.AlarmName === alarmName)?.Properties.AlarmDescription ?? '';
      expect(description.length).toBeLessThanOrEqual(1024);
      expect(description).toContain(twin.descriptionMentions);
      expect(description).toContain(twin.levelAlarm);
      expect(description).not.toMatch(/[\u2013\u2014]/);
      // The level-triggered alarm points at its twin instead of "read the lines".
      const level = alarms.find((a) => a.Properties.AlarmName === `wereda-connect-dev-${twin.levelAlarm}`);
      const levelDescription = level?.Properties.AlarmDescription ?? '';
      expect(levelDescription).toContain(twin.name);
      expect(levelDescription).not.toContain('read the lines');
      expect(levelDescription.length).toBeLessThanOrEqual(1024);
      expect(levelDescription).not.toMatch(/[\u2013\u2014]/);
      const dashboard = JSON.stringify(t.findResources('AWS::CloudWatch::Dashboard'));
      expect(dashboard).toMatch(new RegExp(`"Fn::GetAtt":\\["${twin.metric}[0-9A-F]+","Arn"\\]`));
    }
  });
});

describe('FUP-436: the filters and the server agree on the tokens', () => {
  const read = (rel: string) => fs.readFileSync(path.join(__dirname, '../../../server/src', rel), 'utf8');

  test('each token is a MoneyAlarmLog constant with its level line\'s fields', () => {
    const moneyAlarmLog = read('Helpers/MoneyAlarmLog.cs');
    expect(moneyAlarmLog).toContain('TruckerPayoutNewlyOverdueToken = "TRUCKER_PAYOUT_NEWLY_OVERDUE"');
    expect(moneyAlarmLog).toContain(
      '$"{TruckerPayoutNewlyOverdueToken} escrowId={LogId(escrowId)} reason={Safe(reason)} ageHours={Math.Max(0, ageHours)}"',
    );
    expect(moneyAlarmLog).toContain('ReleaseBlockedNewlyOverdueToken = "RELEASE_BLOCKED_NEWLY_OVERDUE"');
    expect(moneyAlarmLog).toContain(
      '$"{ReleaseBlockedNewlyOverdueToken} escrowId={LogId(escrowId)} leg={Safe(leg)} reason={Safe(reason)} ageHours={Math.Max(0, ageHours)}"',
    );
    expect(moneyAlarmLog).toContain('LedgerRowNewlyOverdueToken = "LEDGER_ROW_NEWLY_OVERDUE"');
    expect(moneyAlarmLog).toContain(
      '$"{LedgerRowNewlyOverdueToken} escrowId={LogId(escrowId)} ref={LogRef(reference)} status={Safe(status)} ageHours={Math.Max(0, ageHours)}"',
    );
  });

  test('escrow-cron logs each one after a marker write: the trucker retries, the release gate and the ledger sweep', () => {
    const cron = read('Functions/EscrowCronFunction.cs');
    expect(cron.match(/context\.Logger\.LogWarning\(MoneyAlarmLog\.TruckerPayoutNewlyOverdue\(/g)).toHaveLength(2);
    expect(cron).toContain('_escrowRepo.TryMarkTruckerPayoutOverdueLoggedAsync(');
    expect(cron).toContain('ledger.TryMarkTransportOverdueLoggedAsync(');
    const service = read('Services/EscrowService.cs');
    expect(service).toContain('AlarmLog(MoneyAlarmLog.ReleaseBlockedNewlyOverdue(');
    expect(service).toContain('_escrowRepo.TryMarkSettlementBlockOverdueLoggedAsync(');
    const ledger = read('Services/EscrowService.Ledger.cs');
    expect(ledger).toContain('AlarmLog(MoneyAlarmLog.LedgerRowNewlyOverdue(');
    expect(ledger).toContain('LedgerRepo!.TryMarkRowOverdueLoggedAsync(');
  });
});
