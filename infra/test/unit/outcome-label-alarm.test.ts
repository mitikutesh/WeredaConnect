import * as fs from 'fs';
import * as path from 'path';
import * as cdk from 'aws-cdk-lib';
import { Template, Match } from 'aws-cdk-lib/assertions';
import { MonitoringStack } from '../../lib/stacks/monitoring-stack';
import { environments } from '../../lib/config/environments';

// FUP-142: a metric filter and an alarm on OUTCOME_LABEL_FAILED (FRD-18, fraud spec section 20.1).
// The helpers of stack.test.ts are repeated here on purpose (sibling branches edit that file). The
// deployed filter is not yet checked against the real service (FUP-142's synthetic check part).

const devConfig = environments['dev'];
const lg = (suffix: string) => `/aws/lambda/wereda-connect-dev-${suffix}`;

// The exact lines the server writes: OutcomeLabelWriter.WriteAsync (admin and escrow Lambdas) and
// the short form of EscrowService.LabelHeldExitAsync.
const FAILED_LINES = [
  'OUTCOME_LABEL_FAILED case=fraud_signal outcome=fraud_confirmed caseId=sig-1 error=ProvisionedThroughputExceededException',
  'OUTCOME_LABEL_FAILED case=dispute outcome=buyer_wins caseId=escrow-order-ord-1 error=AmazonDynamoDBException',
  'OUTCOME_LABEL_FAILED case=held_escrow outcome=hold_released caseId=escrow-order-ord-1 error=TimeoutException',
  'OUTCOME_LABEL_FAILED case=held_escrow error=NullReferenceException',
];
// The other label lines (a written or existing row is not a failure) and neighbours.
const OTHER_LINES = [
  'OUTCOME_LABEL_WRITTEN case=dispute outcome=farmer_wins caseId=escrow-order-ord-1',
  'OUTCOME_LABEL_EXISTS case=fraud_signal outcome=false_positive caseId=sig-1',
  'OUTCOME_LABEL_EXISTS case=fraud_signal outcome=whitelisted caseId=sig-1 reason=same_decision',
  'FRAUD_HOLD_EXIT_AUDIT_FAILED escrowId=escrow-order-ord-1 op=release error=TimeoutException',
  'ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-1 action=release_failed outcome=failed code=TELEBIRR_TIMEOUT',
  'RISK_SCORE_UNAVAILABLE userId=3f2a-user error=ProvisionedThroughputExceededException',
];
// The Lambda runtime's prefix: time, request id and level, tab separated.
const prefixed = (level: string, line: string) => `2026-11-07T10:00:00.000Z\t4f1c7a2e-req\t${level}\t${line}`;

type MetricFilterResource = {
  Properties: {
    LogGroupName: string;
    FilterPattern: string;
    MetricTransformations: Array<{ MetricName: string; MetricNamespace: string; MetricValue: string; DefaultValue?: number }>;
  };
};

function monitoringTemplate(enableTelebirrLive: boolean): Template {
  const app = new cdk.App();
  const stack = new MonitoringStack(app, 'OutcomeLabelMonitoring', {
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

const notifiesTopic = Match.arrayWith([Match.objectLike({ Ref: Match.stringLikeRegexp('^AlarmTopic') })]);

describe('FUP-142: outcome label write failures', () => {
  test.each([false, true])('the token is counted on the admin, escrow and escrow-cron log groups only, bare and prefixed (live %s)', (live) => {
    const template = monitoringTemplate(live);
    const filters = allFilters(template).filter((f) => f.Properties.MetricTransformations[0].MetricName === 'OutcomeLabelFailed');
    expect(filters.map((f) => f.Properties.LogGroupName).sort()).toEqual([lg('admin'), lg('escrow'), lg('escrow-cron')]);
    for (const filter of filters) {
      expect(filter.Properties.FilterPattern).toBe('"OUTCOME_LABEL_FAILED"');
      // No default: billed only in the hours a line appears; the alarm treats missing data as OK.
      expect(filter.Properties.MetricTransformations[0]).toEqual({
        MetricName: 'OutcomeLabelFailed',
        MetricNamespace: 'WeredaConnect/dev',
        MetricValue: '1',
      });
      for (const line of FAILED_LINES) {
        for (const event of [line, prefixed('warn', line), prefixed('info', line)]) {
          expect([event, cloudWatchTermsMatch(filter.Properties.FilterPattern, event)]).toEqual([event, true]);
        }
      }
      for (const line of OTHER_LINES) {
        expect([line, cloudWatchTermsMatch(filter.Properties.FilterPattern, line)]).toEqual([line, false]);
      }
    }
    // No other filter of the stack counts a label failure.
    for (const filter of allFilters(template).filter((f) => f.Properties.MetricTransformations[0].MetricName !== 'OutcomeLabelFailed')) {
      const metric = filter.Properties.MetricTransformations[0].MetricName;
      expect([metric, FAILED_LINES.some((line) => cloudWatchTermsMatch(filter.Properties.FilterPattern, line))]).toEqual([metric, false]);
    }
  });

  test.each([false, true])('alarm: any line in 15 minutes, held 5 periods, notifies the alarm topic, on the dashboard (live %s)', (live) => {
    const template = monitoringTemplate(live);
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'wereda-connect-dev-outcome-label-failed',
      Metrics: [
        Match.objectLike({
          MetricStat: Match.objectLike({
            Metric: Match.objectLike({ MetricName: 'OutcomeLabelFailed', Namespace: 'WeredaConnect/dev' }),
            Period: 900,
            Stat: 'Sum',
          }),
        }),
      ],
      Threshold: 0,
      ComparisonOperator: 'GreaterThanThreshold',
      EvaluationPeriods: 5,
      DatapointsToAlarm: 1,
      TreatMissingData: 'notBreaching',
      AlarmActions: notifiesTopic,
      OKActions: notifiesTopic,
      AlarmDescription: Match.stringLikeRegexp('OUTCOME_LABEL_FAILED'),
    });
    const alarms = Object.values(template.findResources('AWS::CloudWatch::Alarm')) as Array<{
      Properties: { AlarmName: string; AlarmDescription?: string };
    }>;
    const alarm = alarms.find((a) => a.Properties.AlarmName === 'wereda-connect-dev-outcome-label-failed');
    expect((alarm?.Properties.AlarmDescription ?? '').length).toBeLessThanOrEqual(1024);
    const dashboard = JSON.stringify(template.findResources('AWS::CloudWatch::Dashboard'));
    expect(dashboard).toMatch(/"Fn::GetAtt":\["OutcomeLabelFailed[0-9A-F]+","Arn"\]/);
  });

  test('client text cannot spell the token once the server wrote it (it carries an underscore)', () => {
    const [filter] = allFilters(monitoringTemplate(false)).filter(
      (f) => f.Properties.MetricTransformations[0].MetricName === 'OutcomeLabelFailed',
    );
    const defanged = 'OUTCOME_LABEL_FAILED'.replace(/_/g, '-');
    for (const line of [
      `OUTCOME_LABEL_WRITTEN case=fraud_signal outcome=false_positive caseId=${defanged}`,
      `Admin fraud action on signal ${defanged}: [TimeoutException]`,
      `{"timestamp":"2026-11-07T10:00:00Z","category":"ADMIN","action":"FRAUD_ACTION","userId":"u-1","entityId":"${defanged}","outcome":"success"}`,
    ]) {
      expect([line, cloudWatchTermsMatch(filter.Properties.FilterPattern, line)]).toEqual([line, false]);
    }
  });

  test('the producers write the token, and caseId goes through MoneyAlarmLog.LogId', () => {
    const read = (rel: string) => fs.readFileSync(path.join(__dirname, '../../../server/src', rel), 'utf8');
    const writer = read('Helpers/OutcomeLabelWriter.cs');
    expect(writer).toContain('$"OUTCOME_LABEL_FAILED case={label.Case} outcome={label.Outcome} caseId={MoneyAlarmLog.LogId(label.CaseId)} error={ex.GetType().Name}"');
    expect(read('Services/EscrowService.cs')).toContain('OUTCOME_LABEL_FAILED case={OutcomeLabels.CaseHeldEscrow}');
    // The callers: the admin Lambda (fraud decision, dispute verdict) and EscrowService (held exit,
    // run by the escrow and escrow-cron Lambdas). A new caller elsewhere needs its log group added.
    const callers = fs
      .readdirSync(path.join(__dirname, '../../../server/src'), { recursive: true, encoding: 'utf8' })
      .filter((f) => f.endsWith('.cs') && !f.endsWith('OutcomeLabelWriter.cs'))
      .filter((f) => read(f).includes('OutcomeLabelWriter.WriteAsync('))
      .map((f) => f.split(path.sep).join('/'))
      .sort();
    expect(callers).toEqual(['Functions/AdminFunction.cs', 'Services/EscrowService.cs']);
  });
});
