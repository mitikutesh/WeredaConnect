import * as fs from 'fs';
import * as path from 'path';
import * as cdk from 'aws-cdk-lib';
import { Template, Match } from 'aws-cdk-lib/assertions';
import { MonitoringStack } from '../../lib/stacks/monitoring-stack';
import { environments } from '../../lib/config/environments';

// FUP-27: DISPUTE_PROPAGATION_FAILED had no metric filter (a failed dispute mirror leg alarmed
// nothing), and the escrow dispute-rate alarm counted only POST /escrow/dispute, not delivery
// disputes through sync. The money alarm helpers of stack.test.ts are repeated here on purpose
// (sibling branches edit that file).

const devConfig = environments['dev'];

const ESCROW_LG = '/aws/lambda/wereda-connect-dev-escrow';
const ESCROW_CRON_LG = '/aws/lambda/wereda-connect-dev-escrow-cron';
const SYNC_LG = '/aws/lambda/wereda-connect-dev-sync';

// The exact lines the server writes (server/src/Helpers/MoneyAlarmLog.cs).
const PROPAGATION_LINE =
  'DISPUTE_PROPAGATION_FAILED escrowId=escrow-order-ord-1 orderId=ord-1 deliveryId=del-1 legs=order:conflict';
const PROPAGATION_LOOKUP_LINE =
  'DISPUTE_PROPAGATION_FAILED escrowId=escrow-001 orderId=- deliveryId=- legs=lookup:AmazonDynamoDBException';
const FROZEN_LINE = 'DELIVERY_DISPUTE_FROZEN escrowId=escrow-order-ord-1 orderId=ord-1 deliveryId=del-1';
// The Lambda runtime's prefix: time, request id and level, tab separated.
const prefixed = (level: string, line: string) => `2026-10-23T10:00:00.000Z\t4f1c7a2e-req\t${level}\t${line}`;

// Lines of the dispute paths that are not these tokens (the server's own formats).
const OTHER_DISPUTE_LINES = [
  // ESC-03's verdict mirror: a failed write the next run repairs; not this alarm.
  'DISPUTE_VERDICT_PROPAGATION_FAILED escrowId=escrow-order-ord-1 orderId=ord-1 deliveryId=del-1 legs=order:conflict',
  'DISPUTE_PROPAGATION_AUDIT_FAILED escrowId=escrow-order-ord-1 reason=[AmazonDynamoDBException]',
  'DISPUTE_VERDICT_PROPAGATION_AUDIT_FAILED escrowId=escrow-order-ord-1 reason=[AmazonDynamoDBException]',
  'DISPUTE_REPAIR_FAILED escrowId=escrow-order-ord-1 reason=[TimeoutException]',
  'Dispute mirror for delivery del-1 failed: [TimeoutException] The operation timed out.',
  // The sync freeze's info lines, refusals and retries.
  'Dispute on delivery del-1 by buyer-1: escrow escrow-order-ord-1 Frozen',
  'Dispute on delivery del-1 by buyer-1: escrow escrow-order-ord-1 AlreadyDisputed',
  'Repeated dispute on delivery del-1: escrow escrow-order-ord-1 Frozen',
  'Op op-1: status=error, msg=DELIVERY_DISPUTE_REFUSED: The payment for this delivery is being settled, so it can no longer be disputed. Contact support.',
  'Op op-1: status=error, msg=DELIVERY_DISPUTE_RETRY: The dispute could not be recorded right now. It will be sent again.',
  'DISPUTE_NO_ESCROW orderId=ord-1 deliveryId=del-1',
  // escrow-cron's sweep freeze of a legacy disputed trade (a repair, not a new dispute).
  'ESCROW_SWEEP_DISPUTE_FROZEN escrowId=escrow-order-ord-1 op=release',
  'ESCROW_DISPUTE_REFUSED_SETTLEMENT_IN_FLIGHT escrowId=escrow-order-ord-1',
];

// The other money tokens: the new filters must not count them, and theirs must not count ours.
const OTHER_MONEY_LINES = [
  'ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-1 action=release_failed outcome=unknown code=TELEBIRR_TIMEOUT',
  'ORDER_ESCROW_LINK_FAILED orderId=ord-1 deliveryId=del-1 reason=[ProvisionedThroughputExceededException]',
  'MANUAL_PAYOUT_OVERDUE escrowId=escrow-order-ord-1 ref=release:farmer ageHours=26',
  'ADMIN_QUEUE_OVERDUE orderId=ord-1 reason=undelivered ageHours=30',
  'TRUCKER_PAYOUT_OVERDUE escrowId=escrow-order-ord-1 reason=phone_unverified ageHours=26',
  'DISPUTE_VERDICT_ORDER_MISMATCH escrowId=escrow-order-ord-1 op=refund legs=order:mismatch:accepted',
  'DISPUTE_VERDICT_SETTLEMENT_BLOCKED escrowId=escrow-order-ord-1 op=refund legs=order:cancel:conflict',
  'ORDER_MONEY_MISMATCH orderId=ord-1 escrowId=escrow-order-ord-1 orderStatus=delivered escrowStatus=refunded reason=cannot_follow',
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
  const stack = new MonitoringStack(app, 'DisputeAlarmsMonitoring', {
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

function filterOn(template: Template, logGroupName: string, metricName: string): MetricFilterResource {
  const hit = filtersPublishing(template, metricName).find((f) => f.Properties.LogGroupName === logGroupName);
  if (!hit) {
    throw new Error(`no metric filter publishing ${metricName} on ${logGroupName}`);
  }
  return hit;
}

// The same minimal model of CloudWatch Logs term matching as stack.test.ts: a bare or quoted term
// matches a whole token of the line, `?` makes the terms alternatives, a quoted term with other
// characters matches as a substring. The deployed patterns are checked against the real service
// by scripts/money_alarms_synthetic.sh.
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

// Every term the pattern requires appears as a plain substring: stricter than the service.
function substringMatch(pattern: string, line: string): boolean {
  const terms = filterTerms(pattern);
  return pattern.includes('?') ? terms.some((t) => line.includes(t)) : terms.every((t) => line.includes(t));
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

const notifiesTopic = Match.arrayWith([Match.objectLike({ Ref: Match.stringLikeRegexp('^AlarmTopic') })]);

describe('FUP-27: dispute propagation alarm', () => {
  test('the token is counted on every log group whose Lambda runs the dispute mirror, and only there', () => {
    const template = monitoringTemplate(true);
    const filters = filtersPublishing(template, 'DisputePropagationFailed');
    // escrow (POST /escrow/dispute and its repeat), sync (delivery dispute), escrow-cron (sweep freeze).
    expect(filters.map((f) => f.Properties.LogGroupName).sort()).toEqual([ESCROW_LG, ESCROW_CRON_LG, SYNC_LG].sort());
    for (const filter of filters) {
      expect(filter.Properties.MetricTransformations[0]).toEqual(
        expect.objectContaining({ MetricValue: '1', DefaultValue: 0 }),
      );
      const pattern = filter.Properties.FilterPattern;
      for (const line of [PROPAGATION_LINE, PROPAGATION_LOOKUP_LINE]) {
        for (const event of [line, prefixed('info', line), prefixed('warn', line)]) {
          expect([event, cloudWatchTermsMatch(pattern, event)]).toEqual([event, true]);
        }
      }
      for (const line of [...OTHER_DISPUTE_LINES, FROZEN_LINE, ...OTHER_MONEY_LINES]) {
        expect([line, cloudWatchTermsMatch(pattern, line)]).toEqual([line, false]);
      }
    }
  });

  test('no other money filter counts the propagation line', () => {
    const template = monitoringTemplate(true);
    for (const metric of [
      'EscrowSettlementFailures', 'EscrowSettlementUnknownOutcomes', 'OrderEscrowProblems', 'ManualPayoutsOverdue',
      'AdminQueueOverdue', 'TruckerPayoutsOverdue', 'DisputeVerdictOrderMismatch', 'DisputeVerdictSettlementBlocked',
      'OrderMoneyMismatch', 'OrderPaymentProblems', 'EscrowDispute',
    ]) {
      for (const filter of filtersPublishing(template, metric)) {
        expect([metric, cloudWatchTermsMatch(filter.Properties.FilterPattern, PROPAGATION_LINE)]).toEqual([metric, false]);
      }
    }
  });

  test('alarm: any line in 15 minutes, held for 5 periods, notifies the alarm topic, on the dashboard', () => {
    for (const live of [false, true]) {
      const template = monitoringTemplate(live);
      template.hasResourceProperties('AWS::CloudWatch::Alarm', {
        AlarmName: 'wereda-connect-dev-dispute-propagation-failed',
        Metrics: [customMetric('DisputePropagationFailed', 900)],
        Threshold: 0,
        ComparisonOperator: 'GreaterThanThreshold',
        EvaluationPeriods: 5,
        DatapointsToAlarm: 1,
        TreatMissingData: 'notBreaching',
        AlarmActions: notifiesTopic,
        OKActions: notifiesTopic,
        AlarmDescription: Match.stringLikeRegexp('DISPUTE_PROPAGATION_FAILED'),
      });
      const alarms = Object.values(template.findResources('AWS::CloudWatch::Alarm')) as Array<{
        Properties: { AlarmName: string; AlarmDescription?: string };
      }>;
      const alarm = alarms.find((a) => a.Properties.AlarmName === 'wereda-connect-dev-dispute-propagation-failed');
      expect((alarm?.Properties.AlarmDescription ?? '').length).toBeLessThanOrEqual(1024);
      const dashboard = JSON.stringify(template.findResources('AWS::CloudWatch::Dashboard'));
      expect(dashboard).toMatch(/"Fn::GetAtt":\["DisputePropagationFailed[0-9A-F]+","Arn"\]/);
    }
  });
});

describe('FUP-27: sync delivery disputes count toward the escrow dispute rate', () => {
  test('a sync filter on DELIVERY_DISPUTE_FROZEN publishes the same metric as the escrow audit line', () => {
    const template = monitoringTemplate(false);
    const sync = filterOn(template, SYNC_LG, 'EscrowDispute');
    expect(sync.Properties.MetricTransformations[0]).toEqual(expect.objectContaining({ MetricValue: '1', DefaultValue: 0 }));
    for (const event of [FROZEN_LINE, prefixed('warn', FROZEN_LINE), prefixed('info', FROZEN_LINE)]) {
      expect([event, cloudWatchTermsMatch(sync.Properties.FilterPattern, event)]).toEqual([event, true]);
    }
    // Only a fresh freeze counts: already disputed, refused, retried, the propagation line and
    // the cron's repair freeze are not new disputes.
    for (const line of [...OTHER_DISPUTE_LINES, PROPAGATION_LINE, ...OTHER_MONEY_LINES]) {
      expect([line, cloudWatchTermsMatch(sync.Properties.FilterPattern, line)]).toEqual([line, false]);
    }
    // The escrow Lambda's JSON audit filter is still there, unchanged, and nothing else publishes
    // the metric (escrow-cron's sweep freeze is a repair of an old dispute).
    const escrow = filterOn(template, ESCROW_LG, 'EscrowDispute');
    expect(escrow.Properties.FilterPattern).toBe('{ ($.category = "ESCROW") && ($.action = "DISPUTE") }');
    expect(filtersPublishing(template, 'EscrowDispute').map((f) => f.Properties.LogGroupName).sort()).toEqual(
      [ESCROW_LG, SYNC_LG].sort(),
    );
  });

  test('no money filter counts a sync dispute (it is a rate, not a failure)', () => {
    const template = monitoringTemplate(true);
    for (const metric of [
      'EscrowSettlementFailures', 'EscrowSettlementUnknownOutcomes', 'OrderEscrowProblems', 'ManualPayoutsOverdue',
      'AdminQueueOverdue', 'TruckerPayoutsOverdue', 'DisputeVerdictOrderMismatch', 'DisputeVerdictSettlementBlocked',
      'OrderMoneyMismatch', 'OrderPaymentProblems', 'DisputePropagationFailed',
    ]) {
      for (const filter of filtersPublishing(template, metric)) {
        expect([metric, cloudWatchTermsMatch(filter.Properties.FilterPattern, FROZEN_LINE)]).toEqual([metric, false]);
      }
    }
  });

  test('the dispute-rate alarm keeps its shape and says what it counts', () => {
    const template = monitoringTemplate(false);
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'wereda-connect-dev-escrow-dispute-rate',
      Metrics: [customMetric('EscrowDispute', 3600)],
      Threshold: 5,
      ComparisonOperator: 'GreaterThanThreshold',
      EvaluationPeriods: 1,
      TreatMissingData: 'notBreaching',
      AlarmActions: notifiesTopic,
      OKActions: notifiesTopic,
      AlarmDescription: Match.stringLikeRegexp('DELIVERY_DISPUTE_FROZEN'),
    });
  });
});

describe('FUP-27: the new filters cannot be fired by client text', () => {
  function newPatterns(template: Template): string[] {
    return [
      ...filtersPublishing(template, 'DisputePropagationFailed'),
      filterOn(template, SYNC_LG, 'EscrowDispute'),
    ].map((f) => f.Properties.FilterPattern);
  }

  test('every term carries an underscore (the server turns _ into - in client values)', () => {
    const patterns = newPatterns(monitoringTemplate(true));
    expect(patterns).toHaveLength(4);
    for (const pattern of patterns) {
      expect([pattern, filterTerms(pattern).every((t) => t.includes('_'))]).toEqual([pattern, true]);
    }
  });

  test('ids that spell the tokens, as the server logs them, match no new filter', () => {
    const patterns = newPatterns(monitoringTemplate(true));
    const hostile = patterns.flatMap(filterTerms).join('-');
    const defanged = hostile.replace(/_/g, '-');
    // A raw id would fire them: the defang is what keeps them safe.
    expect(patterns.every((p) => cloudWatchTermsMatch(p, `Dispute on delivery ${hostile}`))).toBe(true);
    const lines = [
      `Dispute on delivery ${defanged} by ${defanged}: escrow escrow-order-${defanged} RefusedNotHeld`,
      `Repeated dispute on delivery ${defanged}: escrow escrow-order-${defanged} Frozen`,
      `Dispute mirror for delivery ${defanged} failed: [TimeoutException] ${defanged}`,
      `Freeze for disputed delivery ${defanged} undecided, nothing written: [FormatException] ${defanged}`,
      `Op ${defanged}: status=error, msg=Invalid action: '${defanged}'. Must be create, update, or delete.`,
      `{"timestamp":"2026-10-23T10:00:00Z","category":"ESCROW","action":"DISPUTE","userId":"u-1","correlationId":"${defanged}","entityType":"escrow","entityId":"${defanged}","outcome":"failure"}`,
      `DISPUTE_VERDICT_PROPAGATION_FAILED escrowId=escrow-order-${defanged} orderId=${defanged} deliveryId=${defanged} legs=order:conflict`,
    ];
    for (const line of lines) {
      for (const pattern of patterns) {
        expect([line, cloudWatchTermsMatch(pattern, line)]).toEqual([line, false]);
        expect([line, substringMatch(pattern, line)]).toEqual([line, false]);
      }
    }
  });
});

describe('FUP-27: the filters and the server agree on the tokens', () => {
  const read = (rel: string) => fs.readFileSync(path.join(__dirname, '../../../server/src', rel), 'utf8');

  test('the tokens are MoneyAlarmLog constants and the producers write them through MoneyAlarmLog', () => {
    const moneyAlarmLog = read('Helpers/MoneyAlarmLog.cs');
    expect(moneyAlarmLog).toContain('DisputePropagationFailedToken = "DISPUTE_PROPAGATION_FAILED"');
    expect(moneyAlarmLog).toContain('DeliveryDisputeFrozenToken = "DELIVERY_DISPUTE_FROZEN"');
    // The mirror writes through the money alarm sink (stdout in every Lambda).
    expect(read('Services/EscrowService.cs')).toContain('AlarmLog(MoneyAlarmLog.DisputePropagationFailed(');
    expect(read('Functions/SyncFunction.cs')).toContain('MoneyAlarmLog.DeliveryDisputeFrozen(');
  });
});
