import * as fs from 'fs';
import * as path from 'path';
import * as cdk from 'aws-cdk-lib';
import { Template, Match } from 'aws-cdk-lib/assertions';
import { MonitoringStack } from '../../lib/stacks/monitoring-stack';
import { environments } from '../../lib/config/environments';

// FUP-41: a release the payout identity gate blocks (release_blocked_kyc, audited once) waited silently for as
// long as the payee did not verify. The escrow service now logs RELEASE_BLOCKED_OVERDUE from escrow-cron's
// release on every hourly run once the stored reason is a day old. The helpers of dispute-alarms.test.ts are
// repeated here on purpose (sibling branches edit that file).

const devConfig = environments['dev'];

const ESCROW_LG = '/aws/lambda/wereda-connect-dev-escrow';
const ESCROW_CRON_LG = '/aws/lambda/wereda-connect-dev-escrow-cron';

// The exact lines the server writes (server/src/Helpers/MoneyAlarmLog.cs, ReleaseBlockedOverdue).
const FARMER_LINE = 'RELEASE_BLOCKED_OVERDUE escrowId=escrow-order-ord-1 leg=farmer reason=phone_unverified ageHours=30';
const TRUCKER_LINE = 'RELEASE_BLOCKED_OVERDUE escrowId=escrow-order-ord-2 leg=trucker reason=fayda_required ageHours=26';
const prefixed = (level: string, line: string) => `2026-11-13T10:00:00.000Z\t4f1c7a2e-req\t${level}\t${line}`;

// Lines of the same paths that are not this token: the gate's Information line, the block's audit failure,
// the other overdue and risk lines.
const OTHER_LINES = [
  'ESCROW_RELEASE_BLOCKED escrowId=escrow-order-ord-1 code=ESCROW_029 sweep=auto-release',
  'SETTLEMENT_BLOCK_AUDIT_FAILED escrowId=escrow-order-ord-1 reason=[AmazonDynamoDBException]',
  'TRUCKER_PAYOUT_OVERDUE escrowId=escrow-order-ord-1 reason=phone_unverified ageHours=26',
  'MANUAL_PAYOUT_OVERDUE escrowId=escrow-order-ord-1 ref=release:farmer ageHours=26',
  'RISK_SETTLEMENT_SKIPPED escrowId=escrow-order-ord-1 sweep=payout-farmer reason=payee_flagged holdReason=account_suspended',
  'ESCROW_SETTLEMENT_FAILED escrowId=escrow-del-1 action=auto_release_blocked outcome=failed code=release:trade:double_escrow',
  'ESCROW_DOUBLE_ESCROW_REFUND_DECIDED escrowId=escrow-del-1 orderId=ord-1 orderEscrowStatus=released',
];

type MetricFilterResource = {
  Properties: {
    LogGroupName: string;
    FilterPattern: string;
    MetricTransformations: Array<{ MetricName: string; MetricValue: string; DefaultValue?: number }>;
  };
};

function monitoringTemplate(enableTelebirrLive: boolean): Template {
  const app = new cdk.App();
  const stack = new MonitoringStack(app, 'ReleaseBlockedMonitoring', {
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

describe('FUP-41: releases blocked by the payout identity gate', () => {
  test('one filter, on escrow-cron only, counts both legs and nothing else', () => {
    const template = monitoringTemplate(true);
    const filters = filtersPublishing(template, 'ReleasesBlockedOverdue');
    expect(filters.map((f) => f.Properties.LogGroupName)).toEqual([ESCROW_CRON_LG]);
    const [filter] = filters;
    expect(filter.Properties.MetricTransformations[0]).toEqual(expect.objectContaining({ MetricValue: '1', DefaultValue: 0 }));
    for (const line of [FARMER_LINE, TRUCKER_LINE]) {
      for (const event of [line, prefixed('info', line), prefixed('warn', line)]) {
        expect([event, cloudWatchTermsMatch(filter.Properties.FilterPattern, event)]).toEqual([event, true]);
      }
    }
    for (const line of OTHER_LINES) {
      expect([line, cloudWatchTermsMatch(filter.Properties.FilterPattern, line)]).toEqual([line, false]);
    }
    expect(filtersPublishing(template, 'ReleasesBlockedOverdue').some((f) => f.Properties.LogGroupName === ESCROW_LG)).toBe(false);
  });

  test('no other money filter counts the new line', () => {
    const template = monitoringTemplate(true);
    for (const metric of [
      'EscrowSettlementFailures', 'EscrowSettlementUnknownOutcomes', 'OrderEscrowProblems', 'ManualPayoutsOverdue',
      'AdminQueueOverdue', 'TruckerPayoutsOverdue', 'DisputeVerdictOrderMismatch', 'DisputeVerdictSettlementBlocked',
      'OrderMoneyMismatch', 'RiskSettlementSkipped', 'LedgerRowsOverdue', 'DisputePropagationFailed',
    ]) {
      for (const filter of filtersPublishing(template, metric)) {
        expect([metric, cloudWatchTermsMatch(filter.Properties.FilterPattern, FARMER_LINE)]).toEqual([metric, false]);
      }
    }
  });

  test('the term carries an underscore, and an id that spells it, as the server logs it, matches nothing', () => {
    const [filter] = filtersPublishing(monitoringTemplate(true), 'ReleasesBlockedOverdue');
    const terms = filterTerms(filter.Properties.FilterPattern);
    expect(terms.every((t) => t.includes('_'))).toBe(true);
    const defanged = terms.join('-').replace(/_/g, '-');
    const line = `ESCROW_RELEASE_BLOCKED escrowId=${defanged} code=ESCROW_029 sweep=auto-release`;
    expect(cloudWatchTermsMatch(filter.Properties.FilterPattern, line)).toBe(false);
  });

  test('alarm: the money alarm shape (level-triggered, one notification per episode), on the dashboard', () => {
    for (const live of [false, true]) {
      const template = monitoringTemplate(live);
      template.hasResourceProperties('AWS::CloudWatch::Alarm', {
        AlarmName: 'wereda-connect-dev-releases-blocked-overdue',
        Metrics: [customMetric('ReleasesBlockedOverdue', 900)],
        Threshold: 0,
        ComparisonOperator: 'GreaterThanThreshold',
        EvaluationPeriods: 5,
        DatapointsToAlarm: 1,
        TreatMissingData: 'notBreaching',
        AlarmActions: notifiesTopic,
        OKActions: notifiesTopic,
        AlarmDescription: Match.stringLikeRegexp('RELEASE_BLOCKED_OVERDUE'),
      });
      const alarms = Object.values(template.findResources('AWS::CloudWatch::Alarm')) as Array<{
        Properties: { AlarmName: string; AlarmDescription?: string };
      }>;
      const alarm = alarms.find((a) => a.Properties.AlarmName === 'wereda-connect-dev-releases-blocked-overdue');
      const description = alarm?.Properties.AlarmDescription ?? '';
      expect(description.length).toBeLessThanOrEqual(1024);
      expect(description).toContain('phone_unverified or fayda_required');
      expect(description).not.toMatch(/[\u2013\u2014]/);
      const dashboard = JSON.stringify(template.findResources('AWS::CloudWatch::Dashboard'));
      expect(dashboard).toMatch(/"Fn::GetAtt":\["ReleasesBlockedOverdue[0-9A-F]+","Arn"\]/);
    }
  });
});

describe('FUP-41: the filter and the server agree on the token', () => {
  const read = (rel: string) => fs.readFileSync(path.join(__dirname, '../../../server/src', rel), 'utf8');

  test('the token is a MoneyAlarmLog constant, written through the money alarm sink of the escrow service', () => {
    const moneyAlarmLog = read('Helpers/MoneyAlarmLog.cs');
    expect(moneyAlarmLog).toContain('ReleaseBlockedOverdueToken = "RELEASE_BLOCKED_OVERDUE"');
    expect(moneyAlarmLog).toContain(
      '$"{ReleaseBlockedOverdueToken} escrowId={LogId(escrowId)} leg={Safe(leg)} reason={Safe(reason)} ageHours={Math.Max(0, ageHours)}"',
    );
    expect(read('Services/EscrowService.cs')).toContain('AlarmLog(MoneyAlarmLog.ReleaseBlockedOverdue(');
  });

  test('only the system release asks for it (the 1.x release, the ledger release, the sweep split)', () => {
    const service = read('Services/EscrowService.cs');
    const ledger = read('Services/EscrowService.LedgerSettle.cs');
    expect(service).toContain('overdueLeg: ManualPayoutFields.LegFarmer');
    expect(ledger).toContain('overdueLeg: ManualPayoutFields.LegFarmer');
    expect(ledger).toContain('overdueLeg: actor == SystemActor ? ManualPayoutFields.LegFarmer : null');
    expect(ledger).toContain('overdueLeg: actor == SystemActor ? ManualPayoutFields.LegTrucker : null');
  });
});
