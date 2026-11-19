import * as cdk from 'aws-cdk-lib';
import { Template, Match } from 'aws-cdk-lib/assertions';
import { DatabaseStack } from '../../lib/stacks/database-stack';
import { ApiStack } from '../../lib/stacks/api-stack';
import { MonitoringStack } from '../../lib/stacks/monitoring-stack';
import { environments, EnvironmentConfig } from '../../lib/config/environments';

// Persona simulation BUG-6 (escrow-protocol.md 2.28.0): every Lambda that creates a dispute reads the delivery's
// transaction-audit rows for the handoff evidence, through the entity index only. Kept out of stack.test.ts.

const devConfig = environments['dev'];

type Statement = { Action: string | string[]; Resource: unknown; Effect?: string; Condition?: unknown };

function apiTemplate(envConfig: EnvironmentConfig = devConfig): Template {
  const app = new cdk.App();
  const db = new DatabaseStack(app, 'DbStack', {
    env: { account: '123456789012', region: envConfig.region },
    envConfig,
  });
  const api = new ApiStack(app, 'TestApiStack', {
    env: { account: '123456789012', region: envConfig.region },
    envConfig,
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
    listingReportsTable: db.listingReportsTable,
    reputationEventsTable: db.reputationEventsTable,
    transactionAuditTable: db.transactionAuditTable,
    uploadsBucket: db.uploadsBucket,
  });
  return Template.fromStack(api);
}

function functionOf(template: Template, functionName: string): { id: string; props: Record<string, any> } {
  const fns = template.findResources('AWS::Lambda::Function');
  const id = Object.keys(fns).find((key) => fns[key].Properties?.FunctionName === functionName);
  if (!id) {
    throw new Error(`no Lambda named ${functionName}`);
  }
  return { id, props: fns[id].Properties };
}

function statementsOf(template: Template, functionName: string): Statement[] {
  const fn = functionOf(template, functionName).props;
  const roleId = (fn.Role as { 'Fn::GetAtt': [string, string] })['Fn::GetAtt'][0];
  return Object.values(template.findResources('AWS::IAM::Policy'))
    .filter((p) => JSON.stringify(p.Properties.Roles).includes(`"${roleId}"`))
    .flatMap((p) => p.Properties.PolicyDocument.Statement as Statement[]);
}

const actionsOf = (s: Statement): string[] => (Array.isArray(s.Action) ? s.Action : [s.Action]);

/** Whether a statement's resources mention `hint` (a table's logical id, an ARN fragment). */
const mentions = (s: Statement, hint: string) => JSON.stringify(s.Resource).includes(hint);

describe('BUG-6 dispute evidence reads (ApiStack)', () => {
  const template = apiTemplate();
  const disputers = ['sync', 'escrow', 'escrow-cron', 'order-cron'].map((n) => `wereda-connect-dev-${n}`);

  test.each(disputers)('%s may Query the transaction-audit entity index', (fn) => {
    const statements = statementsOf(template, fn);
    const index = statements.filter((s) => mentions(s, '/index/EntityIndex') && mentions(s, 'TransactionAudit'));
    expect(index.some((s) => actionsOf(s).includes('dynamodb:Query'))).toBe(true);
  });

  test.each(disputers)('%s gets the Query on the index only, never a Query or Scan of the whole table', (fn) => {
    const statements = statementsOf(template, fn).filter((s) => mentions(s, 'TransactionAudit'));
    for (const s of statements) {
      const resources = JSON.stringify(s.Resource);
      if (actionsOf(s).includes('dynamodb:Query')) {
        expect(resources).toContain('/index/EntityIndex');
      }
      expect(actionsOf(s)).not.toContain('dynamodb:Scan');
    }
  });
});
