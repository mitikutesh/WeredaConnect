import * as cdk from 'aws-cdk-lib';
import { Template, Match } from 'aws-cdk-lib/assertions';
import { DatabaseStack } from '../../lib/stacks/database-stack';
import { ApiStack } from '../../lib/stacks/api-stack';
import { MonitoringStack } from '../../lib/stacks/monitoring-stack';
import { environments, EnvironmentConfig } from '../../lib/config/environments';

// ESC-28: the reputation core (reputation-architecture.md sections 3, 4, 7, 8, 9, 12.1 and 14). Kept out
// of stack.test.ts on purpose (sibling branches edit that file).

const devConfig = environments['dev'];
const ACCOUNT = '123456789012';

type Statement = { Action: string | string[]; Resource: unknown; Effect?: string };
type Resource = { Properties: Record<string, any>; DeletionPolicy?: string };

function stacks(envConfig: EnvironmentConfig, environmentContext?: string): { db: Template; api: Template } {
  // The Lambda construct sets reserved concurrency only outside dev (the `environment` context).
  const app = new cdk.App(environmentContext ? { context: { environment: environmentContext } } : {});
  const env = { account: ACCOUNT, region: envConfig.region };
  const db = new DatabaseStack(app, 'RepDb', { env, envConfig });
  const api = new ApiStack(app, 'RepApi', {
    env,
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
    reputationEventsTable: db.reputationEventsTable,
    listingReportsTable: db.listingReportsTable,
    escrowLedgerTable: db.escrowLedgerTable,
    transactionAuditTable: db.transactionAuditTable,
    uploadsBucket: db.uploadsBucket,
  });
  return { db: Template.fromStack(db), api: Template.fromStack(api) };
}

function monitoringTemplate(): Template {
  const app = new cdk.App();
  const stack = new MonitoringStack(app, 'RepMonitoring', {
    env: { account: ACCOUNT, region: devConfig.region },
    envConfig: devConfig,
    apiUrl: `https://test.execute-api.${devConfig.region}.amazonaws.com`,
    apiId: 'testapi123',
  });
  return Template.fromStack(stack);
}

/** The one resource of this type whose properties match, with its logical id. */
function only(template: Template, type: string, props: Record<string, unknown>): [string, Resource] {
  const found = Object.entries(template.findResources(type, { Properties: props }));
  expect([type, props, found.length]).toEqual([type, props, 1]);
  return found[0] as [string, Resource];
}

function lambdaId(template: Template, functionName: string): string {
  return only(template, 'AWS::Lambda::Function', { FunctionName: functionName })[0];
}

function envOf(template: Template, functionName: string): Record<string, unknown> {
  return only(template, 'AWS::Lambda::Function', { FunctionName: functionName })[1].Properties.Environment.Variables;
}

/** Every policy statement attached to the role of the Lambda with this function name. */
function statementsOf(template: Template, functionName: string): Statement[] {
  const fn = only(template, 'AWS::Lambda::Function', { FunctionName: functionName })[1];
  const roleId = (fn.Properties.Role as { 'Fn::GetAtt': [string, string] })['Fn::GetAtt'][0];
  return [
    ...Object.values(template.findResources('AWS::IAM::Policy')),
    ...Object.values(template.findResources('AWS::IAM::ManagedPolicy')),
  ]
    .filter((p) => JSON.stringify(p.Properties.Roles).includes(`"${roleId}"`))
    .flatMap((p) => p.Properties.PolicyDocument.Statement as Statement[]);
}

const actionsOf = (s: Statement): string[] => (Array.isArray(s.Action) ? s.Action : [s.Action]);

/** The statements of a Lambda's role on resources whose JSON mentions `hint`. */
function statementsOn(template: Template, functionName: string, hint: string): Statement[] {
  return statementsOf(template, functionName).filter((s) => JSON.stringify(s.Resource).includes(hint));
}

/** The sorted, unique actions a Lambda may take on resources whose JSON mentions `hint`. */
function actionsOn(template: Template, functionName: string, hint: string): string[] {
  return [...new Set(statementsOn(template, functionName, hint).flatMap(actionsOf))].sort();
}

const STREAM_FN = 'wereda-connect-dev-reputation-stream';
const ORDER_CRON_FN = 'wereda-connect-dev-order-cron';
const SYNC_FN = 'wereda-connect-dev-sync';
const REPUTATION_FN = 'wereda-connect-dev-reputation';
const NOTIFY_FN = 'wereda-connect-dev-order-notify-stream';
const DELETION_FNS = ['wereda-connect-dev-account-deletion', 'wereda-connect-dev-account-deletion-sweep'];

const POLICY_JSON =
  '{"declinePoints":1,"expiryPoints":1,"cancelPoints":3,"lateCancelPoints":4,"tripCancelPoints":3,' +
  '"tripLateCancelPoints":4,"noShowPoints":5,"overduePoints":5,"lateCancelHours":24,"pointsLifeDays":90,' +
  '"pairCapDays":30,"dailyCapPoints":6,"expiryMinWindowHours":12,"requestPointsMax":9,"warningAt":3,' +
  '"lowAt":6,"restrictAt":10,"restrictionDays":14,"repeatWindowDays":365,"ratingDeductionPerPoint":0.1,' +
  '"ratingDeductionMax":1.0,"reliabilityMinOrders":3,"earnBackMinOrderEtb":500,"earnBackPer30Days":2,' +
  '"earnBackBuyerRepeatDays":90,"appealWindowDays":7,"tagMaxDays":90,"adminRestrictMaxDays":365,' +
  '"buyerDeclineSignalCount":3,"buyerDeclineSignalDays":30,"eventsReadCap":2000}';

describe('ESC-28 reputation-events table (DatabaseStack)', () => {
  let db: Template;

  beforeAll(() => {
    ({ db } = stacks(devConfig));
  });

  test('keys, on-demand billing, PITR, the purgeAt TTL and a NEW_IMAGE stream', () => {
    const [, table] = only(db, 'AWS::DynamoDB::Table', { TableName: 'wereda-connect-dev-reputation-events' });
    expect(table.Properties).toMatchObject({
      KeySchema: [
        { AttributeName: 'userId', KeyType: 'HASH' },
        { AttributeName: 'eventId', KeyType: 'RANGE' },
      ],
      BillingMode: 'PAY_PER_REQUEST',
      PointInTimeRecoverySpecification: { PointInTimeRecoveryEnabled: true },
      // The TTL attribute can never be renamed on a live table.
      TimeToLiveSpecification: { AttributeName: 'purgeAt', Enabled: true },
      StreamSpecification: { StreamViewType: 'NEW_IMAGE' },
    });
    // Dev tables are destroyed with the stack; prod's are retained (below).
    expect(table.DeletionPolicy).toBe('Delete');
  });

  test('two sparse GSIs declared with the table: AppealStatusIndex (ALL) and DueIndex (KEYS_ONLY)', () => {
    const [, table] = only(db, 'AWS::DynamoDB::Table', { TableName: 'wereda-connect-dev-reputation-events' });
    expect(table.Properties.GlobalSecondaryIndexes).toEqual([
      {
        IndexName: 'AppealStatusIndex',
        KeySchema: [
          { AttributeName: 'appealStatus', KeyType: 'HASH' },
          { AttributeName: 'appealedAt', KeyType: 'RANGE' },
        ],
        Projection: { ProjectionType: 'ALL' },
      },
      {
        IndexName: 'DueIndex',
        KeySchema: [
          { AttributeName: 'dueDay', KeyType: 'HASH' },
          { AttributeName: 'dueAt', KeyType: 'RANGE' },
        ],
        Projection: { ProjectionType: 'KEYS_ONLY' },
      },
    ]);
    const attributes = (table.Properties.AttributeDefinitions as Array<{ AttributeName: string; AttributeType: string }>)
      .map((a) => `${a.AttributeName}:${a.AttributeType}`)
      .sort();
    expect(attributes).toEqual(['appealStatus:S', 'appealedAt:S', 'dueAt:S', 'dueDay:S', 'eventId:S', 'userId:S']);
  });

  test('the table name is a stack output', () => {
    db.hasOutput('ReputationEventsTableName', {});
  });

  test('prod retains the table', () => {
    const prod = stacks(environments['prod']).db;
    const [, table] = only(prod, 'AWS::DynamoDB::Table', { TableName: 'wereda-connect-prod-reputation-events' });
    expect(table.DeletionPolicy).toBe('Retain');
  });
});

describe('ESC-28 reputation core (ApiStack)', () => {
  let api: Template;

  beforeAll(() => {
    ({ api } = stacks(devConfig));
  });

  test('the three queues: 14 days, SQS-managed encryption, TLS only; the parked queue redrives after 5 receives', () => {
    const ids: Record<string, string> = {};
    for (const name of ['reputation-dlq', 'reputation-parked-dlq', 'reputation-parked']) {
      const [id] = only(api, 'AWS::SQS::Queue', {
        QueueName: `wereda-connect-dev-${name}`,
        MessageRetentionPeriod: 14 * 24 * 3600,
        SqsManagedSseEnabled: true,
      });
      ids[name] = id;
      api.hasResourceProperties('AWS::SQS::QueuePolicy', {
        Queues: [{ Ref: id }],
        PolicyDocument: Match.objectLike({
          Statement: Match.arrayWith([
            Match.objectLike({ Effect: 'Deny', Condition: { Bool: { 'aws:SecureTransport': 'false' } } }),
          ]),
        }),
      });
    }
    // Longer than the 5 minute order-cron run that receives from it.
    api.hasResourceProperties('AWS::SQS::Queue', {
      QueueName: 'wereda-connect-dev-reputation-parked',
      VisibilityTimeout: 360,
      RedrivePolicy: {
        deadLetterTargetArn: { 'Fn::GetAtt': [ids['reputation-parked-dlq'], 'Arn'] },
        maxReceiveCount: 5,
      },
    });
    // The two DLQs redrive nowhere.
    for (const name of ['reputation-dlq', 'reputation-parked-dlq']) {
      const [, queue] = only(api, 'AWS::SQS::Queue', { QueueName: `wereda-connect-dev-${name}` });
      expect([name, queue.Properties.RedrivePolicy]).toEqual([name, undefined]);
    }
  });

  test('the policy parameter holds the 31 defaults of section 5.9, as written', () => {
    const [, parameter] = only(api, 'AWS::SSM::Parameter', { Name: '/wereda-connect-dev/reputation/policy' });
    expect(parameter.Properties.Type).toBe('String');
    expect(parameter.Properties.Value).toBe(POLICY_JSON);
    const policy = JSON.parse(parameter.Properties.Value) as Record<string, unknown>;
    expect(Object.keys(policy)).toEqual([
      'declinePoints', 'expiryPoints', 'cancelPoints', 'lateCancelPoints', 'tripCancelPoints',
      'tripLateCancelPoints', 'noShowPoints', 'overduePoints', 'lateCancelHours', 'pointsLifeDays',
      'pairCapDays', 'dailyCapPoints', 'expiryMinWindowHours', 'requestPointsMax', 'warningAt', 'lowAt',
      'restrictAt', 'restrictionDays', 'repeatWindowDays', 'ratingDeductionPerPoint', 'ratingDeductionMax',
      'reliabilityMinOrders', 'earnBackMinOrderEtb', 'earnBackPer30Days', 'earnBackBuyerRepeatDays',
      'appealWindowDays', 'tagMaxDays', 'adminRestrictMaxDays', 'buyerDeclineSignalCount',
      'buyerDeclineSignalDays', 'eventsReadCap',
    ]);
    expect(Object.values(policy).every((v) => typeof v === 'number')).toBe(true);
  });

  test('every policy reader names the parameter and may read it', () => {
    const [paramId] = only(api, 'AWS::SSM::Parameter', { Name: '/wereda-connect-dev/reputation/policy' });
    for (const fn of [STREAM_FN, ORDER_CRON_FN, SYNC_FN]) {
      const name = envOf(api, fn).REPUTATION_POLICY_PARAM;
      expect([fn, JSON.stringify(name)]).toEqual([fn, expect.stringMatching(new RegExp(`${paramId}|/wereda-connect-dev/reputation/policy`))]);
      expect([fn, actionsOn(api, fn, paramId)]).toEqual([fn, expect.arrayContaining(['ssm:GetParameter'])]);
    }
  });

  test('reputation-stream: name, handler, memory, timeout and its environment (flags off in dev)', () => {
    const [parkedId] = only(api, 'AWS::SQS::Queue', { QueueName: 'wereda-connect-dev-reputation-parked' });
    api.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: STREAM_FN,
      MemorySize: 256,
      Timeout: 30,
      Architectures: ['arm64'],
      Environment: {
        Variables: Match.objectLike({
          FUNCTION_HANDLER: 'ReputationStreamFunction',
          REPUTATION_EVENTS_TABLE: Match.anyValue(),
          USERS_TABLE: Match.anyValue(),
          DEVICE_TOKENS_TABLE: Match.anyValue(),
          FRAUD_SIGNALS_TABLE: Match.anyValue(),
          REPUTATION_PARKED_QUEUE_URL: { Ref: parkedId },
          REPUTATION_POLICY_PARAM: Match.anyValue(),
          REPUTATION_PENALTIES_ENABLED: 'false',
          REPUTATION_TIMEOUT_PENALTIES_ENABLED: 'false',
        }),
      },
    });
  });

  test('reputation-stream reads the orders stream with the section 7.1 settings and exactly five filters', () => {
    const fnId = lambdaId(api, STREAM_FN);
    const [dlqId] = only(api, 'AWS::SQS::Queue', { QueueName: 'wereda-connect-dev-reputation-dlq' });
    const mappings = Object.values(
      api.findResources('AWS::Lambda::EventSourceMapping', { Properties: { FunctionName: { Ref: fnId } } }),
    ).map((m) => m.Properties);
    expect(mappings).toHaveLength(1);
    const m = mappings[0];
    // The orders table's stream ARN, not the table ARN.
    expect(JSON.stringify(m.EventSourceArn)).toContain('FnGetAttOrdersTable');
    expect(JSON.stringify(m.EventSourceArn)).toContain('StreamArn');
    expect(m).toMatchObject({
      StartingPosition: 'LATEST',
      BatchSize: 25,
      ParallelizationFactor: 1,
      MaximumRetryAttempts: 10,
      BisectBatchOnFunctionError: true,
      FunctionResponseTypes: ['ReportBatchItemFailures'],
      MaximumRecordAgeInSeconds: 86400,
      DestinationConfig: { OnFailure: { Destination: { 'Fn::GetAtt': [dlqId, 'Arn'] } } },
    });
    const modifiedWith = (field: string, rule: unknown) =>
      JSON.stringify({ eventName: ['MODIFY'], dynamodb: { NewImage: { [field]: { S: rule } } } });
    expect(m.FilterCriteria).toEqual({
      Filters: [
        modifiedWith('farmerResponse', ['declined', 'expired']),
        modifiedWith('truckerResponse', ['declined', 'expired', 'cancelled']),
        modifiedWith('transportFailure', [{ exists: true }]),
        modifiedWith('cancelReason', ['farmer_cancelled', 'farmer_no_show']),
        modifiedWith('status', ['settled', 'disputed']),
      ].map((Pattern) => ({ Pattern })),
    });
  });

  test('the orders stream has exactly two consumers (a third needs a fan-out, section 7.1)', () => {
    const onOrders = Object.values(api.findResources('AWS::Lambda::EventSourceMapping')).filter((m) =>
      JSON.stringify(m.Properties.EventSourceArn).includes('FnGetAttOrdersTable'),
    );
    const consumers = onOrders.map((m) => (m.Properties.FunctionName as { Ref: string }).Ref).sort();
    expect(consumers).toEqual([lambdaId(api, NOTIFY_FN), lambdaId(api, STREAM_FN)].sort());
  });

  test('reputation-stream holds only the base-table calls it makes, the parked send and the parameter read', () => {
    const base = (hint: string) => {
      const statements = statementsOn(api, STREAM_FN, hint);
      expect([hint, JSON.stringify(statements.map((s) => s.Resource))]).toEqual([hint, expect.not.stringContaining('/index/')]);
      return [...new Set(statements.flatMap(actionsOf))].sort();
    };
    expect(base('UsersTable')).toEqual(['dynamodb:GetItem', 'dynamodb:UpdateItem']);
    expect(base('DeviceTokensTable')).toEqual(['dynamodb:Query']);
    // ESC-33: the fraud hooks put signal rows (NO_SHOW, DELIVERY_OVERDUE, BUYER_DECLINE_PATTERN).
    expect(base('FraudSignalsTable')).toEqual(['dynamodb:PutItem', 'dynamodb:Query']);
    expect(base('ReputationEventsTable')).toEqual(['dynamodb:PutItem', 'dynamodb:Query', 'dynamodb:UpdateItem']);
    expect(actionsOn(api, STREAM_FN, 'ReputationParkedQueue')).toEqual(
      expect.arrayContaining(['sqs:SendMessage']),
    );
    expect(actionsOn(api, STREAM_FN, 'ReputationParkedQueue')).not.toContain('sqs:ReceiveMessage');
    // The failure destination: the stream mapping sends batch metadata to the reputation DLQ.
    expect(actionsOn(api, STREAM_FN, 'ReputationDlq')).toContain('sqs:SendMessage');
    // On the orders table: the stream reads, and (ESC-33) a Query of the buyer index. Never the base table.
    for (const s of statementsOn(api, STREAM_FN, 'OrdersTable')) {
      const resource = JSON.stringify(s.Resource);
      if (resource.includes('/index/')) {
        expect(resource).toContain('/index/BuyerIdCreatedAtIndex');
        expect(resource).not.toContain('/index/*');
        expect(actionsOf(s)).toEqual(['dynamodb:Query']);
      } else {
        expect(resource).toContain('StreamArn');
      }
    }
  });

  test('ESC-33 fraud hooks: reputation-stream knows the orders table, escrow-cron reads the penalties flag', () => {
    expect(envOf(api, STREAM_FN).ORDERS_TABLE).toBeDefined();
    const onBuyerIndex = statementsOn(api, STREAM_FN, 'OrdersTable').filter((s) =>
      JSON.stringify(s.Resource).includes('/index/BuyerIdCreatedAtIndex'),
    );
    expect(onBuyerIndex).toHaveLength(1);
    // The unpark replay (order-cron) writes the same signals and reads the same index.
    expect(actionsOn(api, ORDER_CRON_FN, 'FraudSignalsTable')).toEqual(expect.arrayContaining(['dynamodb:PutItem', 'dynamodb:Query']));
    expect(actionsOn(api, ORDER_CRON_FN, 'OrdersTable')).toEqual(expect.arrayContaining(['dynamodb:Query']));
    for (const envName of ['dev', 'staging', 'prod']) {
      const stack = stacks(environments[envName]).api;
      expect([envName, envOf(stack, `wereda-connect-${envName}-escrow-cron`).REPUTATION_PENALTIES_ENABLED]).toEqual([envName, 'false']);
    }
  });

  test('ESC-29 copy refresh: reputation-stream reads the owner indexes and updates crop and trip rows, nothing more', () => {
    expect(envOf(api, STREAM_FN).CROPS_TABLE).toBeDefined();
    expect(envOf(api, STREAM_FN).TRIPS_TABLE).toBeDefined();
    for (const [hint, index, baseActions] of [
      ['CropsTable', '/index/FarmerIdCreatedAtIndex', ['dynamodb:BatchGetItem', 'dynamodb:UpdateItem']],
      ['TripsTable', '/index/TruckerIdCreatedAtIndex', ['dynamodb:UpdateItem']],
    ] as [string, string, string[]][]) {
      const statements = statementsOn(api, STREAM_FN, hint);
      const onIndex = statements.filter((s) => JSON.stringify(s.Resource).includes('/index/'));
      expect([hint, [...new Set(onIndex.flatMap(actionsOf))]]).toEqual([hint, ['dynamodb:Query']]);
      expect([hint, JSON.stringify(onIndex.map((s) => s.Resource))]).toEqual([hint, expect.stringContaining(index)]);
      expect(JSON.stringify(onIndex.map((s) => s.Resource))).not.toContain('/index/*');
      const onBase = statements.filter((s) => !JSON.stringify(s.Resource).includes('/index/'));
      expect([hint, [...new Set(onBase.flatMap(actionsOf))].sort()]).toEqual([hint, baseActions]);
    }
    // The other two writers of the copies already hold read and write on both tables.
    for (const fn of [ORDER_CRON_FN, SYNC_FN]) {
      for (const hint of ['CropsTable', 'TripsTable']) {
        expect([fn, hint, actionsOn(api, fn, hint)]).toEqual([
          fn,
          hint,
          expect.arrayContaining(['dynamodb:Query', 'dynamodb:BatchGetItem', 'dynamodb:UpdateItem']),
        ]);
      }
    }
  });

  test('ESC-29 reputation Lambda: five routes behind the app authorizer, least privilege, flags off', () => {
    const env = envOf(api, REPUTATION_FN);
    expect(env.REPUTATION_PENALTIES_ENABLED).toBe('false');
    expect(env.REPUTATION_PUBLIC_TAGS_ENABLED).toBe('false');
    for (const name of ['REPUTATION_EVENTS_TABLE', 'USERS_TABLE', 'TRANSACTION_AUDIT_TABLE', 'UPLOADS_BUCKET', 'PAGINATION_SECRET_ARN', 'REPUTATION_POLICY_PARAM']) {
      expect([name, env[name]]).toEqual([name, expect.anything()]);
    }
    api.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: REPUTATION_FN,
      MemorySize: 256,
      Timeout: 10,
    });

    expect(actionsOn(api, REPUTATION_FN, 'UsersTable')).toEqual(['dynamodb:GetItem']);
    const events = statementsOn(api, REPUTATION_FN, 'ReputationEventsTable');
    expect([...new Set(events.flatMap(actionsOf))].sort()).toEqual(['dynamodb:GetItem', 'dynamodb:Query', 'dynamodb:UpdateItem']);
    expect(actionsOn(api, REPUTATION_FN, 'ReputationEventsTable')).not.toContain('dynamodb:Scan');
    // Nothing on the listings, the orders or the ratings.
    for (const hint of ['CropsTable', 'TripsTable', 'OrdersTable', 'RatingsTable']) {
      expect([hint, statementsOn(api, REPUTATION_FN, hint)]).toEqual([hint, []]);
    }
    // S3: only objects under the appeal prefix.
    const s3 = statementsOn(api, REPUTATION_FN, 'uploads/appeals/*');
    expect(s3.length).toBeGreaterThan(0);
    const objectStatements = statementsOn(api, REPUTATION_FN, 'Uploads').filter((s) =>
      actionsOf(s).some((a) => a === 's3:PutObject' || a.startsWith('s3:GetObject')),
    );
    for (const s of objectStatements) {
      expect(JSON.stringify(s.Resource)).toContain('uploads/appeals/*');
    }
    expect(actionsOn(api, REPUTATION_FN, 'Uploads')).not.toContain('s3:DeleteObject*');

    const routes = Object.values(api.findResources('AWS::ApiGatewayV2::Route'))
      .map((r) => r.Properties)
      .filter((r) => String(r.RouteKey).includes('/reputation'));
    expect(routes.map((r) => r.RouteKey).sort()).toEqual([
      'GET /reputation/me',
      'GET /reputation/me/events',
      'GET /reputation/{userId}',
      'POST /reputation/appeals/photo-url',
      'POST /reputation/events/{eventId}/appeal',
    ]);
    // The same (app pool) authorizer as /ratings: an admin pool token gets 401.
    const [ratings] = Object.values(api.findResources('AWS::ApiGatewayV2::Route'))
      .map((r) => r.Properties)
      .filter((r) => r.RouteKey === 'GET /ratings');
    for (const route of routes) {
      expect([route.RouteKey, route.AuthorizationType, route.AuthorizerId]).toEqual([
        route.RouteKey,
        'JWT',
        ratings.AuthorizerId,
      ]);
    }
  });

  test('ESC-29 reads: ratings reads the ratee row and events; crops, trips and match carry both flags, off', () => {
    const ratingsFn = 'wereda-connect-dev-ratings';
    expect(actionsOn(api, ratingsFn, 'UsersTable')).toEqual(['dynamodb:GetItem']);
    expect(actionsOn(api, ratingsFn, 'ReputationEventsTable')).toEqual(['dynamodb:Query']);
    for (const fn of [ratingsFn, 'wereda-connect-dev-crops', 'wereda-connect-dev-trips', 'wereda-connect-dev-match']) {
      const env = envOf(api, fn);
      expect([fn, env.REPUTATION_PENALTIES_ENABLED, env.REPUTATION_PUBLIC_TAGS_ENABLED]).toEqual([fn, 'false', 'false']);
    }
    expect(envOf(api, SYNC_FN).REPUTATION_PUBLIC_TAGS_ENABLED).toBe('false');
  });

  test('ESC-29 suspension and deletion: admin may only upsert the heal marker; account deletion removes appeal photos only', () => {
    const adminFn = 'wereda-connect-dev-admin';
    expect(envOf(api, adminFn).REPUTATION_PENALTIES_ENABLED).toBe('false');
    expect(envOf(api, adminFn).REPUTATION_EVENTS_TABLE).toBeDefined();
    expect(actionsOn(api, adminFn, 'ReputationEventsTable')).toEqual(['dynamodb:UpdateItem']);
    for (const fn of DELETION_FNS) {
      expect([fn, envOf(api, fn).UPLOADS_BUCKET]).toEqual([fn, expect.anything()]);
      // The appeal photo step runs only with the flag on; off, a deletion makes no S3 call.
      expect([fn, envOf(api, fn).REPUTATION_PENALTIES_ENABLED]).toEqual([fn, 'false']);
      const deletes = statementsOn(api, fn, 'uploads/appeals/*');
      expect([fn, [...new Set(deletes.flatMap(actionsOf))].sort()]).toEqual([fn, ['s3:DeleteObject', 's3:DeleteObjectVersion']]);
      const lists = statementsOn(api, fn, 'Uploads').filter((s) => actionsOf(s).includes('s3:ListBucketVersions'));
      expect([fn, lists.length]).toEqual([fn, 1]);
      expect(JSON.stringify(lists[0])).toContain('uploads/appeals/*');
    }
  });

  test('order-notify-stream reads the reputation-events stream: INSERT of a penalty code or TIER_CHANGED, notify settings', () => {
    const fnId = lambdaId(api, NOTIFY_FN);
    const [notifyDlqId] = only(api, 'AWS::SQS::Queue', { QueueName: 'wereda-connect-dev-notify-dlq' });
    const onReputation = Object.values(
      api.findResources('AWS::Lambda::EventSourceMapping', { Properties: { FunctionName: { Ref: fnId } } }),
    )
      .map((m) => m.Properties)
      .filter((m) => JSON.stringify(m.EventSourceArn).includes('FnGetAttReputationEventsTable'));
    expect(onReputation).toHaveLength(1);
    const m = onReputation[0];
    expect(JSON.stringify(m.EventSourceArn)).toContain('StreamArn');
    expect(m).toMatchObject({
      StartingPosition: 'LATEST',
      BatchSize: 10,
      MaximumRetryAttempts: 3,
      BisectBatchOnFunctionError: true,
      FunctionResponseTypes: ['ReportBatchItemFailures'],
      MaximumRecordAgeInSeconds: 86400,
      DestinationConfig: { OnFailure: { Destination: { 'Fn::GetAtt': [notifyDlqId, 'Arn'] } } },
    });
    expect(m.FilterCriteria).toEqual({
      Filters: [
        {
          Pattern: JSON.stringify({
            eventName: ['INSERT'],
            dynamodb: {
              NewImage: {
                code: {
                  S: [
                    'DECLINED_REQUEST',
                    'RESPONSE_EXPIRED',
                    'CANCELLED_AFTER_ACCEPT',
                    'LATE_CANCEL',
                    'TRIP_CANCELLED_WITH_BOOKINGS',
                    'NO_SHOW',
                    'DELIVERY_OVERDUE',
                    'TIER_CHANGED',
                  ],
                },
              },
            },
          }),
        },
      ],
    });
    // It reads only the record: stream actions on the reputation-events stream, no table grant.
    const onTable = statementsOn(api, NOTIFY_FN, 'ReputationEventsTable');
    expect(onTable.length).toBeGreaterThan(0);
    for (const s of onTable) {
      expect(JSON.stringify(s.Resource)).toContain('StreamArn');
      expect(actionsOf(s).every((a) => ['dynamodb:DescribeStream', 'dynamodb:GetRecords', 'dynamodb:GetShardIterator'].includes(a))).toBe(true);
    }
  });

  test('order-cron: the reputation environment, flags off in dev', () => {
    const [parkedId] = only(api, 'AWS::SQS::Queue', { QueueName: 'wereda-connect-dev-reputation-parked' });
    api.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: ORDER_CRON_FN,
      Description: Match.stringLikeRegexp('reputation'),
      Environment: {
        Variables: Match.objectLike({
          REPUTATION_EVENTS_TABLE: Match.anyValue(),
          REPUTATION_PARKED_QUEUE_URL: { Ref: parkedId },
          REPUTATION_POLICY_PARAM: Match.anyValue(),
          REPUTATION_PENALTIES_ENABLED: 'false',
          REPUTATION_TIMEOUT_PENALTIES_ENABLED: 'false',
          RATINGS_TABLE: Match.anyValue(),
          DEVICE_TOKENS_TABLE: Match.anyValue(),
        }),
      },
    });
  });

  test('order-cron: two new rules, the daily job at 00:00 UTC and the hourly unpark, each with its body', () => {
    const target = (action: string) => [
      Match.objectLike({
        Arn: { 'Fn::GetAtt': [Match.stringLikeRegexp('^OrderCronFunction'), 'Arn'] },
        Input: JSON.stringify({ body: JSON.stringify({ action }) }),
      }),
    ];
    api.hasResourceProperties('AWS::Events::Rule', {
      Name: 'wereda-connect-dev-order-cron-reputation-daily',
      ScheduleExpression: 'cron(0 0 * * ? *)',
      State: 'ENABLED',
      Targets: target('reputation_daily'),
    });
    api.hasResourceProperties('AWS::Events::Rule', {
      Name: 'wereda-connect-dev-order-cron-reputation-unpark',
      ScheduleExpression: 'rate(1 hour)',
      State: 'ENABLED',
      Targets: target('reputation_unpark'),
    });
    // The backfill and the full recompute are invoked by hand only.
    const rules = JSON.stringify(api.findResources('AWS::Events::Rule'));
    expect(rules).not.toContain('reputation_backfill_ratings');
    expect(rules).not.toContain('reputation_recompute_all');
  });

  test('order-cron: reputation-events Query on the table and its two indexes, row writes on the table only', () => {
    const statements = statementsOn(api, ORDER_CRON_FN, 'ReputationEventsTable');
    const queries = statements.filter((s) => actionsOf(s).includes('dynamodb:Query'));
    const queried = JSON.stringify(queries.map((s) => s.Resource));
    expect(queried).toContain('/index/DueIndex');
    expect(queried).toContain('/index/AppealStatusIndex');
    expect(queried).not.toContain('/index/*');
    const writes = statements.filter((s) => !actionsOf(s).includes('dynamodb:Query'));
    expect([...new Set(writes.flatMap(actionsOf))].sort()).toEqual([
      'dynamodb:DeleteItem',
      'dynamodb:GetItem',
      'dynamodb:PutItem',
      'dynamodb:UpdateItem',
    ]);
    expect(JSON.stringify(writes.map((s) => s.Resource))).not.toContain('/index/');
  });

  test('order-cron: users UpdateItem, the ratings index Query and UpdateItem, device tokens Query, the parked queue consume', () => {
    expect(actionsOn(api, ORDER_CRON_FN, 'UsersTable')).toEqual(
      expect.arrayContaining(['dynamodb:GetItem', 'dynamodb:Query', 'dynamodb:UpdateItem']),
    );
    expect(actionsOn(api, ORDER_CRON_FN, 'UsersTable')).not.toContain('dynamodb:PutItem');
    const ratings = statementsOn(api, ORDER_CRON_FN, 'RatingsTable');
    const ratingQueries = ratings.filter((s) => actionsOf(s).includes('dynamodb:Query'));
    expect(ratingQueries).toHaveLength(1);
    expect(JSON.stringify(ratingQueries[0].Resource)).toContain('/index/RateeIdCreatedAtIndex');
    expect([...new Set(ratings.flatMap(actionsOf))].sort()).toEqual(['dynamodb:Query', 'dynamodb:UpdateItem']);
    expect(actionsOn(api, ORDER_CRON_FN, 'DeviceTokensTable')).toEqual(['dynamodb:Query']);
    expect(actionsOn(api, ORDER_CRON_FN, 'ReputationParkedQueue')).toEqual(
      expect.arrayContaining(['sqs:ReceiveMessage', 'sqs:DeleteMessage', 'sqs:ChangeMessageVisibility']),
    );
  });

  test('sync: users UpdateItem and reputation-events Query, PutItem and UpdateItem, base tables only (rating-api 1.2.0)', () => {
    api.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: SYNC_FN,
      Environment: {
        Variables: Match.objectLike({
          REPUTATION_EVENTS_TABLE: Match.anyValue(),
          REPUTATION_POLICY_PARAM: Match.anyValue(),
          REPUTATION_PENALTIES_ENABLED: 'false',
        }),
      },
    });
    const userUpdates = statementsOn(api, SYNC_FN, 'UsersTable').filter((s) => actionsOf(s).includes('dynamodb:UpdateItem'));
    expect(userUpdates).toHaveLength(1);
    expect(JSON.stringify(userUpdates[0].Resource)).not.toContain('/index/');
    // The read grant stays.
    expect(actionsOn(api, SYNC_FN, 'UsersTable')).toEqual(expect.arrayContaining(['dynamodb:GetItem', 'dynamodb:Query']));
    const events = statementsOn(api, SYNC_FN, 'ReputationEventsTable');
    expect([...new Set(events.flatMap(actionsOf))].sort()).toEqual(['dynamodb:PutItem', 'dynamodb:Query', 'dynamodb:UpdateItem']);
    expect(JSON.stringify(events.map((s) => s.Resource))).not.toContain('/index/');
  });

  test('account deletion: both Lambdas name the table and may Query and DeleteItem its base table only', () => {
    for (const fn of DELETION_FNS) {
      expect([fn, envOf(api, fn).REPUTATION_EVENTS_TABLE]).toEqual([fn, expect.anything()]);
      const statements = statementsOn(api, fn, 'ReputationEventsTable');
      expect([fn, [...new Set(statements.flatMap(actionsOf))].sort()]).toEqual([fn, ['dynamodb:DeleteItem', 'dynamodb:Query']]);
      expect([fn, JSON.stringify(statements.map((s) => s.Resource))]).toEqual([fn, expect.not.stringContaining('/index/')]);
    }
  });

  test('no other Lambda may write the reputation-events table', () => {
    const writers = new Set<string>();
    const fns = api.findResources('AWS::Lambda::Function');
    for (const [, fn] of Object.entries(fns)) {
      const name = fn.Properties.FunctionName;
      // CDK's own helpers (bucket notifications, auto-delete) carry no name and no table grant.
      if (typeof name !== 'string') continue;
      const writes = statementsOn(api, name, 'ReputationEventsTable')
        .filter((s) => !JSON.stringify(s.Resource).includes('StreamArn'))
        .flatMap(actionsOf)
        .filter((a) => ['dynamodb:PutItem', 'dynamodb:UpdateItem', 'dynamodb:DeleteItem', 'dynamodb:BatchWriteItem'].includes(a));
      if (writes.length > 0) writers.add(name);
    }
    // ESC-29: the admin Lambda upserts the heal marker on a suspend; the reputation Lambda stores an appeal.
    expect([...writers].sort()).toEqual(
      [...DELETION_FNS, 'wereda-connect-dev-admin', ORDER_CRON_FN, REPUTATION_FN, STREAM_FN, SYNC_FN].sort(),
    );
  });
});

describe('ESC-28 reputation core outside dev', () => {
  test('reserved concurrency: reputation-stream 10, order-cron 6 (six rules)', () => {
    const { api } = stacks(environments['staging'], 'staging');
    api.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: 'wereda-connect-staging-reputation-stream',
      ReservedConcurrentExecutions: 10,
    });
    api.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: 'wereda-connect-staging-order-cron',
      ReservedConcurrentExecutions: 6,
    });
    // ESC-29: the reputation reads and the appeal.
    api.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: 'wereda-connect-staging-reputation',
      ReservedConcurrentExecutions: 5,
    });
  });

  test.each(['dev', 'staging', 'prod'])('%s: both reputation flags synthesize "false" on every Lambda that reads them', (envName) => {
    const { api } = stacks(environments[envName]);
    const flags = (fn: string) => {
      const env = envOf(api, `wereda-connect-${envName}-${fn}`);
      return [env.REPUTATION_PENALTIES_ENABLED, env.REPUTATION_TIMEOUT_PENALTIES_ENABLED];
    };
    expect(flags('reputation-stream')).toEqual(['false', 'false']);
    expect(flags('order-cron')).toEqual(['false', 'false']);
    // Sync reads the penalties flag only.
    expect(flags('sync')).toEqual(['false', undefined]);
    // ESC-29: the public tags flag is off on every Lambda that returns a reputation.
    for (const fn of ['crops', 'trips', 'match', 'ratings', 'reputation', 'sync']) {
      const env = envOf(api, `wereda-connect-${envName}-${fn}`);
      expect([fn, env.REPUTATION_PENALTIES_ENABLED, env.REPUTATION_PUBLIC_TAGS_ENABLED]).toEqual([fn, 'false', 'false']);
    }
    expect(environments[envName].reputationPublicTags).toBe(false);
  });

  test('turning the flags on in the config reaches the Lambdas', () => {
    const { api } = stacks({ ...devConfig, reputationPenalties: true, timeoutPenalties: true, reputationPublicTags: true });
    // ESC-29: the gates, the hidden browse and the public tags follow the same config.
    for (const fn of ['crops', 'trips', 'match', 'ratings', 'reputation']) {
      const env = envOf(api, `wereda-connect-dev-${fn}`);
      expect([fn, env.REPUTATION_PENALTIES_ENABLED, env.REPUTATION_PUBLIC_TAGS_ENABLED]).toEqual([fn, 'true', 'true']);
    }
    expect(envOf(api, SYNC_FN).REPUTATION_PUBLIC_TAGS_ENABLED).toBe('true');
    expect(envOf(api, 'wereda-connect-dev-admin').REPUTATION_PENALTIES_ENABLED).toBe('true');
    for (const fn of DELETION_FNS) {
      expect([fn, envOf(api, fn).REPUTATION_PENALTIES_ENABLED]).toEqual([fn, 'true']);
    }
    for (const fn of [STREAM_FN, ORDER_CRON_FN]) {
      const env = envOf(api, fn);
      expect([fn, env.REPUTATION_PENALTIES_ENABLED, env.REPUTATION_TIMEOUT_PENALTIES_ENABLED]).toEqual([fn, 'true', 'true']);
    }
    expect(envOf(api, SYNC_FN).REPUTATION_PENALTIES_ENABLED).toBe('true');
  });
});

describe('ESC-28 reputation alarms (MonitoringStack)', () => {
  let template: Template;
  const lg =(fn: string) => `/aws/lambda/wereda-connect-dev-${fn}`;

  beforeAll(() => {
    template = monitoringTemplate();
  });

  /** The alarm's single metric, whichever of the two CloudFormation shapes CDK picked. */
  function metricOf(alarmName: string): { namespace: string; name: string; stat: string; period: number; dims: unknown } {
    const [, alarm] = only(template, 'AWS::CloudWatch::Alarm', { AlarmName: alarmName });
    const p = alarm.Properties;
    // ALARM and OK both notify the alarm topic.
    for (const actions of [p.AlarmActions, p.OKActions]) {
      expect([alarmName, JSON.stringify(actions)]).toEqual([alarmName, expect.stringMatching(/^\[\{"Ref":"AlarmTopic[0-9A-F]+"\}\]$/)]);
    }
    if (p.Metrics) {
      const stat = p.Metrics[0].MetricStat;
      return { namespace: stat.Metric.Namespace, name: stat.Metric.MetricName, stat: stat.Stat, period: stat.Period, dims: stat.Metric.Dimensions };
    }
    return { namespace: p.Namespace, name: p.MetricName, stat: p.Statistic, period: p.Period, dims: p.Dimensions };
  }

  test('the two queue depth alarms: any visible message', () => {
    for (const [alarmName, queue] of [
      ['wereda-connect-dev-reputation-dlq-messages', 'wereda-connect-dev-reputation-dlq'],
      ['wereda-connect-dev-reputation-parked-dlq-messages', 'wereda-connect-dev-reputation-parked-dlq'],
    ]) {
      expect(metricOf(alarmName)).toMatchObject({
        namespace: 'AWS/SQS',
        name: 'ApproximateNumberOfMessagesVisible',
        dims: [{ Name: 'QueueName', Value: queue }],
      });
      template.hasResourceProperties('AWS::CloudWatch::Alarm', {
        AlarmName: alarmName,
        Threshold: 1,
        ComparisonOperator: 'GreaterThanOrEqualToThreshold',
        TreatMissingData: 'notBreaching',
      });
    }
  });

  test('the stream iterator age alarm fires above 1 hour, the errors alarm on any error', () => {
    expect(metricOf('wereda-connect-dev-reputation-stream-iterator-age')).toMatchObject({
      namespace: 'AWS/Lambda',
      name: 'IteratorAge',
      stat: 'Maximum',
      period: 300,
      dims: [{ Name: 'FunctionName', Value: 'wereda-connect-dev-reputation-stream' }],
    });
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'wereda-connect-dev-reputation-stream-iterator-age',
      Threshold: 3600000,
      ComparisonOperator: 'GreaterThanThreshold',
    });
    expect(metricOf('wereda-connect-dev-reputation-stream-errors')).toMatchObject({
      namespace: 'AWS/Lambda',
      name: 'Errors',
      stat: 'Sum',
      period: 300,
      dims: [{ Name: 'FunctionName', Value: 'wereda-connect-dev-reputation-stream' }],
    });
    // The same threshold as the notify stream's errors alarm.
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'wereda-connect-dev-reputation-stream-errors',
      Threshold: 0,
      ComparisonOperator: 'GreaterThanThreshold',
    });
  });

  test.each([
    ['REPUTATION_POLICY_INVALID', 'ReputationPolicyInvalid', 'reputation-policy-invalid', ['reputation-stream', 'order-cron', 'sync'], 1, 300],
    ['REPUTATION_EVENTS_TRUNCATED', 'ReputationEventsTruncated', 'reputation-events-truncated', ['reputation-stream', 'order-cron', 'sync'], 1, 300],
    ['REPUTATION_REVIEW_OVERDUE', 'ReputationReviewOverdue', 'reputation-review-overdue', ['order-cron'], 1, 300],
    ['REPUTATION_APPEAL_OVERDUE', 'ReputationAppealOverdue', 'reputation-appeal-overdue', ['order-cron'], 1, 300],
    ['REPUTATION_COPY_FAILED', 'ReputationCopyFailed', 'reputation-copy-failed', ['reputation-stream', 'order-cron', 'sync'], 5, 3600],
    ['REPUTATION_PARK_FAILED', 'ReputationParkFailed', 'reputation-park-failed', ['reputation-stream', 'order-cron'], 1, 300],
  ] as const)('%s: a filter on each log group that can write it, and one alarm', (token, metricName, alarmSuffix, sources, threshold, period) => {
    const filters = Object.values(template.findResources('AWS::Logs::MetricFilter')).filter(
      (f) => f.Properties.MetricTransformations[0].MetricName === metricName,
    );
    expect(filters.map((f) => f.Properties.LogGroupName).sort()).toEqual(sources.map(lg).sort());
    for (const f of filters) {
      expect(f.Properties.FilterPattern).toBe(`"${token}"`);
      expect(f.Properties.MetricTransformations[0]).toMatchObject({ MetricNamespace: 'WeredaConnect/dev', MetricValue: '1' });
    }
    const alarmName = `wereda-connect-dev-${alarmSuffix}`;
    expect(metricOf(alarmName)).toMatchObject({ namespace: 'WeredaConnect/dev', name: metricName, stat: 'Sum', period });
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: alarmName,
      Threshold: threshold,
      EvaluationPeriods: 1,
      ComparisonOperator: 'GreaterThanOrEqualToThreshold',
      TreatMissingData: 'notBreaching',
      AlarmDescription: Match.stringLikeRegexp(token),
    });
  });
});
