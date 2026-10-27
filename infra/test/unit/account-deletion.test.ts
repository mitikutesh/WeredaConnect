import * as cdk from 'aws-cdk-lib';
import { Template, Match } from 'aws-cdk-lib/assertions';
import { DatabaseStack } from '../../lib/stacks/database-stack';
import { ApiStack } from '../../lib/stacks/api-stack';
import { MonitoringStack } from '../../lib/stacks/monitoring-stack';
import { environments } from '../../lib/config/environments';

// ACC-02: DELETE /user/account and its completion sweep (account-deletion.md). Kept out of
// stack.test.ts on purpose (sibling branches edit that file).

const devConfig = environments['dev'];

type Statement = { Action: string | string[]; Resource: unknown; Effect?: string };

function apiTemplate(): Template {
  const app = new cdk.App();
  const db = new DatabaseStack(app, 'DbStack', {
    env: { account: '123456789012', region: devConfig.region },
    envConfig: devConfig,
  });
  const api = new ApiStack(app, 'TestApiStack', {
    env: { account: '123456789012', region: devConfig.region },
    envConfig: devConfig,
    usersTable: db.usersTable,
    cropsTable: db.cropsTable,
    tripsTable: db.tripsTable,
    rateLimitsTable: db.rateLimitsTable,
    syncMetadataTable: db.syncMetadataTable,
    deliveriesTable: db.deliveriesTable,
    paymentsTable: db.paymentsTable,
    escrowsTable: db.escrowsTable,
    escrowAuditTable: db.escrowAuditTable,
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
    transactionAuditTable: db.transactionAuditTable,
    uploadsBucket: db.uploadsBucket,
  });
  return Template.fromStack(api);
}

/** Logical id of the Lambda with this function name. */
function functionId(template: Template, functionName: string): string {
  const fns = template.findResources('AWS::Lambda::Function');
  const id = Object.keys(fns).find((key) => fns[key].Properties?.FunctionName === functionName);
  if (!id) {
    throw new Error(`no Lambda named ${functionName}`);
  }
  return id;
}

/** Every policy statement attached to the role of the Lambda with this function name. */
function statementsOf(template: Template, functionName: string): Statement[] {
  const fn = template.findResources('AWS::Lambda::Function')[functionId(template, functionName)];
  const roleId = (fn.Properties.Role as { 'Fn::GetAtt': [string, string] })['Fn::GetAtt'][0];
  return Object.values(template.findResources('AWS::IAM::Policy'))
    .filter((p) => JSON.stringify(p.Properties.Roles).includes(`"${roleId}"`))
    .flatMap((p) => p.Properties.PolicyDocument.Statement as Statement[]);
}

/** The actions a Lambda may take on resources whose JSON mentions `resourceHint`. */
function actionsOn(template: Template, functionName: string, resourceHint: string): string[] {
  return statementsOf(template, functionName)
    .filter((s) => JSON.stringify(s.Resource).includes(resourceHint))
    .flatMap((s) => (Array.isArray(s.Action) ? s.Action : [s.Action]))
    .sort();
}

const API_FN = 'wereda-connect-dev-account-deletion';
const SWEEP_FN = 'wereda-connect-dev-account-deletion-sweep';

describe('ACC-02 account deletion (ApiStack)', () => {
  let template: Template;

  beforeAll(() => {
    template = apiTemplate();
  });

  test('DELETE /user/account sits on the mobile JWT authorizer and reaches the deletion Lambda', () => {
    const authorizers = template.findResources('AWS::ApiGatewayV2::Authorizer');
    const mobileAuthorizerId = Object.entries(authorizers)
      .find(([, r]) => (r as { Properties?: { Name?: string } }).Properties?.Name === 'CognitoAuthorizer')?.[0];
    expect(mobileAuthorizerId).toBeDefined();
    template.hasResourceProperties('AWS::ApiGatewayV2::Route', {
      RouteKey: 'DELETE /user/account',
      AuthorizationType: 'JWT',
      AuthorizerId: { Ref: mobileAuthorizerId },
    });

    const integrations = template.findResources('AWS::ApiGatewayV2::Integration');
    const deletionIntegration = Object.values(integrations).find((i) =>
      JSON.stringify(i.Properties.IntegrationUri).includes(functionId(template, API_FN)));
    expect(deletionIntegration?.Properties.PayloadFormatVersion).toBe('1.0');
  });

  test('the browser preflight allows DELETE', () => {
    template.hasResourceProperties('AWS::ApiGatewayV2::Api', {
      CorsConfiguration: Match.objectLike({ AllowMethods: Match.arrayWith(['DELETE']) }),
    });
  });

  test('two Lambdas run the one handler: the request (30 s) and the sweep (5 minutes)', () => {
    for (const [name, timeout] of [[API_FN, 30], [SWEEP_FN, 300]] as const) {
      template.hasResourceProperties('AWS::Lambda::Function', {
        FunctionName: name,
        Timeout: timeout,
        Environment: {
          Variables: Match.objectLike({
            FUNCTION_HANDLER: 'AccountDeletionFunction',
            USERS_TABLE: Match.anyValue(),
            CROPS_TABLE: Match.anyValue(),
            TRIPS_TABLE: Match.anyValue(),
            ORDERS_TABLE: Match.anyValue(),
            DELIVERIES_TABLE: Match.anyValue(),
            ESCROWS_TABLE: Match.anyValue(),
            ESCROW_AUDIT_TABLE: Match.anyValue(),
            ESCROW_IDEMPOTENCY_TABLE: Match.anyValue(),
            TRANSACTION_AUDIT_TABLE: Match.anyValue(),
            RATINGS_TABLE: Match.anyValue(),
            MESSAGES_TABLE: Match.anyValue(),
            INTENTS_TABLE: Match.anyValue(),
            MATCH_EVENTS_TABLE: Match.anyValue(),
            DEVICE_TOKENS_TABLE: Match.anyValue(),
            COGNITO_USER_POOL_ID: Match.anyValue(),
            COGNITO_CLIENT_ID: Match.anyValue(),
          }),
        },
      });
    }
  });

  test('Cognito: global sign-out and delete only, on the mobile user pool', () => {
    for (const fn of [API_FN, SWEEP_FN]) {
      const cognito = statementsOf(template, fn).filter((s) =>
        (Array.isArray(s.Action) ? s.Action : [s.Action]).some((a) => a.startsWith('cognito-idp:')));
      expect(cognito).toHaveLength(1);
      expect((cognito[0].Action as string[]).sort()).toEqual([
        'cognito-idp:AdminDeleteUser',
        'cognito-idp:AdminUserGlobalSignOut',
      ]);
      expect(JSON.stringify(cognito[0].Resource)).toContain('UserPool');
      expect(JSON.stringify(cognito[0].Resource)).not.toContain('AdminUserPool');
    }
  });

  test('SNS: DeleteEndpoint on the FCM endpoints only', () => {
    for (const fn of [API_FN, SWEEP_FN]) {
      const sns = statementsOf(template, fn).filter((s) =>
        (Array.isArray(s.Action) ? s.Action : [s.Action]).some((a) => a.startsWith('sns:')));
      expect(sns).toHaveLength(1);
      expect(sns[0].Action).toBe('sns:DeleteEndpoint');
      expect(JSON.stringify(sns[0].Resource)).toContain(':endpoint/GCM/wereda-connect-dev-fcm/*');
    }
  });

  test('DynamoDB grants are per action: money and audit rows are never put or deleted', () => {
    const expected: Record<string, string[]> = {
      UsersTable: ['dynamodb:GetItem', 'dynamodb:UpdateItem'],
      CropsTable: ['dynamodb:GetItem', 'dynamodb:Query', 'dynamodb:UpdateItem'],
      TripsTable: ['dynamodb:GetItem', 'dynamodb:Query', 'dynamodb:UpdateItem'],
      OrdersTable: ['dynamodb:GetItem', 'dynamodb:Query', 'dynamodb:UpdateItem'],
      DeliveriesTable: ['dynamodb:Query', 'dynamodb:UpdateItem'],
      EscrowsTable: ['dynamodb:Query', 'dynamodb:UpdateItem'],
      EscrowAuditTable: ['dynamodb:Query'],
      EscrowIdempotencyTable: ['dynamodb:GetItem'],
      TransactionAuditTable: ['dynamodb:PutItem'],
      RatingsTable: ['dynamodb:GetItem', 'dynamodb:Query', 'dynamodb:UpdateItem'],
      MessagesTable: ['dynamodb:DeleteItem', 'dynamodb:Query'],
      IntentsTable: ['dynamodb:DeleteItem', 'dynamodb:Query'],
      MatchEventsTable: ['dynamodb:DeleteItem', 'dynamodb:Query'],
      DeviceTokensTable: ['dynamodb:DeleteItem', 'dynamodb:Query'],
    };
    for (const [table, actions] of Object.entries(expected)) {
      expect([table, actionsOn(template, API_FN, table)]).toEqual([table, actions]);
    }
    // No other table (payments, fraud signals, rate limits, sync metadata, QR secrets).
    for (const table of ['PaymentsTable', 'FraudSignalsTable', 'RateLimitsTable', 'SyncMetadataTable', 'DeliveryQrSecretsTable']) {
      expect([table, actionsOn(template, API_FN, table)]).toEqual([table, []]);
    }
  });

  test('only the sweep scans the users table and reads the account audit trail', () => {
    expect(actionsOn(template, API_FN, 'UsersTable')).not.toContain('dynamodb:Scan');
    expect(actionsOn(template, SWEEP_FN, 'UsersTable')).toContain('dynamodb:Scan');
    expect(actionsOn(template, API_FN, 'TransactionAuditTable')).toEqual(['dynamodb:PutItem']);
    expect(actionsOn(template, SWEEP_FN, 'TransactionAuditTable')).toEqual(['dynamodb:PutItem', 'dynamodb:Query']);
  });

  test('the sweep runs every 6 hours (three tries within 24 hours) with the sweep action wrapped as body', () => {
    template.hasResourceProperties('AWS::Events::Rule', {
      Name: 'wereda-connect-dev-account-deletion-sweep',
      ScheduleExpression: 'rate(6 hours)',
      Targets: [
        Match.objectLike({
          Arn: { 'Fn::GetAtt': [functionId(template, SWEEP_FN), 'Arn'] },
          Input: JSON.stringify({ body: JSON.stringify({ action: 'sweep' }) }),
        }),
      ],
    });
  });

  test('device tokens: the register refusal reads the users table (GetItem only)', () => {
    template.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: 'wereda-connect-dev-device-tokens',
      Environment: { Variables: Match.objectLike({ USERS_TABLE: Match.anyValue() }) },
    });
    expect(actionsOn(template, 'wereda-connect-dev-device-tokens', 'UsersTable')).toEqual(['dynamodb:GetItem']);
  });
});

describe('ACC-02 account deletion (MonitoringStack)', () => {
  const app = new cdk.App();
  const template = Template.fromStack(new MonitoringStack(app, 'TestMonitoringStack', {
    env: { account: '123456789012', region: devConfig.region },
    envConfig: devConfig,
    apiUrl: `https://test.execute-api.${devConfig.region}.amazonaws.com`,
    apiId: 'testapi123',
  }));

  test('a deletion the sweep fails on twice alarms (ACCOUNT_DELETION_STUCK on the sweep log group)', () => {
    template.hasResourceProperties('AWS::Logs::MetricFilter', {
      LogGroupName: '/aws/lambda/wereda-connect-dev-account-deletion-sweep',
      FilterPattern: '"ACCOUNT_DELETION_STUCK"',
      MetricTransformations: [Match.objectLike({ MetricName: 'AccountDeletionStuck', MetricValue: '1' })],
    });
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'wereda-connect-dev-account-deletion-stuck',
      EvaluationPeriods: 7,
      DatapointsToAlarm: 1,
      Threshold: 0,
      TreatMissingData: 'notBreaching',
      AlarmActions: Match.arrayWith([Match.objectLike({ Ref: Match.stringLikeRegexp('^AlarmTopic') })]),
      Metrics: [
        Match.objectLike({
          MetricStat: Match.objectLike({
            Metric: Match.objectLike({ MetricName: 'AccountDeletionStuck', Namespace: 'WeredaConnect/dev' }),
            Period: 3600,
            Stat: 'Sum',
          }),
        }),
      ],
    });
  });
});
