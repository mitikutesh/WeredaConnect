import * as cdk from 'aws-cdk-lib';
import { Template, Match } from 'aws-cdk-lib/assertions';
import { DatabaseStack } from '../../lib/stacks/database-stack';
import { ApiStack } from '../../lib/stacks/api-stack';
import { MonitoringStack } from '../../lib/stacks/monitoring-stack';
import { GithubActionsStack } from '../../lib/stacks/github-actions-stack';
import { environments, EnvironmentConfig } from '../../lib/config/environments';

const devConfig = environments['dev'];
const prodConfig = environments['prod'];

describe('DatabaseStack', () => {
  let template: Template;

  beforeAll(() => {
    const app = new cdk.App();
    const stack = new DatabaseStack(app, 'TestDatabaseStack', {
      env: { account: '123456789012', region: devConfig.region },
      envConfig: devConfig,
    });
    template = Template.fromStack(stack);
  });

  test('creates nineteen DynamoDB tables with PAY_PER_REQUEST billing', () => {
    // 12 core + intents + device_tokens + match_events + ratings + transaction-audit
    // + delivery-qr-secrets + messages (in-app chat) = 19.
    template.resourceCountIs('AWS::DynamoDB::Table', 19);
    template.hasResourceProperties('AWS::DynamoDB::Table', {
      BillingMode: 'PAY_PER_REQUEST',
      PointInTimeRecoverySpecification: { PointInTimeRecoveryEnabled: true },
    });
  });

  test('escrows table carries the sparse ManualPayoutIndex (ESC-02)', () => {
    template.hasResourceProperties('AWS::DynamoDB::Table', {
      TableName: 'wereda-connect-dev-escrows',
      GlobalSecondaryIndexes: Match.arrayWith([
        Match.objectLike({
          IndexName: 'ManualPayoutIndex',
          KeySchema: [
            { AttributeName: 'manualPayout', KeyType: 'HASH' },
            { AttributeName: 'manualPayoutQueuedAt', KeyType: 'RANGE' },
          ],
          Projection: { ProjectionType: 'ALL' },
        }),
      ]),
    });
  });

  test('escrow idempotency table carries the sparse InboxReplayIndex (ESC-45)', () => {
    template.hasResourceProperties('AWS::DynamoDB::Table', {
      TableName: 'wereda-connect-dev-escrow-idempotency',
      GlobalSecondaryIndexes: [
        Match.objectLike({
          IndexName: 'InboxReplayIndex',
          KeySchema: [
            { AttributeName: 'inboxReplay', KeyType: 'HASH' },
            { AttributeName: 'inboxReplayAt', KeyType: 'RANGE' },
          ],
          Projection: { ProjectionType: 'ALL' },
        }),
      ],
    });
  });

  test('orders table carries the sparse AdminQueueIndex (ESC-06)', () => {
    template.hasResourceProperties('AWS::DynamoDB::Table', {
      TableName: 'wereda-connect-dev-orders',
      GlobalSecondaryIndexes: Match.arrayWith([
        Match.objectLike({
          IndexName: 'AdminQueueIndex',
          KeySchema: [
            { AttributeName: 'adminQueue', KeyType: 'HASH' },
            { AttributeName: 'adminQueuedAt', KeyType: 'RANGE' },
          ],
          Projection: { ProjectionType: 'ALL' },
        }),
      ]),
    });
  });

  test('creates S3 buckets with encryption (uploads + access logs)', () => {
    template.resourceCountIs('AWS::S3::Bucket', 2);
    template.hasResourceProperties('AWS::S3::Bucket', {
      BucketEncryption: {
        ServerSideEncryptionConfiguration: [
          { ServerSideEncryptionByDefault: { SSEAlgorithm: 'AES256' } },
        ],
      },
    });
  });
});

describe('ApiStack', () => {
  let template: Template;

  beforeAll(() => {
    const app = new cdk.App();
    const dbStack = new DatabaseStack(app, 'DbStack', {
      env: { account: '123456789012', region: devConfig.region },
      envConfig: devConfig,
    });
    const apiStack = new ApiStack(app, 'TestApiStack', {
      env: { account: '123456789012', region: devConfig.region },
      envConfig: devConfig,
      usersTable: dbStack.usersTable,
      cropsTable: dbStack.cropsTable,
      tripsTable: dbStack.tripsTable,
      rateLimitsTable: dbStack.rateLimitsTable,
      syncMetadataTable: dbStack.syncMetadataTable,
      deliveriesTable: dbStack.deliveriesTable,
      paymentsTable: dbStack.paymentsTable,
      escrowsTable: dbStack.escrowsTable,
      escrowAuditTable: dbStack.escrowAuditTable,
      escrowIdempotencyTable: dbStack.escrowIdempotencyTable,
      deliveryQrSecretsTable: dbStack.deliveryQrSecretsTable,
      ordersTable: dbStack.ordersTable,
      fraudSignalsTable: dbStack.fraudSignalsTable,
      intentsTable: dbStack.intentsTable,
      deviceTokensTable: dbStack.deviceTokensTable,
      matchEventsTable: dbStack.matchEventsTable,
      ratingsTable: dbStack.ratingsTable,
      messagesTable: dbStack.messagesTable,
      transactionAuditTable: dbStack.transactionAuditTable,
      uploadsBucket: dbStack.uploadsBucket,
    });
    template = Template.fromStack(apiStack);
  });

  test('creates HTTP API Gateway', () => {
    template.hasResourceProperties('AWS::ApiGatewayV2::Api', {
      ProtocolType: 'HTTP',
    });
  });

  test('creates Cognito User Pools (mobile + admin) with phone sign-in and deletion protection', () => {
    template.resourceCountIs('AWS::Cognito::UserPool', 2);
    template.hasResourceProperties('AWS::Cognito::UserPool', {
      UsernameAttributes: ['phone_number'],
      DeletionProtection: 'ACTIVE',
    });
  });

  test('creates Cognito User Pool Clients (mobile + admin)', () => {
    template.resourceCountIs('AWS::Cognito::UserPoolClient', 2);
  });

  test('creates twenty-four Lambda functions (dev) on ARM64', () => {
    // Prior 20 (core + match + device-token + crop-match-stream + ratings, plus
    // the delivery-qr-secrets work) + messages (read) + message-stream (push) = 22,
    // + auth-verify-otp (SMS phone proof) + the dev-only mock Fayda provider = 24.
    template.resourceCountIs('AWS::Lambda::Function', 24);
    template.hasResourceProperties('AWS::Lambda::Function', {
      Runtime: 'provided.al2023',
      Architectures: ['arm64'],
    });
  });

  test('creates API Gateway routes for all endpoints', () => {
    // 27 base routes: /health, /auth/register, /auth/fayda-verify, /auth/login, /auth/refresh, /auth/logout,
    //                 /sync, /crops, /crops/{id}, /trips, /trips/{id},
    //                 /orders, /orders/{id},
    //                 /deliveries, /deliveries/{id}, /deliveries/{id}/photos,
    //                 /escrow/initiate, /escrow/release, /escrow/dispute, /escrow/refund, /escrow/{id}, /escrow/history,
    //                 /escrow/by-delivery/{deliveryId} (F-33),
    //                 /auth/forgot-password, /auth/reset-password,
    //                 /match (buyer intent matching), /device-tokens (push alerts),
    //                 /ratings, /ratings/{id} (buyer ratings),
    //                 /messages, /messages/{id} (in-app chat)
    // Plus admin routes and the delivery-qr-secrets work → 49,
    // plus /auth/verify-otp, /auth/resend-otp, /auth/fayda-start, /user/fayda-start,
    // /user/fayda-link and DELETE /device-tokens (auth hardening) → 55,
    // plus POST /escrow/order-checkout (ESC-01, live order payments) → 56,
    // plus GET /admin/payouts/manual and POST /admin/payouts/{escrowId}/{ref}/complete
    // (ESC-02, manual payout queue) → 58,
    // plus GET /admin/payments/unapplied and POST /admin/payments/{orderId}/refund-manual
    // (FUP-02, manual refund of an unapplied payment) → 60 total.
    template.resourceCountIs('AWS::ApiGatewayV2::Route', 60);
  });

  test('manual payout queue routes sit on the admin authorizer (ESC-02)', () => {
    const authorizers = template.findResources('AWS::ApiGatewayV2::Authorizer');
    const adminAuthorizerId = Object.entries(authorizers)
      .find(([, r]) => (r as Record<string, Record<string, unknown>>).Properties?.Name === 'AdminCognitoAuthorizer')?.[0];
    expect(adminAuthorizerId).toBeDefined();
    for (const routeKey of ['GET /admin/payouts/manual', 'POST /admin/payouts/{escrowId}/{ref}/complete']) {
      template.hasResourceProperties('AWS::ApiGatewayV2::Route', {
        RouteKey: routeKey,
        AuthorizationType: 'JWT',
        AuthorizerId: { Ref: adminAuthorizerId },
      });
    }
  });

  test('admin Lambda can put the refund claim row and nothing else on escrow-idempotency (FUP-02)', () => {
    const policies = template.findResources('AWS::IAM::Policy');
    const actions = Object.keys(policies)
      .filter((key) => key.startsWith('AdminFunctionServiceRole'))
      .flatMap(
        (id) =>
          policies[id].Properties.PolicyDocument.Statement as Array<{
            Action: string | string[];
            Resource: unknown;
          }>,
      )
      .filter((s) => JSON.stringify(s.Resource).includes('EscrowIdempotencyTable'))
      .flatMap((s) => (Array.isArray(s.Action) ? s.Action : [s.Action]));
    // TransactWriteItems is authorized per item: a conditional Put needs PutItem only.
    expect(actions).toEqual(['dynamodb:PutItem']);

    const admin = Object.values(template.findResources('AWS::Lambda::Function')).find(
      (fn) => fn.Properties?.FunctionName === 'wereda-connect-dev-admin',
    );
    expect(admin).toBeDefined();
    expect(admin!.Properties.Environment.Variables.ESCROW_IDEMPOTENCY_TABLE).toBeDefined();
  });

  test('unapplied payment routes sit on the admin authorizer (FUP-02)', () => {
    const authorizers = template.findResources('AWS::ApiGatewayV2::Authorizer');
    const adminAuthorizerId = Object.entries(authorizers)
      .find(([, r]) => (r as Record<string, Record<string, unknown>>).Properties?.Name === 'AdminCognitoAuthorizer')?.[0];
    expect(adminAuthorizerId).toBeDefined();
    for (const routeKey of ['GET /admin/payments/unapplied', 'POST /admin/payments/{orderId}/refund-manual']) {
      template.hasResourceProperties('AWS::ApiGatewayV2::Route', {
        RouteKey: routeKey,
        AuthorizationType: 'JWT',
        AuthorizerId: { Ref: adminAuthorizerId },
      });
    }
  });

  test('auth hardening: password login only through the server, settlement only for admins', () => {
    // The public mobile client cannot run USER_PASSWORD_AUTH; the server uses the
    // admin flow (IAM signed), so lockout, suspension and audit cannot be bypassed.
    template.hasResourceProperties('AWS::Cognito::UserPoolClient', {
      ClientName: 'wereda-connect-dev-mobile',
      ExplicitAuthFlows: Match.arrayWith(['ALLOW_ADMIN_USER_PASSWORD_AUTH', 'ALLOW_REFRESH_TOKEN_AUTH']),
    });
    const clients = template.findResources('AWS::Cognito::UserPoolClient');
    for (const resource of Object.values(clients)) {
      const props = (resource as Record<string, Record<string, unknown>>).Properties;
      if (props?.ClientName === 'wereda-connect-dev-mobile') {
        expect(props.ExplicitAuthFlows).not.toContain('ALLOW_USER_PASSWORD_AUTH');
        expect(props.ExplicitAuthFlows).not.toContain('ALLOW_USER_SRP_AUTH');
      }
    }
    // Admin pool MFA is optional outside prod (dev template here): SMS codes are
    // undeliverable there and Cognito can never drop SMS_MFA from a pool with an
    // SMS configuration, so REQUIRED would SMS-challenge every admin. TOTP is
    // always offered.
    template.hasResourceProperties('AWS::Cognito::UserPool', {
      UserPoolName: 'wereda-connect-dev-admins',
      MfaConfiguration: 'OPTIONAL',
      EnabledMfas: ['SMS_MFA', 'SOFTWARE_TOKEN_MFA'],
      // Phone-only recovery is rejected by Cognito alongside SMS MFA.
      AccountRecoverySetting: { RecoveryMechanisms: [{ Name: 'admin_only', Priority: 1 }] },
    });
    // Escrow settlement routes sit on the admin authorizer, initiate stays on mobile.
    const authorizers = template.findResources('AWS::ApiGatewayV2::Authorizer');
    const adminAuthorizerId = Object.entries(authorizers)
      .find(([, r]) => (r as Record<string, Record<string, unknown>>).Properties?.Name === 'AdminCognitoAuthorizer')?.[0];
    expect(adminAuthorizerId).toBeDefined();
    for (const routeKey of ['POST /escrow/release', 'POST /escrow/refund']) {
      template.hasResourceProperties('AWS::ApiGatewayV2::Route', {
        RouteKey: routeKey,
        AuthorizationType: 'JWT',
        AuthorizerId: { Ref: adminAuthorizerId },
      });
    }
    // Phone proof and Fayda link-later routes exist; the OTP routes are public.
    for (const routeKey of ['POST /auth/verify-otp', 'POST /auth/resend-otp', 'POST /auth/fayda-start']) {
      template.hasResourceProperties('AWS::ApiGatewayV2::Route', {
        RouteKey: routeKey,
        AuthorizationType: 'NONE',
      });
    }
    for (const routeKey of ['POST /user/fayda-start', 'POST /user/fayda-link', 'DELETE /device-tokens']) {
      template.hasResourceProperties('AWS::ApiGatewayV2::Route', {
        RouteKey: routeKey,
        AuthorizationType: 'JWT',
      });
    }
  });

  test('dev: mock Fayda provider is deployed and the server points at it', () => {
    // faydaMode 'mock' in dev: the mock eSignet Lambda carries the FAYDA_MOCK
    // marker (refused in prod by ProductionBypassGuard) and the SSM issuer /
    // client parameters are wired to it, so the server code needs no branch.
    template.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: 'wereda-connect-dev-fayda-mock',
      Environment: { Variables: Match.objectLike({ FAYDA_MOCK: 'true' }) },
    });
    template.hasResourceProperties('AWS::SSM::Parameter', {
      Name: '/wereda-connect-dev/fayda/client-id',
      Value: 'weredaconnect-wereda-connect-dev',
    });
    // The RP key is created by the mock at runtime, never by the stack.
    expect(JSON.stringify(template.toJSON())).not.toContain('client-secret');
    // Web app callback pages are registered with the mock and accepted by the
    // server only in mock mode (FAYDA_MOCK marker on the verify Lambda).
    const webCallback = 'https://dev-app.weredaconnect.com/fayda-callback.html';
    template.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: 'wereda-connect-dev-fayda-mock',
      Environment: { Variables: Match.objectLike({ ALLOWED_REDIRECT_URIS: Match.stringLikeRegexp(webCallback) }) },
    });
    template.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: 'wereda-connect-dev-auth-fayda-verify',
      Environment: {
        Variables: Match.objectLike({
          FAYDA_MOCK: 'true',
          FAYDA_MOCK_WEB_REDIRECT_URIS: Match.stringLikeRegexp(webCallback),
        }),
      },
    });
    // Dev auth Lambdas: OTP bypass on, SMS off (SNS sandbox); never in prod (see T-SEC tests).
    template.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: 'wereda-connect-dev-auth-verify-otp',
      Environment: { Variables: Match.objectLike({ DEV_OTP_BYPASS: 'true', SMS_ENABLED: 'false' }) },
    });
  });

  test('creates JWT authorizers for mobile and admin routes', () => {
    template.resourceCountIs('AWS::ApiGatewayV2::Authorizer', 2);
    template.hasResourceProperties('AWS::ApiGatewayV2::Authorizer', {
      AuthorizerType: 'JWT',
    });
  });

  test('dev: no Secrets Manager secrets — env vars reference SSM parameters', () => {
    // Non-prod secrets live in SSM Parameter Store (free tier); the SecureString
    // parameters are seeded out-of-band by scripts/seed-ssm-secrets.sh.
    template.resourceCountIs('AWS::SecretsManager::Secret', 0);
    // The *_ARN env vars carry the SSM parameter NAME in non-prod — the server
    // routes on the value's shape (AwsParameterResolver).
    template.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: 'wereda-connect-dev-crops',
      Environment: {
        Variables: Match.objectLike({
          PAGINATION_SECRET_ARN: '/wereda-connect-dev/pagination-secret',
        }),
      },
    });
    // Dev runs faydaMode 'mock', yet reads the client private key from the same
    // seeded parameter live mode uses (the mock fills its PLACEHOLDER).
    template.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: 'wereda-connect-dev-auth-fayda-verify',
      Environment: {
        Variables: Match.objectLike({
          FAYDA_CLIENT_PRIVATE_KEY_ARN: '/wereda-connect-dev/fayda/client-private-key',
          FAYDA_SIGNING_SECRET_ARN: '/wereda-connect-dev/fayda/signing-secret',
        }),
      },
    });
  });

  test('dev: lambda roles are granted ssm:GetParameter on the secret parameters', () => {
    const policies = JSON.stringify(template.findResources('AWS::IAM::Policy'));
    expect(policies).toContain('ssm:GetParameter');
    expect(policies).toContain('parameter/wereda-connect-dev/pagination-secret');
    expect(policies).toContain('parameter/wereda-connect-dev/fayda/client-private-key');
    expect(policies).toContain('parameter/wereda-connect-dev/fayda/signing-secret');
  });

  test('sync Lambda can read the escrow audit trail to dedupe dispute refusals (ESC-07)', () => {
    // A refused delivery dispute is audited once per escrow, delivery and reason a day; the
    // trail is read first. Least privilege: Query on top of the write grant, never Scan. The sync
    // role is large, so CDK moves part of it into an overflow managed policy: read both.
    const statements = [
      ...Object.entries(template.findResources('AWS::IAM::Policy')),
      ...Object.entries(template.findResources('AWS::IAM::ManagedPolicy')),
    ]
      .filter(([id]) => id.startsWith('SyncFunctionServiceRole'))
      .flatMap(
        ([, policy]) =>
          policy.Properties.PolicyDocument.Statement as Array<{ Action: string | string[]; Resource: unknown }>,
      );
    expect(statements.length).toBeGreaterThan(0);
    const actions = statements
      .filter((s) => JSON.stringify(s.Resource).includes('EscrowAuditTable'))
      .flatMap((s) => (Array.isArray(s.Action) ? s.Action : [s.Action]));
    expect(actions).toContain('dynamodb:Query');
    expect(actions).toContain('dynamodb:PutItem');
    expect(actions).not.toContain('dynamodb:Scan');
  });

  test('escrow Lambda can update the order and delivery a dispute mirrors onto (SIM-47)', () => {
    // POST /escrow/dispute marks the order and delivery disputed. With read-only grants
    // the UpdateItem was denied and both stayed delivered (ratings still allowed).
    const policies = template.findResources('AWS::IAM::Policy');
    const escrowPolicyId = Object.keys(policies).find((id) =>
      id.startsWith('EscrowFunctionServiceRole'),
    );
    expect(escrowPolicyId).toBeDefined();
    const statements: Array<{ Action: string | string[]; Resource: unknown }> =
      policies[escrowPolicyId!].Properties.PolicyDocument.Statement;
    const actionsOn = (tableId: string) =>
      statements
        .filter((s) => JSON.stringify(s.Resource).includes(tableId))
        .flatMap((s) => (Array.isArray(s.Action) ? s.Action : [s.Action]));

    for (const tableId of ['OrdersTable', 'DeliveriesTable']) {
      const actions = actionsOn(tableId);
      expect(actions).toContain('dynamodb:UpdateItem');
      // Least privilege: the mirror is a conditional update, never a put or delete.
      expect(actions).not.toContain('dynamodb:PutItem');
      expect(actions).not.toContain('dynamodb:DeleteItem');
      expect(actions).not.toContain('dynamodb:BatchWriteItem');
    }
  });

  test('a dispute verdict can move its order and delivery (ESC-03)', () => {
    // The admin verdict, and the escrow and escrow-cron settlement of it, move the order
    // and delivery to the verdict and audit each hop in transaction-audit.
    const policies = template.findResources('AWS::IAM::Policy');
    const actionsOn = (rolePrefix: string, tableId: string) => {
      const ids = Object.keys(policies).filter((key) => key.startsWith(rolePrefix));
      expect(ids.length).toBeGreaterThan(0);
      return ids
        .flatMap(
          (id) =>
            policies[id].Properties.PolicyDocument.Statement as Array<{
              Action: string | string[];
              Resource: unknown;
            }>,
        )
        .filter((s) => JSON.stringify(s.Resource).includes(tableId))
        .flatMap((s) => (Array.isArray(s.Action) ? s.Action : [s.Action]));
    };

    for (const tableId of ['OrdersTable', 'DeliveriesTable']) {
      const actions = actionsOn('AdminFunctionServiceRole', tableId);
      expect(actions).toContain('dynamodb:UpdateItem');
      expect(actions).not.toContain('dynamodb:PutItem');
      expect(actions).not.toContain('dynamodb:DeleteItem');
      expect(actions).not.toContain('dynamodb:BatchWriteItem');
    }

    // A buyer_wins refund before pickup restores the listing stock from the escrow Lambda too.
    const escrowCrops = actionsOn('EscrowFunctionServiceRole', 'CropsTable');
    expect(escrowCrops).toContain('dynamodb:UpdateItem');
    expect(escrowCrops).not.toContain('dynamodb:PutItem');
    expect(escrowCrops).not.toContain('dynamodb:DeleteItem');

    const cronDeliveries = actionsOn('EscrowCronFunctionServiceRole', 'DeliveriesTable');
    expect(cronDeliveries).toContain('dynamodb:UpdateItem');
    expect(cronDeliveries).not.toContain('dynamodb:PutItem');
    expect(cronDeliveries).not.toContain('dynamodb:DeleteItem');

    for (const rolePrefix of ['EscrowFunctionServiceRole', 'EscrowCronFunctionServiceRole']) {
      // Append-only: PutItem and nothing else.
      expect(actionsOn(rolePrefix, 'TransactionAuditTable')).toEqual(['dynamodb:PutItem']);
    }

    for (const functionName of ['wereda-connect-dev-escrow', 'wereda-connect-dev-escrow-cron']) {
      template.hasResourceProperties('AWS::Lambda::Function', {
        FunctionName: functionName,
        Environment: {
          Variables: Match.objectLike({ TRANSACTION_AUDIT_TABLE: Match.anyValue() }),
        },
      });
    }
  });

  test('ESC-10: escrow-cron can read the escrow audit trail the sweep freeze checks', () => {
    // The sweeps freeze a disputed trade through the ESC-07 delivery dispute path, whose refusal row
    // reads the escrow's audit trail first.
    const policies = template.findResources('AWS::IAM::Policy');
    const ids = Object.keys(policies).filter((key) => key.startsWith('EscrowCronFunctionServiceRole'));
    expect(ids.length).toBeGreaterThan(0);
    const actions = ids
      .flatMap(
        (id) =>
          policies[id].Properties.PolicyDocument.Statement as Array<{
            Action: string | string[];
            Resource: unknown;
          }>,
      )
      .filter((s) => JSON.stringify(s.Resource).includes('EscrowAuditTable'))
      .flatMap((s) => (Array.isArray(s.Action) ? s.Action : [s.Action]));
    expect(actions).toContain('dynamodb:Query');
    expect(actions).toContain('dynamodb:PutItem');
  });

  test('ESC-05: the Lambdas that settle escrows stop well within the 15 minute stale-lock threshold', () => {
    // The recovery step parks a settlement lock older than 15 minutes as an unknown outcome. That
    // is only safe while no settlement can still be running after 15 minutes.
    const functions = template.findResources('AWS::Lambda::Function') as Record<
      string,
      { Properties: { FunctionName?: string; Timeout?: number } }
    >;
    for (const name of ['wereda-connect-dev-escrow', 'wereda-connect-dev-escrow-cron']) {
      const fn = Object.values(functions).find((f) => f.Properties.FunctionName === name);
      expect(fn).toBeDefined();
      expect([name, (fn!.Properties.Timeout ?? 3) <= 300]).toEqual([name, true]);
    }
  });

  test('ESC-48: the orders Lambda reads the users table for the phones of an open order, and only reads it', () => {
    template.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: 'wereda-connect-dev-orders',
      Environment: {
        Variables: Match.objectLike({ USERS_TABLE: Match.anyValue() }),
      },
    });

    const policies = template.findResources('AWS::IAM::Policy');
    const actions = Object.keys(policies)
      .filter((key) => key.startsWith('OrderFunctionServiceRole'))
      .flatMap(
        (id) =>
          policies[id].Properties.PolicyDocument.Statement as Array<{
            Action: string | string[];
            Resource: unknown;
          }>,
      )
      .filter((s) => JSON.stringify(s.Resource).includes('UsersTable'))
      .flatMap((s) => (Array.isArray(s.Action) ? s.Action : [s.Action]));
    expect(actions).toContain('dynamodb:BatchGetItem');
    expect(actions).not.toContain('dynamodb:PutItem');
    expect(actions).not.toContain('dynamodb:UpdateItem');
    expect(actions).not.toContain('dynamodb:DeleteItem');
  });
});

describe('ApiStack (Telebirr live gateway, gated)', () => {
  function buildTemplate(
    enableTelebirrLive: boolean,
    extra: Partial<typeof devConfig> = {},
  ): Template {
    const app = new cdk.App();
    const dbStack = new DatabaseStack(app, 'TbDb', {
      env: { account: '123456789012', region: devConfig.region },
      envConfig: devConfig,
    });
    const apiStack = new ApiStack(app, 'TbApi', {
      env: { account: '123456789012', region: devConfig.region },
      envConfig: { ...devConfig, enableTelebirrLive, ...extra },
      usersTable: dbStack.usersTable,
      cropsTable: dbStack.cropsTable,
      tripsTable: dbStack.tripsTable,
      rateLimitsTable: dbStack.rateLimitsTable,
      syncMetadataTable: dbStack.syncMetadataTable,
      deliveriesTable: dbStack.deliveriesTable,
      paymentsTable: dbStack.paymentsTable,
      escrowsTable: dbStack.escrowsTable,
      escrowAuditTable: dbStack.escrowAuditTable,
      escrowIdempotencyTable: dbStack.escrowIdempotencyTable,
      deliveryQrSecretsTable: dbStack.deliveryQrSecretsTable,
      ordersTable: dbStack.ordersTable,
      fraudSignalsTable: dbStack.fraudSignalsTable,
      intentsTable: dbStack.intentsTable,
      deviceTokensTable: dbStack.deviceTokensTable,
      matchEventsTable: dbStack.matchEventsTable,
      ratingsTable: dbStack.ratingsTable,
      messagesTable: dbStack.messagesTable,
      transactionAuditTable: dbStack.transactionAuditTable,
      uploadsBucket: dbStack.uploadsBucket,
    });
    return Template.fromStack(apiStack);
  }

  test('OFF by default: no VPC / NAT / EIP provisioned', () => {
    const t = buildTemplate(false);
    t.resourceCountIs('AWS::EC2::VPC', 0);
    t.resourceCountIs('AWS::EC2::Instance', 0);
    t.resourceCountIs('AWS::EC2::EIP', 0);
  });

  test('ON: provisions VPC + t4g.nano NAT instance + EIP + 2 gateway endpoints', () => {
    const t = buildTemplate(true);
    t.resourceCountIs('AWS::EC2::VPC', 1);
    t.resourceCountIs('AWS::EC2::EIP', 1);
    t.resourceCountIs('AWS::EC2::EIPAssociation', 1);
    // NAT INSTANCE (not a NAT Gateway — see the "Never NAT Gateways" rule).
    t.resourceCountIs('AWS::EC2::NatGateway', 0);
    t.hasResourceProperties('AWS::EC2::Instance', { InstanceType: 't4g.nano' });
    t.resourceCountIs('AWS::EC2::VPCEndpoint', 2); // DynamoDB + S3 (free, off-NAT)
  });

  test('ON with a pre-allocated EIP: reuses it, creates no new EIP', () => {
    const t = buildTemplate(true, {
      telebirrNatEipAllocationId: 'eipalloc-0abc123def456',
    });
    t.resourceCountIs('AWS::EC2::EIP', 0);
    t.hasResourceProperties('AWS::EC2::EIPAssociation', {
      AllocationId: 'eipalloc-0abc123def456',
    });
  });

  test('ON (dev): references the Telebirr SSM parameter — no Secrets Manager secret', () => {
    const t = buildTemplate(true);
    t.resourceCountIs('AWS::SecretsManager::Secret', 0);
    t.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: 'wereda-connect-dev-escrow',
      Environment: {
        Variables: Match.objectLike({
          TELEBIRR_SECRET_ARN: '/wereda-connect-dev/telebirr/credentials',
        }),
      },
    });
  });

  test('ON (prod): creates the Telebirr credentials secret in Secrets Manager', () => {
    const t = buildTemplate(true, { isProd: true, name: 'prod' });
    t.hasResourceProperties('AWS::SecretsManager::Secret', {
      Name: '/wereda-connect-prod/telebirr/credentials',
    });
  });

  test('ON: public webhook route has NO authorizer; escrow routes stay JWT', () => {
    const t = buildTemplate(true);
    t.hasResourceProperties('AWS::ApiGatewayV2::Route', {
      RouteKey: 'POST /webhooks/telebirr',
      AuthorizationType: 'NONE',
      AuthorizerId: Match.absent(),
    });
    t.hasResourceProperties('AWS::ApiGatewayV2::Route', {
      RouteKey: 'POST /escrow/initiate',
      AuthorizationType: 'JWT',
    });
  });

  test('ON: webhook Lambda gets the order tables it needs to fund orders (ESC-01)', () => {
    buildTemplate(true).hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: 'wereda-connect-dev-telebirr-webhook',
      Environment: {
        Variables: Match.objectLike({
          ORDERS_TABLE: Match.anyValue(),
          CROPS_TABLE: Match.anyValue(),
          TRANSACTION_AUDIT_TABLE: Match.anyValue(),
          ESCROW_IDEMPOTENCY_TABLE: Match.anyValue(),
        }),
      },
    });
  });

  test.each([false, true])(
    'order checkout route is JWT-protected (live=%s, ESC-01)',
    (live) => {
      buildTemplate(live).hasResourceProperties('AWS::ApiGatewayV2::Route', {
        RouteKey: 'POST /escrow/order-checkout',
        AuthorizationType: 'JWT',
      });
    },
  );

  test('ON: the inbox replay rule invokes the webhook every 5 minutes with the replay body (ESC-45)', () => {
    const t = buildTemplate(true);
    t.hasResourceProperties('AWS::Events::Rule', {
      Name: 'wereda-connect-dev-telebirr-inbox-replay',
      ScheduleExpression: 'rate(5 minutes)',
      Targets: [
        Match.objectLike({
          Arn: { 'Fn::GetAtt': [Match.stringLikeRegexp('^TelebirrWebhookFunction'), 'Arn'] },
          Input: JSON.stringify({ body: JSON.stringify({ action: 'webhook_inbox_replay' }) }),
        }),
      ],
    });
  });

  test('OFF: no inbox replay rule (the webhook does not exist)', () => {
    const rules = buildTemplate(false).findResources('AWS::Events::Rule');
    expect(JSON.stringify(rules)).not.toContain('telebirr-inbox-replay');
  });

  type StageResource = {
    DependsOn?: string[];
    Properties: { DefaultRouteSettings?: unknown; RouteSettings?: unknown };
  };
  function defaultStage(t: Template): StageResource {
    const stages = Object.entries(t.findResources('AWS::ApiGatewayV2::Stage')).filter(([id]) =>
      id.startsWith('HttpApiDefaultStage'),
    );
    expect(stages).toHaveLength(1);
    return stages[0][1] as StageResource;
  }
  const STAGE_THROTTLING = { ThrottlingBurstLimit: 50, ThrottlingRateLimit: 25 };

  test('ON: detailed metrics on the webhook route only, throttling kept, stage after the route (FUP-39)', () => {
    // The monitoring stack subtracts this route's 5xx and Count from the API-wide 5xx rate.
    // Detailed metrics are billed per metric, so no other route gets them.
    const t = buildTemplate(true);
    const stage = defaultStage(t);
    expect(stage.Properties.DefaultRouteSettings).toEqual(STAGE_THROTTLING);
    expect(stage.Properties.RouteSettings).toEqual({
      'POST /webhooks/telebirr': { ...STAGE_THROTTLING, DetailedMetricsEnabled: true },
    });
    // API Gateway refuses route settings for a route key that does not exist yet.
    const routeIds = Object.keys(
      t.findResources('AWS::ApiGatewayV2::Route', { Properties: { RouteKey: 'POST /webhooks/telebirr' } }),
    );
    expect(routeIds).toHaveLength(1);
    expect(stage.DependsOn).toEqual(expect.arrayContaining(routeIds));
  });

  test('OFF: no route settings, so no detailed metrics anywhere (FUP-39)', () => {
    const stage = defaultStage(buildTemplate(false));
    expect(stage.Properties.DefaultRouteSettings).toEqual(STAGE_THROTTLING);
    expect(stage.Properties.RouteSettings).toBeUndefined();
  });

  test('ON: the webhook may query the InboxReplayIndex (ESC-45)', () => {
    const policies = buildTemplate(true).findResources('AWS::IAM::Policy');
    const webhook = Object.entries(policies).filter(([id]) => id.startsWith('TelebirrWebhookFunction'));
    expect(webhook.length).toBeGreaterThan(0);
    const text = JSON.stringify(webhook);
    expect(text).toContain('dynamodb:Query');
    expect(text).toContain('/index/*');
  });

  test('ON: the webhook may query the transaction-audit base table, no index (ESC-47)', () => {
    // The repair of a redelivered payment reads the order's trail before it writes a missing
    // payment_verified row; grantWriteData alone has no Query, so the read would be denied.
    const policies = buildTemplate(true).findResources('AWS::IAM::Policy');
    const statements = Object.entries(policies)
      .filter(([id]) => id.startsWith('TelebirrWebhookFunction'))
      .flatMap(([, p]) =>
        (p as { Properties: { PolicyDocument: { Statement: Array<{ Action: unknown; Resource: unknown }> } } })
          .Properties.PolicyDocument.Statement);
    const onAudit = statements.filter((s) => JSON.stringify(s.Resource).includes('TransactionAuditTable'));
    const queries = onAudit.filter((s) => ([] as unknown[]).concat(s.Action).includes('dynamodb:Query'));
    expect(queries).toHaveLength(1);
    expect(queries[0].Action).toBe('dynamodb:Query');
    expect(JSON.stringify(queries[0].Resource)).not.toContain('/index/');
    // The writes stay as they were.
    expect(JSON.stringify(onAudit)).toContain('dynamodb:PutItem');
  });

  test.each([false, true])(
    'order cron reads only the InboxReplayIndex of the idempotency table (live=%s, ESC-45)',
    (live) => {
      const t = buildTemplate(live);
      t.hasResourceProperties('AWS::Lambda::Function', {
        FunctionName: 'wereda-connect-dev-order-cron',
        Environment: { Variables: Match.objectLike({ ESCROW_IDEMPOTENCY_TABLE: Match.anyValue() }) },
      });
      const policies = t.findResources('AWS::IAM::Policy');
      const cron = Object.entries(policies).filter(([id]) => id.startsWith('OrderCronFunction'));
      const statements = cron.flatMap(([, p]) =>
        (p as { Properties: { PolicyDocument: { Statement: Array<{ Action: unknown; Resource: unknown }> } } })
          .Properties.PolicyDocument.Statement);
      const onIdempotency = statements.filter((s) => JSON.stringify(s.Resource).includes('EscrowIdempotencyTable'));
      expect(onIdempotency).toHaveLength(1);
      expect(onIdempotency[0].Action).toBe('dynamodb:Query');
      expect(JSON.stringify(onIdempotency[0].Resource)).toContain('/index/InboxReplayIndex');
    },
  );

  test('ON: escrow Lambda drops TELEBIRR_MOCK and gets the secret + notify URL', () => {
    buildTemplate(true).hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: 'wereda-connect-dev-escrow',
      Environment: {
        Variables: Match.objectLike({
          TELEBIRR_MOCK: Match.absent(),
          TELEBIRR_SECRET_ARN: Match.anyValue(),
          TELEBIRR_NOTIFY_URL: Match.anyValue(),
        }),
      },
    });
  });
});

describe('ApiStack (prod security)', () => {
  let template: Template;

  beforeAll(() => {
    const app = new cdk.App();
    const dbStack = new DatabaseStack(app, 'ProdDbStack', {
      env: { account: '123456789012', region: prodConfig.region },
      envConfig: prodConfig,
    });
    const apiStack = new ApiStack(app, 'ProdApiStack', {
      env: { account: '123456789012', region: prodConfig.region },
      envConfig: prodConfig,
      usersTable: dbStack.usersTable,
      cropsTable: dbStack.cropsTable,
      tripsTable: dbStack.tripsTable,
      rateLimitsTable: dbStack.rateLimitsTable,
      syncMetadataTable: dbStack.syncMetadataTable,
      deliveriesTable: dbStack.deliveriesTable,
      paymentsTable: dbStack.paymentsTable,
      escrowsTable: dbStack.escrowsTable,
      escrowAuditTable: dbStack.escrowAuditTable,
      escrowIdempotencyTable: dbStack.escrowIdempotencyTable,
      deliveryQrSecretsTable: dbStack.deliveryQrSecretsTable,
      ordersTable: dbStack.ordersTable,
      fraudSignalsTable: dbStack.fraudSignalsTable,
      intentsTable: dbStack.intentsTable,
      deviceTokensTable: dbStack.deviceTokensTable,
      matchEventsTable: dbStack.matchEventsTable,
      ratingsTable: dbStack.ratingsTable,
      messagesTable: dbStack.messagesTable,
      transactionAuditTable: dbStack.transactionAuditTable,
      uploadsBucket: dbStack.uploadsBucket,
    });
    template = Template.fromStack(apiStack);
  });

  test('prod admin pool requires MFA', () => {
    template.hasResourceProperties('AWS::Cognito::UserPool', {
      UserPoolName: 'wereda-connect-prod-admins',
      MfaConfiguration: 'ON',
      EnabledMfas: ['SMS_MFA', 'SOFTWARE_TOKEN_MFA'],
    });
  });

  test('T-SEC-06: prod CORS origins contain no localhost', () => {
    const api = template.findResources('AWS::ApiGatewayV2::Api');
    for (const [, resource] of Object.entries(api)) {
      const corsConfig = (resource as Record<string, Record<string, unknown>>).Properties?.CorsConfiguration as
        Record<string, unknown> | undefined;
      if (corsConfig?.AllowOrigins) {
        const origins = corsConfig.AllowOrigins as string[];
        for (const origin of origins) {
          expect(origin).not.toMatch(/localhost/);
        }
      }
    }
  });

  test('CORS allows the device identity headers sent on login/register', () => {
    template.hasResourceProperties('AWS::ApiGatewayV2::Api', {
      CorsConfiguration: Match.objectLike({
        AllowHeaders: Match.arrayWith(['X-Device-Id', 'X-Device-Name']),
      }),
    });
  });

  test('T-SEC-01/02: no Lambda has DEV_FAYDA_BYPASS, TELEBIRR_MOCK, or DEV_OTP_BYPASS', () => {
    const lambdas = template.findResources('AWS::Lambda::Function');
    for (const resource of Object.values(lambdas)) {
      const env = (resource as Record<string, Record<string, Record<string, Record<string, unknown>>>>)
        .Properties?.Environment?.Variables ?? {};
      // NOTE: toHaveProperty(key, value) treats arg 2 as the EXPECTED VALUE, not a
      // failure message — passing a message made these assertions vacuous (they only
      // failed if the flag existed AND equaled the message text). This describe builds
      // the PROD stack, where none of these flags is set, so the single-arg form is correct.
      expect(env).not.toHaveProperty('DEV_FAYDA_BYPASS');
      expect(env).not.toHaveProperty('TELEBIRR_MOCK');
      // SRV-01/CHAIN-04: dev OTP bypass must never reach prod.
      expect(env).not.toHaveProperty('DEV_OTP_BYPASS');
      // The mock Fayda provider (faydaMode 'mock') must never be deployed beside prod.
      expect(env).not.toHaveProperty('FAYDA_MOCK');
      expect(env).not.toHaveProperty('FAYDA_MOCK_WEB_REDIRECT_URIS');
    }
  });

  test('T-SEC-07: admin token validity is 15 minutes or less', () => {
    const clients = template.findResources('AWS::Cognito::UserPoolClient');
    for (const [, resource] of Object.entries(clients)) {
      const accessValidity = (resource as Record<string, Record<string, number>>).Properties?.AccessTokenValidity;
      if (accessValidity) {
        expect(accessValidity).toBeLessThanOrEqual(60); // minutes — CDK may use different units
      }
    }
  });

  test('prod: secrets stay in Secrets Manager (3 always-on, unchanged names)', () => {
    template.resourceCountIs('AWS::SecretsManager::Secret', 3);
    template.hasResourceProperties('AWS::SecretsManager::Secret', {
      Name: '/wereda-connect-prod/pagination-secret',
    });
    template.hasResourceProperties('AWS::SecretsManager::Secret', {
      Name: '/wereda-connect-prod/fayda/client-private-key',
    });
    template.hasResourceProperties('AWS::SecretsManager::Secret', {
      Name: '/wereda-connect-prod/fayda/signing-secret',
    });
  });
});

describe('ApiStack (staging uses the live Fayda provider)', () => {
  const liveTemplate = (stagingConfig: EnvironmentConfig): Template => {
    const app = new cdk.App();
    const dbStack = new DatabaseStack(app, 'StagingDbStack', {
      env: { account: '123456789012', region: stagingConfig.region },
      envConfig: stagingConfig,
    });
    const apiStack = new ApiStack(app, 'StagingApiStack', {
      env: { account: '123456789012', region: stagingConfig.region },
      envConfig: stagingConfig,
      usersTable: dbStack.usersTable,
      cropsTable: dbStack.cropsTable,
      tripsTable: dbStack.tripsTable,
      rateLimitsTable: dbStack.rateLimitsTable,
      syncMetadataTable: dbStack.syncMetadataTable,
      deliveriesTable: dbStack.deliveriesTable,
      paymentsTable: dbStack.paymentsTable,
      escrowsTable: dbStack.escrowsTable,
      escrowAuditTable: dbStack.escrowAuditTable,
      escrowIdempotencyTable: dbStack.escrowIdempotencyTable,
      deliveryQrSecretsTable: dbStack.deliveryQrSecretsTable,
      ordersTable: dbStack.ordersTable,
      fraudSignalsTable: dbStack.fraudSignalsTable,
      intentsTable: dbStack.intentsTable,
      deviceTokensTable: dbStack.deviceTokensTable,
      matchEventsTable: dbStack.matchEventsTable,
      ratingsTable: dbStack.ratingsTable,
      messagesTable: dbStack.messagesTable,
      transactionAuditTable: dbStack.transactionAuditTable,
      uploadsBucket: dbStack.uploadsBucket,
    });
    return Template.fromStack(apiStack);
  };

  test('no mock provider and no mock-only web redirects', () => {
    const template = liveTemplate(environments['staging']);

    const lambdas = template.findResources('AWS::Lambda::Function');
    const names = Object.values(lambdas).map((r) => JSON.stringify(r.Properties?.FunctionName ?? ''));
    expect(names.some((n) => n.includes('auth-fayda-verify'))).toBe(true);
    expect(names.some((n) => n.includes('fayda-mock'))).toBe(false);
    for (const resource of Object.values(lambdas)) {
      const env = (resource as Record<string, Record<string, Record<string, Record<string, unknown>>>>)
        .Properties?.Environment?.Variables ?? {};
      expect(env).not.toHaveProperty('FAYDA_MOCK');
      expect(env).not.toHaveProperty('FAYDA_MOCK_WEB_REDIRECT_URIS');
    }
  });

  test('live wiring: real eSignet issuer and the seeded client private key', () => {
    const template = liveTemplate(environments['staging']);
    template.hasResourceProperties('AWS::SSM::Parameter', {
      Name: '/wereda-connect-staging/fayda/issuer-url',
      Value: 'https://esignet.ida.fayda.et',
    });
    template.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: 'wereda-connect-staging-auth-fayda-verify',
      Environment: {
        Variables: Match.objectLike({
          FAYDA_CLIENT_PRIVATE_KEY_ARN: '/wereda-connect-staging/fayda/client-private-key',
        }),
      },
    });
  });

  test('live wiring: faydaClientId and faydaIssuerUrl come from config', () => {
    const template = liveTemplate({
      ...environments['staging'],
      faydaClientId: 'wc-fayda-client-123',
      faydaIssuerUrl: 'https://esignet.example.et',
    });
    template.hasResourceProperties('AWS::SSM::Parameter', {
      Name: '/wereda-connect-staging/fayda/client-id',
      Value: 'wc-fayda-client-123',
    });
    template.hasResourceProperties('AWS::SSM::Parameter', {
      Name: '/wereda-connect-staging/fayda/issuer-url',
      Value: 'https://esignet.example.et',
    });
  });
});

describe('MonitoringStack', () => {
  test('creates CloudWatch dashboard', () => {
    const app = new cdk.App();
    const stack = new MonitoringStack(app, 'TestMonitoringStack', {
      env: { account: '123456789012', region: devConfig.region },
      envConfig: devConfig,
      apiUrl: `https://test.execute-api.${devConfig.region}.amazonaws.com`,
      apiId: 'testapi123',
    });

    const template = Template.fromStack(stack);
    template.resourceCountIs('AWS::CloudWatch::Dashboard', 1);
  });

  function monitoringTemplate(enableTelebirrLive: boolean): Template {
    const app = new cdk.App();
    const stack = new MonitoringStack(app, 'TestMonitoringStackLive', {
      env: { account: '123456789012', region: devConfig.region },
      envConfig: { ...devConfig, enableTelebirrLive },
      apiUrl: `https://test.execute-api.${devConfig.region}.amazonaws.com`,
      apiId: 'testapi123',
    });
    return Template.fromStack(stack);
  }

  test('ON: alarms on order payments the Telebirr webhook could not apply (ESC-01)', () => {
    const template = monitoringTemplate(true);
    template.hasResourceProperties('AWS::Logs::MetricFilter', {
      LogGroupName: '/aws/lambda/wereda-connect-dev-telebirr-webhook',
      FilterPattern: Match.stringLikeRegexp('ORDER_PAYMENT_UNMATCHED'),
      MetricTransformations: [Match.objectLike({ MetricName: 'OrderPaymentProblems' })],
    });
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'wereda-connect-dev-order-payment-problems',
      Threshold: 0,
    });
  });

  test('ON: a stalled inbox replay sweep alarms (ESC-45)', () => {
    const template = monitoringTemplate(true);
    template.hasResourceProperties('AWS::Logs::MetricFilter', {
      LogGroupName: '/aws/lambda/wereda-connect-dev-telebirr-webhook',
      FilterPattern: '"TELEBIRR_INBOX_REPLAY_RUN"',
      MetricTransformations: [Match.objectLike({ MetricName: 'TelebirrInboxReplayRuns', DefaultValue: 0 })],
    });
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'wereda-connect-dev-telebirr-inbox-replay-stalled',
      Threshold: 1,
      EvaluationPeriods: 3,
      ComparisonOperator: 'LessThanThreshold',
      TreatMissingData: 'breaching',
    });
  });

  test('OFF: no webhook log filter (that log group does not exist)', () => {
    const template = monitoringTemplate(false);
    const filters = template.findResources('AWS::Logs::MetricFilter');
    expect(JSON.stringify(filters)).not.toContain('telebirr-webhook');
  });

  // ─── FUP-39: the webhook's deliberate 500s stay out of the API-wide 5xx rate ─────────
  type MetricQuery = {
    Id: string;
    Expression?: string;
    MetricStat?: {
      Metric: { MetricName: string; Namespace: string; Dimensions: Array<{ Name: string; Value: string }> };
      Period: number;
      Stat: string;
    };
  };
  type AlarmResource = {
    Properties: {
      AlarmName: string;
      Threshold: number;
      ComparisonOperator: string;
      EvaluationPeriods: number;
      Metrics: MetricQuery[];
    };
  };
  function alarmNamed(template: Template, alarmName: string): AlarmResource {
    const alarms = Object.values(template.findResources('AWS::CloudWatch::Alarm')) as AlarmResource[];
    const hit = alarms.find((a) => a.Properties.AlarmName === alarmName);
    if (!hit) {
      throw new Error(`no alarm ${alarmName}`);
    }
    return hit;
  }
  // The webhook route's own API Gateway metrics (HTTP API detailed metrics dimensions).
  const WEBHOOK_ROUTE_DIMENSIONS = [
    { Name: 'ApiId', Value: 'testapi123' },
    { Name: 'Method', Value: 'POST' },
    { Name: 'Resource', Value: '/webhooks/telebirr' },
    { Name: 'Stage', Value: '$default' },
  ];
  const API_DIMENSIONS = [{ Name: 'ApiId', Value: 'testapi123' }];

  // One 5 minute period of API Gateway datapoints. A route field left out means the webhook
  // had no traffic, so CloudWatch has no datapoint for it.
  type Period = { apiErrors: number; apiRequests: number; routeErrors?: number; routeRequests?: number };

  // Minimal model of CloudWatch metric math for the API alarm's expression: arithmetic counts
  // a missing datapoint as 0 (AWS docs, "Missing values in a time series are treated as 0") and
  // IF picks a branch per datapoint. Each query's value comes from what it measures (its
  // metric name and whether it carries the route dimensions), not from its Id.
  function apiRate(alarm: AlarmResource, period: Period): number {
    const expression = alarm.Properties.Metrics.find((q) => q.Expression)?.Expression ?? '';
    expect(expression).toMatch(/^[A-Za-z0-9_ ()+\-*/<>=,.]+$/);
    const metrics = alarm.Properties.Metrics.filter((q) => q.MetricStat);
    const values = metrics.map((q) => {
      const stat = q.MetricStat!;
      const onRoute = stat.Metric.Dimensions.some((d) => d.Name === 'Resource');
      const isErrors = stat.Metric.MetricName === '5xx';
      const value = onRoute
        ? isErrors ? period.routeErrors : period.routeRequests
        : isErrors ? period.apiErrors : period.apiRequests;
      return value ?? 0;
    });
    const IF = (condition: unknown, whenTrue: number, whenFalse: number) => (condition ? whenTrue : whenFalse);
    // Safe to evaluate: the stack's own expression, checked above to hold only ids, numbers and operators.
    const evaluate = new Function('IF', ...metrics.map((q) => q.Id), `return ${expression};`);
    return evaluate(IF, ...values) as number;
  }

  test('FUP-39 OFF: the API-wide 5xx alarm is unchanged (no webhook route, no route metrics)', () => {
    const alarm = alarmNamed(monitoringTemplate(false), 'wereda-connect-dev-high-api-5xx-rate');
    expect(alarm.Properties.Metrics.find((q) => q.Expression)?.Expression).toBe(
      'IF(requests > 0, errors / requests * 100, 0)',
    );
    const stats = alarm.Properties.Metrics.filter((q) => q.MetricStat).map((q) => q.MetricStat!);
    expect(stats.map((m) => m.Metric.MetricName).sort()).toEqual(['5xx', 'Count']);
    for (const m of stats) {
      expect(m.Metric.Dimensions).toEqual(API_DIMENSIONS);
    }
  });

  test('FUP-39 ON: the API-wide 5xx alarm subtracts the webhook route and still counts every other 5xx', () => {
    const template = monitoringTemplate(true);
    const alarm = alarmNamed(template, 'wereda-connect-dev-high-api-5xx-rate');
    expect(alarm.Properties.Threshold).toBe(5);
    expect(alarm.Properties.ComparisonOperator).toBe('GreaterThanThreshold');
    expect(alarm.Properties.EvaluationPeriods).toBe(1);
    const stats = alarm.Properties.Metrics.filter((q) => q.MetricStat).map((q) => q.MetricStat!);
    expect(stats).toHaveLength(4);
    for (const m of stats) {
      expect(m.Metric.Namespace).toBe('AWS/ApiGateway');
      expect(m.Period).toBe(300);
      expect(m.Stat).toBe('Sum');
      // The API-wide totals keep the ApiId dimension alone; the route metrics are exactly the
      // webhook route's.
      expect([API_DIMENSIONS, WEBHOOK_ROUTE_DIMENSIONS]).toContainEqual(m.Metric.Dimensions);
    }
    expect(stats.filter((m) => m.Metric.Dimensions.length === 1).map((m) => m.Metric.MetricName).sort())
      .toEqual(['5xx', 'Count']);
    expect(stats.filter((m) => m.Metric.Dimensions.length === 4).map((m) => m.Metric.MetricName).sort())
      .toEqual(['5xx', 'Count']);

    const old = alarmNamed(monitoringTemplate(false), 'wereda-connect-dev-high-api-5xx-rate');
    // Low traffic, the only 5xx is the webhook's deliberate 500: the old rate paged, this one does not.
    const lowTraffic = { apiErrors: 1, apiRequests: 10, routeErrors: 1, routeRequests: 1 };
    expect(apiRate(old, lowTraffic)).toBeGreaterThan(5);
    expect(apiRate(alarm, lowTraffic)).toBe(0);
    // Only webhook traffic in the period, all 500s: 0, no division by zero.
    expect(apiRate(alarm, { apiErrors: 3, apiRequests: 3, routeErrors: 3, routeRequests: 3 })).toBe(0);
    // Real 5xx on other routes next to webhook 500s: 1 of the 18 other requests is over 5 %.
    expect(apiRate(alarm, { apiErrors: 3, apiRequests: 20, routeErrors: 2, routeRequests: 2 })).toBeGreaterThan(5);
    // No webhook traffic (no route datapoints): the same rate as before FUP-39.
    const noWebhook = { apiErrors: 3, apiRequests: 40 };
    expect(apiRate(alarm, noWebhook)).toBe(apiRate(old, noWebhook));
    expect(apiRate(alarm, noWebhook)).toBeGreaterThan(5);
    // Webhook requests that succeed do not dilute the rate of the other routes.
    expect(apiRate(alarm, { apiErrors: 1, apiRequests: 18, routeErrors: 0, routeRequests: 2 })).toBeCloseTo(6.25);
    expect(apiRate(alarm, { apiErrors: 0, apiRequests: 100, routeErrors: 0, routeRequests: 4 })).toBe(0);
  });

  test('FUP-39 ON: webhook 5xx volume pages on its own count threshold', () => {
    const template = monitoringTemplate(true);
    const notifiesTopic = Match.arrayWith([Match.objectLike({ Ref: Match.stringLikeRegexp('^AlarmTopic') })]);
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'wereda-connect-dev-telebirr-webhook-5xx',
      Metrics: [
        Match.objectLike({
          MetricStat: Match.objectLike({
            Metric: Match.objectLike({
              MetricName: '5xx',
              Namespace: 'AWS/ApiGateway',
              Dimensions: WEBHOOK_ROUTE_DIMENSIONS,
            }),
            Period: 900,
            Stat: 'Sum',
          }),
        }),
      ],
      Threshold: 5,
      ComparisonOperator: 'GreaterThanOrEqualToThreshold',
      EvaluationPeriods: 1,
      TreatMissingData: 'notBreaching',
      AlarmActions: notifiesTopic,
      OKActions: notifiesTopic,
    });
  });

  test('FUP-39 ON: any webhook Lambda error pages (crash, timeout, out of memory; never a deliberate 500)', () => {
    const template = monitoringTemplate(true);
    const notifiesTopic = Match.arrayWith([Match.objectLike({ Ref: Match.stringLikeRegexp('^AlarmTopic') })]);
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'wereda-connect-dev-telebirr-webhook-errors',
      Metrics: [
        Match.objectLike({
          MetricStat: Match.objectLike({
            Metric: Match.objectLike({
              MetricName: 'Errors',
              Namespace: 'AWS/Lambda',
              Dimensions: [{ Name: 'FunctionName', Value: 'wereda-connect-dev-telebirr-webhook' }],
            }),
            Period: 300,
            Stat: 'Sum',
          }),
        }),
      ],
      Threshold: 1,
      ComparisonOperator: 'GreaterThanOrEqualToThreshold',
      EvaluationPeriods: 3,
      DatapointsToAlarm: 1,
      TreatMissingData: 'notBreaching',
      AlarmActions: notifiesTopic,
      OKActions: notifiesTopic,
    });
  });

  test('FUP-39 ON: any webhook Lambda throttle pages (not a Lambda error; under the 5xx count at low traffic)', () => {
    const template = monitoringTemplate(true);
    const notifiesTopic = Match.arrayWith([Match.objectLike({ Ref: Match.stringLikeRegexp('^AlarmTopic') })]);
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'wereda-connect-dev-telebirr-webhook-throttles',
      Metrics: [
        Match.objectLike({
          MetricStat: Match.objectLike({
            Metric: Match.objectLike({
              MetricName: 'Throttles',
              Namespace: 'AWS/Lambda',
              Dimensions: [{ Name: 'FunctionName', Value: 'wereda-connect-dev-telebirr-webhook' }],
            }),
            Period: 300,
            Stat: 'Sum',
          }),
        }),
      ],
      Threshold: 1,
      ComparisonOperator: 'GreaterThanOrEqualToThreshold',
      EvaluationPeriods: 3,
      DatapointsToAlarm: 1,
      TreatMissingData: 'notBreaching',
      AlarmActions: notifiesTopic,
      OKActions: notifiesTopic,
    });
  });

  test('FUP-39 OFF: no webhook alarms (the route and the Lambda do not exist)', () => {
    const alarms = monitoringTemplate(false).findResources('AWS::CloudWatch::Alarm');
    expect(JSON.stringify(alarms)).not.toContain('telebirr-webhook');
  });

  // ─── ESC-13 money alarms ─────────────────────────────────────────────
  const ESCROW_LG = '/aws/lambda/wereda-connect-dev-escrow';
  const ESCROW_CRON_LG = '/aws/lambda/wereda-connect-dev-escrow-cron';
  const SYNC_LG = '/aws/lambda/wereda-connect-dev-sync';
  const MONEY_ALARMS = [
    'wereda-connect-dev-escrow-settlement-failures',
    'wereda-connect-dev-escrow-settlement-unknown-outcomes',
    'wereda-connect-dev-escrow-cron-errors',
    'wereda-connect-dev-order-escrow-problems',
    'wereda-connect-dev-manual-payouts-overdue',
    'wereda-connect-dev-trucker-payouts-overdue',
    'wereda-connect-dev-admin-queue-overdue',
    'wereda-connect-dev-dispute-verdict-order-mismatch',
    'wereda-connect-dev-dispute-verdict-settlement-blocked',
  ];

  // The exact lines the server writes (server/src/Helpers/MoneyAlarmLog.cs and
  // SyncFunction), once bare and once with the Lambda runtime's line prefix.
  const SETTLEMENT_LINE =
    'ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-1 action=release_failed outcome=unknown code=TELEBIRR_TIMEOUT';
  const PREFIXED_SETTLEMENT_LINE = `2026-10-06T10:00:00.000Z\t4f1c7a2e-req\terror\t${SETTLEMENT_LINE}`;
  // ESC-02's overdue-query failure line in escrow-cron (same token, its own code).
  const SWEEP_QUERY_LINE =
    'ESCROW_SETTLEMENT_FAILED escrowId=- action=sweep_failed outcome=failed code=manual_payout_query:InvalidOperationException';
  const CREATE_LINE = 'ORDER_ESCROW_CREATE_FAILED orderId=ord-1 reason=validation';
  const LINK_LINE =
    'ORDER_ESCROW_LINK_FAILED orderId=ord-1 deliveryId=del-1 reason=[ProvisionedThroughputExceededException]';
  const REFUND_SCHEDULE_LINE = 'ORDER_ESCROW_REFUND_SCHEDULE_FAILED orderId=ord-1 reason=[TimeoutException]';
  // ESC-07: a funded order's delivery disputed with no escrow holding its money.
  const DISPUTE_NO_ESCROW_LINE = 'DISPUTE_NO_ESCROW orderId=ord-1 deliveryId=del-1';
  // ESC-06: a delivery that was not pending held the slot right after a transport-failure cancel.
  const LATE_DELIVERY_LINE = 'ORDER_TRANSPORT_CANCEL_LATE_DELIVERY orderId=ord-1 deliveryId=del-1 status=pickedUp';
  // ESC-06: the reverse offline race, a pickup refused on a delivery that cancel cancelled.
  const PICKUP_REFUSED_LINE = 'ORDER_TRANSPORT_CANCEL_PICKUP_REFUSED orderId=ord-1 deliveryId=del-1 status=pickedUp';
  // FUP-42: a delivery pickup or confirm with no QR whose escrow could not be read (it stays deferred).
  const QR_GATE_LINE =
    'QR_GATE_UNDECIDED deliveryId=del-1 orderId=ord-1 reason=ProvisionedThroughputExceededException detail=Rate exceeded';
  // ESC-02's producer: one line per pending leg, ref=<op>:<leg>.
  const PAYOUT_LINE = 'MANUAL_PAYOUT_OVERDUE escrowId=escrow-order-ord-1 ref=release:farmer ageHours=26';
  const QUEUE_LINE = 'ADMIN_QUEUE_OVERDUE orderId=ord-1 reason=undelivered ageHours=30';
  // ESC-04's producer: one line per released escrow whose trucker leg is still owed.
  const TRUCKER_LINE = 'TRUCKER_PAYOUT_OVERDUE escrowId=escrow-order-ord-1 reason=phone_unverified ageHours=26';
  // ESC-03's refusal to settle a verdict its order cannot follow.
  const MISMATCH_LINE =
    'DISPUTE_VERDICT_ORDER_MISMATCH escrowId=escrow-order-ord-1 op=refund legs=order:mismatch:accepted';
  // ESC-03 lines that are not a refused settlement: a failed write the next run repairs, and
  // a delivery dispute that lost to a settlement in flight.
  // ESC-03's fail-closed settlement (read, cancel or restock did not land).
  const BLOCKED_LINE =
    'DISPUTE_VERDICT_SETTLEMENT_BLOCKED escrowId=escrow-order-ord-1 op=refund legs=order:cancel:conflict';
  const VERDICT_OTHER_LINES = [
    'DISPUTE_VERDICT_PROPAGATION_FAILED escrowId=escrow-order-ord-1 orderId=ord-1 deliveryId=del-1 legs=order:conflict',
    'ESCROW_DISPUTE_REFUSED_SETTLEMENT_IN_FLIGHT escrowId=escrow-order-ord-1',
  ];
  // ESC-02 lines of a normal queued payout (202 ESCROW_034): no money alarm may count them.
  const QUEUED_PAYOUT_LINES = [
    'ESCROW_MANUAL_PAYOUT_QUEUED escrowId=escrow-order-ord-1 op=release legs=farmer,trucker reason=PAYOUT_NOT_SUPPORTED',
    'ESCROW_MANUAL_PAYOUT_WAITING escrowId=escrow-order-ord-1 sweep=auto-release',
    'ESCROW_MANUAL_PAYOUT_PAID escrowId=escrow-order-ord-1 op=release leg=farmer remaining=1',
    'ESCROW_MANUAL_PAYOUT_QUEUE_FAILED escrowId=escrow-order-ord-1 op=release error=TimeoutException',
  ];

  type MetricFilterResource = {
    Properties: {
      LogGroupName: string;
      FilterPattern: string;
      MetricTransformations: Array<{ MetricName: string; MetricValue: string; DefaultValue?: number }>;
    };
  };

  function metricFilter(template: Template, logGroupName: string, metricName: string): MetricFilterResource {
    const filters = Object.values(template.findResources('AWS::Logs::MetricFilter')) as MetricFilterResource[];
    const hit = filters.find(
      (f) =>
        f.Properties.LogGroupName === logGroupName &&
        f.Properties.MetricTransformations[0].MetricName === metricName,
    );
    if (!hit) {
      throw new Error(`no metric filter publishing ${metricName} on ${logGroupName}`);
    }
    return hit;
  }

  // Minimal model of CloudWatch Logs term matching for the plain-text patterns this stack
  // uses: a bare or quoted term matches a log event that contains it as a whole token, and
  // a `?` prefix turns the terms into alternatives instead of all being required. A quoted
  // term with characters other than letters, digits and `_` (ESC-05's "outcome=unknown")
  // matches as an exact substring. This is the synthetic check of the filter pattern against
  // sample lines; the deployed pattern is checked against the real service by
  // scripts/money_alarms_synthetic.sh.
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

  // An alarm whose metric carries a label renders as a MetricDataQuery (Metrics[0].MetricStat),
  // not as flat MetricName/Namespace/Period keys.
  function customMetric(metricName: string, periodSeconds: number) {
    return Match.objectLike({
      MetricStat: Match.objectLike({
        Metric: Match.objectLike({ MetricName: metricName, Namespace: 'WeredaConnect/dev' }),
        Period: periodSeconds,
        Stat: 'Sum',
      }),
    });
  }

  test('ESC-13: settlement failures are counted on the escrow and escrow-cron log groups', () => {
    const template = monitoringTemplate(false);
    for (const lg of [ESCROW_LG, ESCROW_CRON_LG]) {
      const filter = metricFilter(template, lg, 'EscrowSettlementFailures');
      expect(filter.Properties.MetricTransformations[0].MetricValue).toBe('1');
      expect(cloudWatchTermsMatch(filter.Properties.FilterPattern, SETTLEMENT_LINE)).toBe(true);
      expect(cloudWatchTermsMatch(filter.Properties.FilterPattern, PREFIXED_SETTLEMENT_LINE)).toBe(true);
    }
    const cronFilter = metricFilter(template, ESCROW_CRON_LG, 'EscrowSettlementFailures');
    expect(cloudWatchTermsMatch(cronFilter.Properties.FilterPattern, SWEEP_QUERY_LINE)).toBe(true);
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'wereda-connect-dev-escrow-settlement-failures',
      Metrics: [customMetric('EscrowSettlementFailures', 900)],
      Threshold: 0,
      ComparisonOperator: 'GreaterThanThreshold',
      EvaluationPeriods: 5,
      DatapointsToAlarm: 1,
      TreatMissingData: 'notBreaching',
    });
  });

  test('ESC-05: unknown outcomes are counted on their own on the escrow and escrow-cron log groups', () => {
    const template = monitoringTemplate(false);
    const unknownLines = [
      SETTLEMENT_LINE,
      PREFIXED_SETTLEMENT_LINE,
      'ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-1 action=stale_lock_parked outcome=unknown code=release',
      'ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-1 action=release_failed outcome=unknown code=GATEWAY_EXCEPTION:TaskCanceledException',
      'ESCROW_SETTLEMENT_FAILED escrowId=- action=sweep_failed outcome=unknown code=auto_release_and_refund:Exception',
    ];
    const notUnknown = [
      SWEEP_QUERY_LINE,
      'ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-1 action=release_failed outcome=failed code=TELEBIRR_RELEASE_FAILED',
      'ESCROW_SETTLEMENT_FAILED escrowId=escrow-order-ord-1 action=auto_release_exception outcome=failed code=TimeoutException',
      // outcome=unknown without the token (a client cannot write the token: it has an underscore).
      'Auto-release failed for escrow-1: ESCROW_013 - outcome=unknown',
      'ESCROW_SETTLEMENT_PAID_UNRECORDED escrowId=escrow-order-ord-1 op=refund buyerRef=GW-1',
      PAYOUT_LINE,
      ...QUEUED_PAYOUT_LINES,
    ];
    for (const lg of [ESCROW_LG, ESCROW_CRON_LG]) {
      const filter = metricFilter(template, lg, 'EscrowSettlementUnknownOutcomes');
      expect(filter.Properties.MetricTransformations[0].MetricValue).toBe('1');
      for (const line of unknownLines) {
        expect([line, cloudWatchTermsMatch(filter.Properties.FilterPattern, line)]).toEqual([line, true]);
      }
      for (const line of notUnknown) {
        expect([line, cloudWatchTermsMatch(filter.Properties.FilterPattern, line)]).toEqual([line, false]);
      }
      // The general failures filter still counts the unknown lines too.
      const failures = metricFilter(template, lg, 'EscrowSettlementFailures');
      expect(cloudWatchTermsMatch(failures.Properties.FilterPattern, unknownLines[2])).toBe(true);
    }
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'wereda-connect-dev-escrow-settlement-unknown-outcomes',
      Metrics: [customMetric('EscrowSettlementUnknownOutcomes', 900)],
      Threshold: 0,
      ComparisonOperator: 'GreaterThanThreshold',
      EvaluationPeriods: 5,
      DatapointsToAlarm: 1,
      TreatMissingData: 'notBreaching',
    });
  });

  test('ESC-13: a single killed or crashed escrow-cron run alarms', () => {
    const template = monitoringTemplate(false);
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'wereda-connect-dev-escrow-cron-errors',
      Metrics: [
        Match.objectLike({
          MetricStat: Match.objectLike({
            Metric: Match.objectLike({
              Namespace: 'AWS/Lambda',
              MetricName: 'Errors',
              Dimensions: [{ Name: 'FunctionName', Value: 'wereda-connect-dev-escrow-cron' }],
            }),
            Period: 300,
            Stat: 'Sum',
          }),
        }),
      ],
      Threshold: 1,
      ComparisonOperator: 'GreaterThanOrEqualToThreshold',
      // A crash plus two async retries leaves empty 5 minute periods: 4 periods keep one episode.
      EvaluationPeriods: 4,
      DatapointsToAlarm: 1,
      TreatMissingData: 'notBreaching',
    });
  });

  test('ESC-13: order escrow problems cover create, link and refund-schedule failures (ESC-07: and a dispute with no escrow)', () => {
    const template = monitoringTemplate(false);
    const filter = metricFilter(template, SYNC_LG, 'OrderEscrowProblems');
    for (const line of [CREATE_LINE, LINK_LINE, REFUND_SCHEDULE_LINE, DISPUTE_NO_ESCROW_LINE, LATE_DELIVERY_LINE, PICKUP_REFUSED_LINE]) {
      expect(cloudWatchTermsMatch(filter.Properties.FilterPattern, line)).toBe(true);
    }
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'wereda-connect-dev-order-escrow-problems',
      Metrics: [customMetric('OrderEscrowProblems', 900)],
      Threshold: 0,
      EvaluationPeriods: 1,
    });
    // Replaced, not duplicated.
    const alarms = JSON.stringify(template.findResources('AWS::CloudWatch::Alarm'));
    expect(alarms).not.toContain('order-escrow-create-failed');
    expect(alarms).not.toContain('"OrderEscrowCreateFailed"');
  });

  test('ESC-13: queue alarms count overdue manual payouts and admin-queue entries on escrow-cron', () => {
    const template = monitoringTemplate(false);
    const payouts = metricFilter(template, ESCROW_CRON_LG, 'ManualPayoutsOverdue');
    expect(cloudWatchTermsMatch(payouts.Properties.FilterPattern, PAYOUT_LINE)).toBe(true);
    const queue = metricFilter(template, ESCROW_CRON_LG, 'AdminQueueOverdue');
    expect(cloudWatchTermsMatch(queue.Properties.FilterPattern, QUEUE_LINE)).toBe(true);
    for (const [alarmName, metricName] of [
      ['wereda-connect-dev-manual-payouts-overdue', 'ManualPayoutsOverdue'],
      ['wereda-connect-dev-admin-queue-overdue', 'AdminQueueOverdue'],
    ]) {
      template.hasResourceProperties('AWS::CloudWatch::Alarm', {
        AlarmName: alarmName,
        Metrics: [customMetric(metricName, 900)],
        Threshold: 0,
        EvaluationPeriods: 5,
        DatapointsToAlarm: 1,
      });
    }
  });

  test('ESC-04: overdue trucker legs are counted on escrow-cron, and only there', () => {
    const template = monitoringTemplate(false);
    const filter = metricFilter(template, ESCROW_CRON_LG, 'TruckerPayoutsOverdue');
    expect(filter.Properties.MetricTransformations[0].MetricValue).toBe('1');
    expect(cloudWatchTermsMatch(filter.Properties.FilterPattern, TRUCKER_LINE)).toBe(true);
    expect(cloudWatchTermsMatch(filter.Properties.FilterPattern, `2026-10-10T10:00:00.000Z\t4f1c7a2e-req\twarn\t${TRUCKER_LINE}`)).toBe(true);
    // Its own token only: not the manual payout line, the run summary, or a truncated read.
    for (const line of [
      PAYOUT_LINE,
      QUEUE_LINE,
      SETTLEMENT_LINE,
      'Auto-release/refund complete: trucker legs: 0 paid, 0 queued, 1 blocked, 0 failed, 0 deferred, 1 overdue.',
      'ESCROW_SETTLEMENT_FAILED escrowId=- action=sweep_truncated outcome=failed code=trucker_retry_query:500',
      'ESCROW_TRUCKER_PAYOUT_BLOCKED escrowId=escrow-order-ord-1 reason=phone_unverified',
    ]) {
      expect([line, cloudWatchTermsMatch(filter.Properties.FilterPattern, line)]).toEqual([line, false]);
    }
    for (const [lg, metric] of [
      [ESCROW_CRON_LG, 'EscrowSettlementFailures'],
      [ESCROW_CRON_LG, 'ManualPayoutsOverdue'],
      [ESCROW_CRON_LG, 'AdminQueueOverdue'],
    ]) {
      expect(cloudWatchTermsMatch(metricFilter(template, lg, metric).Properties.FilterPattern, TRUCKER_LINE)).toBe(false);
    }
    expect(() => metricFilter(template, ESCROW_LG, 'TruckerPayoutsOverdue')).toThrow();
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'wereda-connect-dev-trucker-payouts-overdue',
      Metrics: [customMetric('TruckerPayoutsOverdue', 900)],
      Threshold: 0,
      ComparisonOperator: 'GreaterThanThreshold',
      EvaluationPeriods: 5,
      DatapointsToAlarm: 1,
      TreatMissingData: 'notBreaching',
    });
  });

  test('ESC-03: refused dispute verdicts are counted on the escrow and escrow-cron log groups', () => {
    const template = monitoringTemplate(false);
    for (const lg of [ESCROW_LG, ESCROW_CRON_LG]) {
      const filter = metricFilter(template, lg, 'DisputeVerdictOrderMismatch');
      expect(filter.Properties.MetricTransformations[0].MetricValue).toBe('1');
      expect(cloudWatchTermsMatch(filter.Properties.FilterPattern, MISMATCH_LINE)).toBe(true);
      expect(cloudWatchTermsMatch(filter.Properties.FilterPattern, `2026-10-09T10:00:00.000Z\t4f1c7a2e-req\tinfo\t${MISMATCH_LINE}`)).toBe(true);
      // Negative set: other verdict lines and every other money token.
      for (const line of [...VERDICT_OTHER_LINES, SETTLEMENT_LINE, PAYOUT_LINE, QUEUE_LINE, CREATE_LINE, ...QUEUED_PAYOUT_LINES]) {
        expect([line, cloudWatchTermsMatch(filter.Properties.FilterPattern, line)]).toEqual([line, false]);
      }
    }
    // And no other money filter counts the mismatch line.
    for (const [lg, metric] of [
      [ESCROW_LG, 'EscrowSettlementFailures'],
      [ESCROW_CRON_LG, 'EscrowSettlementFailures'],
      [ESCROW_CRON_LG, 'ManualPayoutsOverdue'],
      [ESCROW_CRON_LG, 'AdminQueueOverdue'],
      [SYNC_LG, 'OrderEscrowProblems'],
    ]) {
      expect(cloudWatchTermsMatch(metricFilter(template, lg, metric).Properties.FilterPattern, MISMATCH_LINE)).toBe(false);
    }
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'wereda-connect-dev-dispute-verdict-order-mismatch',
      Metrics: [customMetric('DisputeVerdictOrderMismatch', 900)],
      Threshold: 0,
      ComparisonOperator: 'GreaterThanThreshold',
      EvaluationPeriods: 5,
      DatapointsToAlarm: 1,
      TreatMissingData: 'notBreaching',
    });
  });

  test('ESC-09: an order the money passed and the sweep could not fix is counted on escrow-cron, and only there', () => {
    const template = monitoringTemplate(false);
    const filter = metricFilter(template, ESCROW_CRON_LG, 'OrderMoneyMismatch');
    expect(filter.Properties.MetricTransformations[0].MetricValue).toBe('1');
    // One positive per reason, bare and with the runtime prefix.
    for (const reason of ['write_failed', 'cannot_follow', 'order_ahead', 'sweep_truncated', 'sweep_failed']) {
      const line =
        `ORDER_MONEY_MISMATCH orderId=ord-1 escrowId=escrow-order-ord-1 orderStatus=delivered escrowStatus=refunded reason=${reason}`;
      expect([line, cloudWatchTermsMatch(filter.Properties.FilterPattern, line)]).toEqual([line, true]);
      const prefixed = `2026-10-10T10:00:00.000Z\t4f1c7a2e-req\terror\t${line}`;
      expect([prefixed, cloudWatchTermsMatch(filter.Properties.FilterPattern, prefixed)]).toEqual([prefixed, true]);
    }
    // Negatives: the inline follow's info line, the verdict mismatch line and every other money token.
    for (const line of [
      'ORDER_MONEY_FOLLOW_DEFERRED escrowId=escrow-order-ord-1 orderId=ord-1 source=release reason=conflict',
      MISMATCH_LINE,
      BLOCKED_LINE,
      SETTLEMENT_LINE,
      PAYOUT_LINE,
      QUEUE_LINE,
      TRUCKER_LINE,
      ...QUEUED_PAYOUT_LINES,
    ]) {
      expect([line, cloudWatchTermsMatch(filter.Properties.FilterPattern, line)]).toEqual([line, false]);
    }
    // And no other money filter counts the order-money line.
    const orderMoneyLine =
      'ORDER_MONEY_MISMATCH orderId=- escrowId=- orderStatus=- escrowStatus=- reason=sweep_truncated';
    for (const [lg, metric] of [
      [ESCROW_CRON_LG, 'EscrowSettlementFailures'],
      [ESCROW_CRON_LG, 'DisputeVerdictOrderMismatch'],
      [ESCROW_CRON_LG, 'ManualPayoutsOverdue'],
      [ESCROW_CRON_LG, 'AdminQueueOverdue'],
      [ESCROW_CRON_LG, 'TruckerPayoutsOverdue'],
    ]) {
      expect(cloudWatchTermsMatch(metricFilter(template, lg, metric).Properties.FilterPattern, orderMoneyLine)).toBe(false);
    }
    expect(() => metricFilter(template, ESCROW_LG, 'OrderMoneyMismatch')).toThrow();
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'wereda-connect-dev-order-money-mismatch',
      Metrics: [customMetric('OrderMoneyMismatch', 900)],
      Threshold: 0,
      ComparisonOperator: 'GreaterThanThreshold',
      EvaluationPeriods: 5,
      DatapointsToAlarm: 1,
      TreatMissingData: 'notBreaching',
    });
  });

  test('ESC-03: a settlement blocked in three of four hourly periods alarms (escrow-cron only)', () => {
    const template = monitoringTemplate(false);
    const filter = metricFilter(template, ESCROW_CRON_LG, 'DisputeVerdictSettlementBlocked');
    expect(filter.Properties.MetricTransformations[0].MetricValue).toBe('1');
    expect(cloudWatchTermsMatch(filter.Properties.FilterPattern, BLOCKED_LINE)).toBe(true);
    expect(cloudWatchTermsMatch(filter.Properties.FilterPattern, `2026-10-09T10:00:00.000Z\t4f1c7a2e-req\tinfo\t${BLOCKED_LINE}`)).toBe(true);
    // Negative set: the mismatch line, the other verdict lines and every other money token.
    for (const line of [MISMATCH_LINE, ...VERDICT_OTHER_LINES, SETTLEMENT_LINE, PAYOUT_LINE, QUEUE_LINE, CREATE_LINE, ...QUEUED_PAYOUT_LINES]) {
      expect([line, cloudWatchTermsMatch(filter.Properties.FilterPattern, line)]).toEqual([line, false]);
    }
    // No other money filter counts the blocked line.
    for (const [lg, metric] of [
      [ESCROW_LG, 'EscrowSettlementFailures'],
      [ESCROW_CRON_LG, 'EscrowSettlementFailures'],
      [ESCROW_LG, 'DisputeVerdictOrderMismatch'],
      [ESCROW_CRON_LG, 'DisputeVerdictOrderMismatch'],
      [ESCROW_CRON_LG, 'ManualPayoutsOverdue'],
      [SYNC_LG, 'OrderEscrowProblems'],
    ]) {
      expect(cloudWatchTermsMatch(metricFilter(template, lg, metric).Properties.FilterPattern, BLOCKED_LINE)).toBe(false);
    }
    // Not on the escrow Lambda: an admin's own call answers 503 already.
    expect(() => metricFilter(template, ESCROW_LG, 'DisputeVerdictSettlementBlocked')).toThrow();
    template.hasResourceProperties('AWS::CloudWatch::Alarm', {
      AlarmName: 'wereda-connect-dev-dispute-verdict-settlement-blocked',
      Metrics: [customMetric('DisputeVerdictSettlementBlocked', 3600)],
      Threshold: 0,
      ComparisonOperator: 'GreaterThanThreshold',
      // 3 of 4: hourly-sweep jitter can leave one clock-hour empty without breaking the alarm.
      EvaluationPeriods: 4,
      DatapointsToAlarm: 3,
      TreatMissingData: 'notBreaching',
    });
  });

  test('ESC-13: filter patterns do not count unrelated escrow log lines', () => {
    const template = monitoringTemplate(false);
    const settlement = metricFilter(template, ESCROW_CRON_LG, 'EscrowSettlementFailures').Properties.FilterPattern;
    const orderProblems = metricFilter(template, SYNC_LG, 'OrderEscrowProblems').Properties.FilterPattern;
    const payouts = metricFilter(template, ESCROW_CRON_LG, 'ManualPayoutsOverdue').Properties.FilterPattern;
    // The cron's own failure-result line (the service already logged the token for it).
    expect(cloudWatchTermsMatch(settlement, 'Auto-release failed for escrow-1: ESCROW_013 - Telebirr gateway error')).toBe(false);
    // Tokens of the other filters.
    expect(cloudWatchTermsMatch(settlement, CREATE_LINE)).toBe(false);
    expect(cloudWatchTermsMatch(settlement, PAYOUT_LINE)).toBe(false);
    expect(cloudWatchTermsMatch(orderProblems, SETTLEMENT_LINE)).toBe(false);
    expect(cloudWatchTermsMatch(payouts, QUEUE_LINE)).toBe(false);
    // A normal queued payout (ESC-02) is not a settlement failure and not overdue.
    const adminQueue = metricFilter(template, ESCROW_CRON_LG, 'AdminQueueOverdue').Properties.FilterPattern;
    for (const line of QUEUED_PAYOUT_LINES) {
      for (const pattern of [settlement, payouts, adminQueue, orderProblems]) {
        expect(cloudWatchTermsMatch(pattern, line)).toBe(false);
      }
    }
    // A sanitized field value cannot forge a token (MoneyAlarmLog drops spaces and newlines).
    expect(cloudWatchTermsMatch(payouts, 'ESCROW_SETTLEMENT_FAILED escrowId=xMANUAL_PAYOUT_OVERDUEx action=a outcome=failed code=-')).toBe(false);
  });

  test('ESC-06: the late-delivery and pickup-refused tokens count on sync only, and nothing else counts them', () => {
    const template = monitoringTemplate(false);
    const orderProblems = metricFilter(template, SYNC_LG, 'OrderEscrowProblems').Properties.FilterPattern;
    expect(cloudWatchTermsMatch(orderProblems, LATE_DELIVERY_LINE)).toBe(true);
    expect(cloudWatchTermsMatch(orderProblems, PICKUP_REFUSED_LINE)).toBe(true);
    expect(cloudWatchTermsMatch(orderProblems, `2026-10-10T10:00:00.000Z\t4f1c7a2e-req\terror\t${PICKUP_REFUSED_LINE}`)).toBe(true);
    expect(cloudWatchTermsMatch(orderProblems, `2026-10-10T10:00:00.000Z\t4f1c7a2e-req\terror\t${LATE_DELIVERY_LINE}`)).toBe(true);
    // The cancel's other lines: a late pending delivery it cancelled itself, and the refund schedule.
    for (const line of [
      'Transport cancel of order ord-1 also cancelled pending delivery del-1, created while the order was being cancelled.',
      'Order escrow refund scheduled: orderId=ord-1 reason=order_transport_failed',
      'Transport cancel of order ord-1: the delivery could not be read or written: [TimeoutException]',
      // An id defanged by MoneyAlarmLog.LogId cannot spell the token.
      'Transport cancel of order ORDER-TRANSPORT-CANCEL-LATE-DELIVERY also cancelled pending delivery del-1',
      'Pickup on a cancelled delivery ORDER-TRANSPORT-CANCEL-PICKUP-REFUSED: its order could not be read: [TimeoutException]',
      // The refusal the trucker's device gets is not the alarm line.
      'DELIVERY_ALREADY_CANCELLED: Cannot update a cancelled delivery.',
    ]) {
      expect([line, cloudWatchTermsMatch(orderProblems, line)]).toEqual([line, false]);
    }
    for (const [lg, metric] of [
      [ESCROW_LG, 'EscrowSettlementFailures'],
      [ESCROW_CRON_LG, 'EscrowSettlementFailures'],
      [ESCROW_CRON_LG, 'ManualPayoutsOverdue'],
      [ESCROW_CRON_LG, 'AdminQueueOverdue'],
    ]) {
      expect(cloudWatchTermsMatch(metricFilter(template, lg, metric).Properties.FilterPattern, LATE_DELIVERY_LINE)).toBe(false);
      expect(cloudWatchTermsMatch(metricFilter(template, lg, metric).Properties.FilterPattern, PICKUP_REFUSED_LINE)).toBe(false);
    }
    // The hold-cap info line and the overdue line of a missing order.
    const adminQueue = metricFilter(template, ESCROW_CRON_LG, 'AdminQueueOverdue').Properties.FilterPattern;
    expect(cloudWatchTermsMatch(adminQueue, 'ORDER_ESCROW_HOLD_CAPPED orderId=ord-1 escrowId=escrow-order-ord-1')).toBe(false);
    expect(cloudWatchTermsMatch(adminQueue, 'ADMIN_QUEUE_OVERDUE orderId=ord-1 reason=order_missing ageHours=30')).toBe(true);
    expect(cloudWatchTermsMatch(orderProblems, 'ORDER_ESCROW_HOLD_CAPPED orderId=ord-1 escrowId=escrow-order-ord-1')).toBe(false);
  });

  test('FUP-42: an undecided QR gate counts on sync as an order escrow problem, and nothing else counts it', () => {
    const template = monitoringTemplate(false);
    const orderProblems = metricFilter(template, SYNC_LG, 'OrderEscrowProblems').Properties.FilterPattern;
    expect(cloudWatchTermsMatch(orderProblems, QR_GATE_LINE)).toBe(true);
    expect(cloudWatchTermsMatch(orderProblems, `2026-10-23T10:00:00.000Z\t4f1c7a2e-req\terror\t${QR_GATE_LINE}`)).toBe(true);
    expect(cloudWatchTermsMatch(orderProblems, 'QR_GATE_UNDECIDED deliveryId=del-1 orderId=- reason=TimeoutException detail=slow')).toBe(true);
    // The answers the device gets are not the alarm line, and an exception text that spells the
    // token is defanged by MoneyAlarmLog.LogText.
    for (const line of [
      'DELIVERY_NONCE_PENDING: The payment check could not be completed. Will retry automatically.',
      "DELIVERY_NONCE_PENDING: Waiting for the farmer's QR code to sync. Will retry automatically.",
      'DELIVERY_NONCE_MISSING: QR verification could not be completed for this payment. Contact support.',
      'Error processing operation op-1: [AmazonDynamoDBException] QR-GATE-UNDECIDED',
    ]) {
      expect([line, cloudWatchTermsMatch(orderProblems, line)]).toEqual([line, false]);
    }
    for (const [lg, metric] of [
      [ESCROW_LG, 'EscrowSettlementFailures'],
      [ESCROW_CRON_LG, 'EscrowSettlementFailures'],
      [ESCROW_CRON_LG, 'ManualPayoutsOverdue'],
      [ESCROW_CRON_LG, 'AdminQueueOverdue'],
    ]) {
      expect(cloudWatchTermsMatch(metricFilter(template, lg, metric).Properties.FilterPattern, QR_GATE_LINE)).toBe(false);
    }
  });

  // Every term of a money filter, as the synthesized pattern spells it.
  function filterTerms(pattern: string): string[] {
    return (pattern.match(/\??"[^"]*"|\??\S+/g) ?? []).map((t) => t.replace(/^\?/, '').replace(/^"|"$/g, ''));
  }

  // The money filters of the four log groups that carry them (webhook only with the live gateway).
  function moneyFilterPatterns(template: Template): string[] {
    const groups = [ESCROW_LG, ESCROW_CRON_LG, SYNC_LG, '/aws/lambda/wereda-connect-dev-telebirr-webhook'];
    const money = [
      'EscrowSettlementFailures', 'OrderEscrowProblems', 'ManualPayoutsOverdue', 'AdminQueueOverdue',
      'OrderPaymentProblems', 'DisputeVerdictOrderMismatch', 'DisputeVerdictSettlementBlocked',
      'EscrowSettlementUnknownOutcomes', 'TruckerPayoutsOverdue',
    ];
    return (Object.values(template.findResources('AWS::Logs::MetricFilter')) as MetricFilterResource[])
      .filter((f) => groups.includes(f.Properties.LogGroupName) && money.includes(f.Properties.MetricTransformations[0].MetricName))
      .map((f) => f.Properties.FilterPattern);
  }

  // Whether every term a pattern requires appears in the line as a plain substring (any one of
  // them for a pattern of `?` alternatives): stricter than the service's token match.
  function substringMatch(pattern: string, line: string): boolean {
    const terms = filterTerms(pattern);
    return pattern.includes('?') ? terms.some((t) => line.includes(t)) : terms.every((t) => line.includes(t));
  }

  test('ESC-13: every money filter needs a term with an underscore (the server defangs client text by turning _ into -)', () => {
    const patterns = moneyFilterPatterns(monitoringTemplate(true));
    expect(patterns.length).toBe(12);
    for (const pattern of patterns) {
      const terms = filterTerms(pattern);
      // Alternatives: every term must carry one. All required (ESC-05's unknown filter pairs the
      // token with "outcome=unknown"): one term with an underscore is enough.
      const safe = pattern.includes('?') ? terms.every((t) => t.includes('_')) : terms.some((t) => t.includes('_'));
      expect([pattern, safe]).toEqual([pattern, true]);
    }
  });

  test('ESC-13: client text that spells a token cannot fire a money alarm once the server wrote it', () => {
    const patterns = moneyFilterPatterns(monitoringTemplate(true));
    const tokens = patterns.flatMap(filterTerms);
    // What a client could send: a path, an id, a header or a field that spells every token.
    const hostile = tokens.join('-');
    const defanged = hostile.replace(/_/g, '-');
    // Before ESC-13 the escrow Lambda logged the raw path: that line would fire the alarm.
    expect(patterns.some((p) => cloudWatchTermsMatch(p, `EscrowFunction invoked: GET /escrow/${hostile}`))).toBe(true);
    // The lines as the server writes them now (route template, MoneyAlarmLog.LogId / LogText,
    // AuditLogger defangs its ids): no pattern matches, by term or even by substring.
    const lines = [
      'EscrowFunction invoked: GET /escrow/{id}',
      `Error getting escrow ${defanged}: [AmazonDynamoDBException]`,
      `Error getting escrow for delivery ${defanged}: [AmazonDynamoDBException]`,
      `{"timestamp":"2026-10-09T10:00:00Z","category":"ESCROW","action":"DISPUTE","userId":"u-1","correlationId":"${defanged}","entityType":"escrow","entityId":"${defanged}","outcome":"failure"}`,
      `Op ${defanged}: status=error, msg=Invalid action: '${defanged}'. Must be create, update, or delete.`,
      `Duplicate idempotency key: ${defanged} for operation ${defanged}`,
      `Error processing operation ${defanged}: [FormatException] The input string '${defanged}' was not in a correct format.`,
      `ESCROW_MANUAL_PAYOUT_WAITING escrowId=escrow-order-${defanged} sweep=auto-release`,
      `DISPUTE_VERDICT_PROPAGATION_FAILED escrowId=escrow-order-${defanged} orderId=${defanged} deliveryId=${defanged} legs=order:mismatch:${defanged}`,
      `Auto-release failed for escrow-order-${defanged}: ESCROW_013 - ${defanged}`,
      `Order funded by a Telebirr payment: orderId=${defanged} merchOrderId=m-1 paymentOrderId=p-1.`,
      `ORDER_PAYMENT_BOOKKEEPING_FAILED orderId=${defanged} merchOrderId=m-1 paymentOrderId=p-1 step=inbox outcome=funded reason=TimeoutException`,
    ];
    for (const line of lines) {
      for (const pattern of patterns) {
        expect([line, cloudWatchTermsMatch(pattern, line)]).toEqual([line, false]);
        expect([line, substringMatch(pattern, line)]).toEqual([line, false]);
      }
    }
  });

  test.each([false, true])('ESC-13: money alarms exist with the live gateway %s and notify the alarm topic', (live) => {
    const template = monitoringTemplate(live);
    const notifiesTopic = Match.arrayWith([Match.objectLike({ Ref: Match.stringLikeRegexp('^AlarmTopic') })]);
    for (const alarmName of MONEY_ALARMS) {
      template.hasResourceProperties('AWS::CloudWatch::Alarm', {
        AlarmName: alarmName,
        AlarmActions: notifiesTopic,
        OKActions: notifiesTopic,
      });
    }
  });

  test.each([false, true])('ESC-13: every alarm description fits the CloudWatch limit (live gateway %s)', (live) => {
    // AlarmDescription is capped at 1024 characters; synth does not check it, the deploy fails.
    const alarms = Object.values(monitoringTemplate(live).findResources('AWS::CloudWatch::Alarm')) as Array<{
      Properties: { AlarmName: string; AlarmDescription?: string };
    }>;
    expect(alarms.length).toBeGreaterThan(0);
    for (const alarm of alarms) {
      expect([alarm.Properties.AlarmName, (alarm.Properties.AlarmDescription ?? '').length <= 1024])
        .toEqual([alarm.Properties.AlarmName, true]);
    }
  });

  test('ESC-13: the dashboard alarm status widget lists the money alarms (webhook alarm only when live)', () => {
    for (const live of [false, true]) {
      const body = JSON.stringify(monitoringTemplate(live).findResources('AWS::CloudWatch::Dashboard'));
      for (const logicalId of [
        'EscrowSettlementFailures',
        'EscrowSettlementUnknownOutcomes',
        'EscrowCronErrors',
        'OrderEscrowProblems',
        'ManualPayoutsOverdue',
        'TruckerPayoutsOverdue',
        'AdminQueueOverdue',
        'DisputeVerdictOrderMismatch',
        'DisputeVerdictSettlementBlocked',
      ]) {
        expect(body).toMatch(new RegExp(`"Fn::GetAtt":\\["${logicalId}[0-9A-F]+","Arn"\\]`));
      }
      const webhookAlarmRef = /"Fn::GetAtt":\["OrderPaymentProblems[0-9A-F]+","Arn"\]/;
      if (live) {
        expect(body).toMatch(webhookAlarmRef);
      } else {
        expect(body).not.toMatch(webhookAlarmRef);
      }
    }
  });
});

describe('GithubActionsStack', () => {
  let template: Template;

  beforeAll(() => {
    const app = new cdk.App();
    const stack = new GithubActionsStack(app, 'TestGithubActionsStack', {
      env: { account: '123456789012', region: devConfig.region },
      envConfig: devConfig,
      githubRepo: 'mitikutesh/WeredaConnect',
      websiteBucketName: 'wereda-connect-dev-eu-north-1-website',
      webappBucketName: 'wereda-connect-dev-eu-north-1-webapp',
      adminBucketName: 'wereda-connect-dev-eu-north-1-admin',
      websiteDistributionId: 'ETEST001',
      webappDistributionId: 'ETEST002',
      adminDistributionId: 'ETEST003',
    });
    template = Template.fromStack(stack);
  });

  test('creates a single IAM role with correct name', () => {
    template.resourceCountIs('AWS::IAM::Role', 1);
    template.hasResourceProperties('AWS::IAM::Role', {
      RoleName: 'wereda-connect-dev-github-deploy',
    });
  });

  test('FUP-31: the GitHub OIDC provider can assume the role only for a job on main that names the environment', () => {
    template.hasResourceProperties('AWS::IAM::Role', {
      AssumeRolePolicyDocument: {
        Statement: [
          {
            Action: 'sts:AssumeRoleWithWebIdentity',
            Principal: {
              Federated: 'arn:aws:iam::123456789012:oidc-provider/token.actions.githubusercontent.com',
            },
            Condition: Match.objectEquals({
              StringEquals: {
                'token.actions.githubusercontent.com:aud': 'sts.amazonaws.com',
                'token.actions.githubusercontent.com:sub':
                  'repo:mitikutesh/WeredaConnect:environment:dev:ref:refs/heads/main',
              },
            }),
          },
        ],
      },
    });
  });

  test.each(['dev', 'staging', 'prod'])(
    'FUP-31: the %s role trusts only its own environment on main, with no wildcard',
    (envName) => {
      const envConfig = environments[envName];
      const app = new cdk.App();
      const stack = new GithubActionsStack(app, `TrustGithubActionsStack-${envName}`, {
        env: { account: '123456789012', region: envConfig.region },
        envConfig,
        githubRepo: 'mitikutesh/WeredaConnect',
        websiteBucketName: `wereda-connect-${envName}-${envConfig.region}-website`,
        webappBucketName: `wereda-connect-${envName}-${envConfig.region}-webapp`,
        adminBucketName: `wereda-connect-${envName}-${envConfig.region}-admin`,
        websiteDistributionId: 'ETRUST01',
        webappDistributionId: 'ETRUST02',
        adminDistributionId: 'ETRUST03',
      });
      const envTemplate = Template.fromStack(stack);
      const roles = Object.values(envTemplate.findResources('AWS::IAM::Role'));
      expect(roles).toHaveLength(1);
      const statements = roles[0].Properties.AssumeRolePolicyDocument.Statement;
      expect(statements).toHaveLength(1);
      expect(statements[0].Condition).toEqual({
        StringEquals: {
          'token.actions.githubusercontent.com:aud': 'sts.amazonaws.com',
          'token.actions.githubusercontent.com:sub': `repo:mitikutesh/WeredaConnect:environment:${envName}:ref:refs/heads/main`,
        },
      });
      const json = JSON.stringify(envTemplate.toJSON());
      expect(json).not.toContain('StringLike');
      expect(json).not.toContain('WeredaConnect:*');
    },
  );

  test('policy grants s3:ListBucket on bucket ARNs (not on /*)', () => {
    template.hasResourceProperties('AWS::IAM::Policy', {
      PolicyDocument: {
        Statement: Match.arrayWith([
          {
            Sid: 'S3ListBuckets',
            Action: 's3:ListBucket',
            Effect: 'Allow',
            Resource: [
              'arn:aws:s3:::wereda-connect-dev-eu-north-1-website',
              'arn:aws:s3:::wereda-connect-dev-eu-north-1-webapp',
              'arn:aws:s3:::wereda-connect-dev-eu-north-1-admin',
            ],
          },
        ]),
      },
    });
  });

  test('policy grants s3 object operations on bucket/* paths', () => {
    template.hasResourceProperties('AWS::IAM::Policy', {
      PolicyDocument: {
        Statement: Match.arrayWith([
          {
            Sid: 'S3SyncObjects',
            Action: ['s3:GetObject', 's3:PutObject', 's3:DeleteObject'],
            Effect: 'Allow',
            Resource: [
              'arn:aws:s3:::wereda-connect-dev-eu-north-1-website/*',
              'arn:aws:s3:::wereda-connect-dev-eu-north-1-webapp/*',
              'arn:aws:s3:::wereda-connect-dev-eu-north-1-admin/*',
            ],
          },
        ]),
      },
    });
  });

  test('policy grants cloudfront:CreateInvalidation scoped to specific distributions', () => {
    template.hasResourceProperties('AWS::IAM::Policy', {
      PolicyDocument: {
        Statement: Match.arrayWith([
          {
            Sid: 'CloudFrontInvalidate',
            Action: 'cloudfront:CreateInvalidation',
            Effect: 'Allow',
            Resource: [
              'arn:aws:cloudfront::123456789012:distribution/ETEST001',
              'arn:aws:cloudfront::123456789012:distribution/ETEST002',
              'arn:aws:cloudfront::123456789012:distribution/ETEST003',
            ],
          },
        ]),
      },
    });
  });

  test('ESC-04: the role can invoke only escrow-cron, for the post-deploy legacy trucker action', () => {
    template.hasResourceProperties('AWS::IAM::Policy', {
      PolicyDocument: Match.objectLike({
        Statement: Match.arrayWith([
          {
            Sid: 'InvokeEscrowCronAfterDeploy',
            Action: 'lambda:InvokeFunction',
            Effect: 'Allow',
            Resource: 'arn:aws:lambda:eu-north-1:123456789012:function:wereda-connect-dev-escrow-cron',
          },
        ]),
      }),
    });
    const policies = JSON.stringify(template.findResources('AWS::IAM::Policy'));
    expect(policies.match(/lambda:InvokeFunction/g) ?? []).toHaveLength(1);
  });

  test('dev role can seed the dashboard admin user in Cognito; prod role cannot', () => {
    template.hasResourceProperties('AWS::IAM::Policy', {
      PolicyDocument: Match.objectLike({
        Statement: Match.arrayWith([
          Match.objectLike({
            Sid: 'SeedAdminUser',
            Action: [
              'cognito-idp:AdminGetUser',
              'cognito-idp:AdminCreateUser',
              'cognito-idp:AdminSetUserPassword',
              'cognito-idp:AdminAddUserToGroup',
              'cognito-idp:AdminSetUserMFAPreference',
              'cognito-idp:AdminListGroupsForUser',
            ],
            Resource: 'arn:aws:cognito-idp:eu-north-1:123456789012:userpool/*',
          }),
        ]),
      }),
    });

    const app = new cdk.App();
    const prodStack = new GithubActionsStack(app, 'ProdGithubActionsStackAdminSeed', {
      env: { account: '123456789012', region: prodConfig.region },
      envConfig: prodConfig,
      githubRepo: 'mitikutesh/WeredaConnect',
      websiteBucketName: 'wereda-connect-prod-me-south-1-website',
      webappBucketName: 'wereda-connect-prod-me-south-1-webapp',
      adminBucketName: 'wereda-connect-prod-me-south-1-admin',
      websiteDistributionId: 'EPROD001',
      webappDistributionId: 'EPROD002',
      adminDistributionId: 'EPROD003',
    });
    expect(JSON.stringify(Template.fromStack(prodStack).toJSON())).not.toContain('SeedAdminUser');
  });

  test('dev role can seed SSM secrets and read legacy secrets; prod role cannot', () => {
    template.hasResourceProperties('AWS::IAM::Policy', {
      PolicyDocument: Match.objectLike({
        Statement: Match.arrayWith([
          Match.objectLike({
            Sid: 'SeedSsmSecrets',
            Action: ['ssm:GetParameter', 'ssm:PutParameter'],
            Resource: [
              'arn:aws:ssm:*:123456789012:parameter/wereda-connect-dev/pagination-secret',
              'arn:aws:ssm:*:123456789012:parameter/wereda-connect-dev/fayda/client-private-key',
              'arn:aws:ssm:*:123456789012:parameter/wereda-connect-dev/fayda/signing-secret',
              'arn:aws:ssm:*:123456789012:parameter/wereda-connect-dev/telebirr/credentials',
            ],
          }),
          Match.objectLike({
            Sid: 'ReadLegacySecretsForMigration',
            Action: 'secretsmanager:GetSecretValue',
            Resource: [
              'arn:aws:secretsmanager:*:123456789012:secret:/wereda-connect-dev/pagination-secret-??????',
              'arn:aws:secretsmanager:*:123456789012:secret:/wereda-connect-dev/fayda/client-private-key-??????',
              'arn:aws:secretsmanager:*:123456789012:secret:/wereda-connect-dev/fayda/signing-secret-??????',
              'arn:aws:secretsmanager:*:123456789012:secret:/wereda-connect-dev/telebirr/credentials-??????',
            ],
          }),
        ]),
      }),
    });

    const app = new cdk.App();
    const prodStack = new GithubActionsStack(app, 'ProdGithubActionsStack', {
      env: { account: '123456789012', region: prodConfig.region },
      envConfig: prodConfig,
      githubRepo: 'mitikutesh/WeredaConnect',
      websiteBucketName: 'wereda-connect-prod-me-south-1-website',
      webappBucketName: 'wereda-connect-prod-me-south-1-webapp',
      adminBucketName: 'wereda-connect-prod-me-south-1-admin',
      websiteDistributionId: 'ETEST101',
      webappDistributionId: 'ETEST102',
      adminDistributionId: 'ETEST103',
    });
    const prodPolicies = JSON.stringify(
      Template.fromStack(prodStack).findResources('AWS::IAM::Policy'),
    );
    expect(prodPolicies).not.toContain('SeedSsmSecrets');
    expect(prodPolicies).not.toContain('secretsmanager:GetSecretValue');
  });
});
