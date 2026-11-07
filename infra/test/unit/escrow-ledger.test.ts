import * as cdk from 'aws-cdk-lib';
import { Template, Match } from 'aws-cdk-lib/assertions';
import { DatabaseStack } from '../../lib/stacks/database-stack';
import { ApiStack } from '../../lib/stacks/api-stack';
import { MonitoringStack } from '../../lib/stacks/monitoring-stack';
import { environments } from '../../lib/config/environments';

// ESC-20: the escrow ledger (escrow-protocol.md 2.2.0, P.3, P.6 and P.12): its table and LedgerOpenIndex, the
// Lambdas that read and write it with the least actions each needs, the 15 minute ledger sweep, the flag and
// the two money alarms. Kept out of stack.test.ts on purpose (sibling branches edit that file).

const devConfig = environments['dev'];

type Statement = { Action: string | string[]; Resource: unknown; Effect?: string };

function stacks(live: boolean, extra: Partial<typeof devConfig> = {}): { db: Template; api: Template } {
  const app = new cdk.App();
  const db = new DatabaseStack(app, 'LedgerDb', {
    env: { account: '123456789012', region: devConfig.region },
    envConfig: devConfig,
  });
  const api = new ApiStack(app, 'LedgerApi', {
    env: { account: '123456789012', region: devConfig.region },
    envConfig: { ...devConfig, enableTelebirrLive: live, ...extra },
    usersTable: db.usersTable,
    cropsTable: db.cropsTable,
    tripsTable: db.tripsTable,
    rateLimitsTable: db.rateLimitsTable,
    syncMetadataTable: db.syncMetadataTable,
    deliveriesTable: db.deliveriesTable,
    paymentsTable: db.paymentsTable,
    escrowsTable: db.escrowsTable,
    escrowAuditTable: db.escrowAuditTable,
    escrowLedgerTable: db.escrowLedgerTable,
    listingReportsTable: db.listingReportsTable,
    reputationEventsTable: db.reputationEventsTable,
    escrowIdempotencyTable: db.escrowIdempotencyTable,
    deliveryQrSecretsTable: db.deliveryQrSecretsTable,
    ordersTable: db.ordersTable,
    fraudSignalsTable: db.fraudSignalsTable,
    intentsTable: db.intentsTable,
    deviceTokensTable: db.deviceTokensTable,
    matchEventsTable: db.matchEventsTable,
    ratingsTable: db.ratingsTable,
    messagesTable: db.messagesTable,
    notificationsTable: db.notificationsTable,
    tripLocationsTable: db.tripLocationsTable,
    transactionAuditTable: db.transactionAuditTable,
    uploadsBucket: db.uploadsBucket,
  });
  return { db: Template.fromStack(db), api: Template.fromStack(api) };
}

function functionId(template: Template, functionName: string): string {
  const fns = template.findResources('AWS::Lambda::Function');
  const id = Object.keys(fns).find((key) => fns[key].Properties?.FunctionName === functionName);
  if (!id) {
    throw new Error(`no Lambda named ${functionName}`);
  }
  return id;
}

function statementsOf(template: Template, functionName: string): Statement[] {
  const fn = template.findResources('AWS::Lambda::Function')[functionId(template, functionName)];
  const roleId = (fn.Properties.Role as { 'Fn::GetAtt': [string, string] })['Fn::GetAtt'][0];
  // A role whose inline policy is full spills into CDK overflow managed policies; both carry Roles.
  return [...Object.values(template.findResources('AWS::IAM::Policy')), ...Object.values(template.findResources('AWS::IAM::ManagedPolicy'))]
    .filter((p) => JSON.stringify(p.Properties.Roles).includes(`"${roleId}"`))
    .flatMap((p) => p.Properties.PolicyDocument.Statement as Statement[]);
}

/** The actions a Lambda may take on the escrow ledger table (or its index). */
function ledgerActions(template: Template, functionName: string): string[] {
  return [
    ...new Set(
      statementsOf(template, functionName)
        .filter((s) => JSON.stringify(s.Resource).includes('EscrowLedgerTable'))
        .flatMap((s) => (Array.isArray(s.Action) ? s.Action : [s.Action])),
    ),
  ].sort();
}

const fn = (name: string) => `wereda-connect-dev-${name}`;

describe('ESC-20: the escrow ledger table', () => {
  const { db } = stacks(false);

  test('is keyed by escrow and ref, on demand, with point-in-time recovery and no TTL', () => {
    db.hasResourceProperties('AWS::DynamoDB::Table', {
      TableName: 'wereda-connect-dev-escrow-ledger',
      BillingMode: 'PAY_PER_REQUEST',
      KeySchema: [
        { AttributeName: 'escrowId', KeyType: 'HASH' },
        { AttributeName: 'ref', KeyType: 'RANGE' },
      ],
      PointInTimeRecoverySpecification: { PointInTimeRecoveryEnabled: true },
      TimeToLiveSpecification: Match.absent(),
    });
  });

  test('has the sparse LedgerOpenIndex on openStatus and dueAt, projecting every attribute', () => {
    db.hasResourceProperties('AWS::DynamoDB::Table', {
      TableName: 'wereda-connect-dev-escrow-ledger',
      GlobalSecondaryIndexes: [
        {
          IndexName: 'LedgerOpenIndex',
          KeySchema: [
            { AttributeName: 'openStatus', KeyType: 'HASH' },
            { AttributeName: 'dueAt', KeyType: 'RANGE' },
          ],
          Projection: { ProjectionType: 'ALL' },
        },
      ],
    });
  });
});

describe.each([false, true])('ESC-20: the Lambdas and the ledger (live=%s)', (live) => {
  const { api } = stacks(live);

  test.each(['escrow', 'escrow-cron', 'admin', 'sync'])('%s carries ESCROW_LEDGER_TABLE', (name) => {
    api.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: fn(name),
      Environment: { Variables: Match.objectLike({ ESCROW_LEDGER_TABLE: Match.anyValue() }) },
    });
  });

  test('sync carries ESCROW_LEDGER_ENABLED, on in dev', () => {
    api.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: fn('sync'),
      Environment: { Variables: Match.objectLike({ ESCROW_LEDGER_ENABLED: 'true' }) },
    });
  });

  test('escrow and escrow-cron read, put and update ledger rows; nothing deletes, batch-writes or scans them', () => {
    for (const name of ['escrow', 'escrow-cron']) {
      expect([name, ledgerActions(api, fn(name))]).toEqual([
        name,
        ['dynamodb:GetItem', 'dynamodb:PutItem', 'dynamodb:Query', 'dynamodb:UpdateItem'],
      ]);
    }
  });

  test('admin reads and records ledger rows but never puts one', () => {
    expect(ledgerActions(api, fn('admin'))).toEqual(['dynamodb:GetItem', 'dynamodb:Query', 'dynamodb:UpdateItem']);
  });

  test('sync only marks the escrow row: no ledger grant', () => {
    expect(ledgerActions(api, fn('sync'))).toEqual([]);
  });

  test('the ledger statements cover the table and its index only', () => {
    for (const name of ['escrow', 'escrow-cron', 'admin']) {
      const resources = statementsOf(api, fn(name))
        .filter((s) => JSON.stringify(s.Resource).includes('EscrowLedgerTable'))
        .map((s) => JSON.stringify(s.Resource));
      expect(resources.length).toBeGreaterThan(0);
      for (const resource of resources) {
        expect(resource).not.toContain('EscrowsTable');
      }
    }
  });

  test('a 15 minute rule invokes escrow-cron with the ledger_sweep body', () => {
    api.hasResourceProperties('AWS::Events::Rule', {
      Name: 'wereda-connect-dev-escrow-cron-ledger',
      ScheduleExpression: 'rate(15 minutes)',
      Targets: [
        Match.objectLike({
          Arn: { 'Fn::GetAtt': [functionId(api, fn('escrow-cron')), 'Arn'] },
          Input: JSON.stringify({ body: JSON.stringify({ action: 'ledger_sweep' }) }),
        }),
      ],
    });
  });
});

describe('ESC-20: the Telebirr webhook and the ledger (live)', () => {
  const { api } = stacks(true);

  test('carries the table and the flag, puts a duplicate refund row only, and reads the audit trail', () => {
    api.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: fn('telebirr-webhook'),
      Environment: {
        Variables: Match.objectLike({ ESCROW_LEDGER_TABLE: Match.anyValue(), ESCROW_LEDGER_ENABLED: 'true' }),
      },
    });
    expect(ledgerActions(api, fn('telebirr-webhook'))).toEqual(['dynamodb:PutItem']);
    const onAudit = statementsOf(api, fn('telebirr-webhook'))
      .filter((s) => JSON.stringify(s.Resource).includes('EscrowAuditTable'))
      .flatMap((s) => (Array.isArray(s.Action) ? s.Action : [s.Action]));
    expect(onAudit).toEqual(expect.arrayContaining(['dynamodb:Query']));
  });
});

describe('ESC-20: the ledger flag per environment', () => {
  test('is on in dev and off in staging and prod', () => {
    expect(environments['dev'].escrowLedgerEnabled).toBe(true);
    expect(environments['staging'].escrowLedgerEnabled ?? false).toBe(false);
    expect(environments['prod'].escrowLedgerEnabled ?? false).toBe(false);
  });

  test('off, sync says false and the table stays wired (escrows already on the ledger keep settling)', () => {
    const { api } = stacks(false, { escrowLedgerEnabled: false });
    api.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: fn('sync'),
      Environment: {
        Variables: Match.objectLike({ ESCROW_LEDGER_ENABLED: 'false', ESCROW_LEDGER_TABLE: Match.anyValue() }),
      },
    });
    expect(ledgerActions(api, fn('escrow-cron')).length).toBe(4);
  });
});

describe('ESC-20: ledger alarms', () => {
  const app = new cdk.App();
  const monitoring = Template.fromStack(
    new MonitoringStack(app, 'LedgerMonitoring', {
      env: { account: '123456789012', region: devConfig.region },
      envConfig: devConfig,
      apiUrl: `https://test.execute-api.${devConfig.region}.amazonaws.com`,
      apiId: 'testapi123',
    }),
  );

  test('LEDGER_ROW_OVERDUE in the escrow-cron log group alarms as ledger-rows-overdue', () => {
    monitoring.hasResourceProperties('AWS::Logs::MetricFilter', {
      LogGroupName: '/aws/lambda/wereda-connect-dev-escrow-cron',
      FilterPattern: '"LEDGER_ROW_OVERDUE"',
      MetricTransformations: [Match.objectLike({ MetricName: 'LedgerRowsOverdue', MetricValue: '1' })],
    });
    monitoring.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'wereda-connect-dev-ledger-rows-overdue',
      Threshold: 0,
      ComparisonOperator: 'GreaterThanThreshold',
      AlarmDescription: Match.stringLikeRegexp('LEDGER_ROW_OVERDUE'),
    });
  });

  test('ESCROW_LEDGER_INVARIANT in the escrow, escrow-cron and admin log groups alarms as escrow-ledger-invariant', () => {
    for (const logGroup of [
      '/aws/lambda/wereda-connect-dev-escrow',
      '/aws/lambda/wereda-connect-dev-escrow-cron',
      '/aws/lambda/wereda-connect-dev-admin',
    ]) {
      monitoring.hasResourceProperties('AWS::Logs::MetricFilter', {
        LogGroupName: logGroup,
        FilterPattern: '"ESCROW_LEDGER_INVARIANT"',
        MetricTransformations: [Match.objectLike({ MetricName: 'EscrowLedgerInvariant', MetricValue: '1' })],
      });
    }
    monitoring.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'wereda-connect-dev-escrow-ledger-invariant',
      AlarmDescription: Match.stringLikeRegexp('ESCROW_LEDGER_INVARIANT'),
    });
  });

  test('the alarm descriptions fit CloudWatch (1,024 characters)', () => {
    const alarms = Object.values(monitoring.findResources('AWS::CloudWatch::Alarm')) as Array<{
      Properties: { AlarmName: string; AlarmDescription?: string };
    }>;
    for (const name of ['wereda-connect-dev-ledger-rows-overdue', 'wereda-connect-dev-escrow-ledger-invariant']) {
      const alarm = alarms.find((a) => a.Properties.AlarmName === name);
      expect((alarm?.Properties.AlarmDescription ?? '').length).toBeLessThanOrEqual(1024);
    }
  });
});
