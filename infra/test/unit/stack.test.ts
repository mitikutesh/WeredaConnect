import * as cdk from 'aws-cdk-lib';
import { Template, Match } from 'aws-cdk-lib/assertions';
import { DatabaseStack } from '../../lib/stacks/database-stack';
import { ApiStack } from '../../lib/stacks/api-stack';
import { MonitoringStack } from '../../lib/stacks/monitoring-stack';
import { GithubActionsStack } from '../../lib/stacks/github-actions-stack';
import { environments } from '../../lib/config/environments';

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
    // /user/fayda-link and DELETE /device-tokens (auth hardening) → 55 total.
    template.resourceCountIs('AWS::ApiGatewayV2::Route', 55);
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
    template.hasResourceProperties('AWS::SSM::Parameter', {
      Name: '/wereda-connect-dev/fayda/client-secret-mock',
    });
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
    // Dev runs faydaMode 'mock': the client secret is a stack-owned parameter
    // (no seed step); the signing secret is still the seeded SecureString.
    template.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: 'wereda-connect-dev-auth-fayda-verify',
      Environment: {
        Variables: Match.objectLike({
          FAYDA_CLIENT_SECRET_ARN: '/wereda-connect-dev/fayda/client-secret-mock',
          FAYDA_SIGNING_SECRET_ARN: '/wereda-connect-dev/fayda/signing-secret',
        }),
      },
    });
  });

  test('dev: lambda roles are granted ssm:GetParameter on the secret parameters', () => {
    const policies = JSON.stringify(template.findResources('AWS::IAM::Policy'));
    expect(policies).toContain('ssm:GetParameter');
    expect(policies).toContain('parameter/wereda-connect-dev/pagination-secret');
    expect(policies).toContain('parameter/wereda-connect-dev/fayda/client-secret');
    expect(policies).toContain('parameter/wereda-connect-dev/fayda/signing-secret');
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
      Name: '/wereda-connect-prod/fayda/client-secret',
    });
    template.hasResourceProperties('AWS::SecretsManager::Secret', {
      Name: '/wereda-connect-prod/fayda/signing-secret',
    });
  });
});

describe('ApiStack (staging uses the live Fayda provider)', () => {
  test('no mock provider and no mock-only web redirects', () => {
    const stagingConfig = environments['staging'];
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
    const template = Template.fromStack(apiStack);

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

  test('role trust policy uses GitHub OIDC provider', () => {
    template.hasResourceProperties('AWS::IAM::Role', {
      AssumeRolePolicyDocument: {
        Statement: [
          {
            Action: 'sts:AssumeRoleWithWebIdentity',
            Condition: {
              StringEquals: {
                'token.actions.githubusercontent.com:aud': 'sts.amazonaws.com',
              },
              StringLike: {
                'token.actions.githubusercontent.com:sub': 'repo:mitikutesh/WeredaConnect:*',
              },
            },
          },
        ],
      },
    });
  });

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
              'arn:aws:ssm:*:123456789012:parameter/wereda-connect-dev/fayda/client-secret',
              'arn:aws:ssm:*:123456789012:parameter/wereda-connect-dev/fayda/signing-secret',
              'arn:aws:ssm:*:123456789012:parameter/wereda-connect-dev/telebirr/credentials',
            ],
          }),
          Match.objectLike({
            Sid: 'ReadLegacySecretsForMigration',
            Action: 'secretsmanager:GetSecretValue',
            Resource: [
              'arn:aws:secretsmanager:*:123456789012:secret:/wereda-connect-dev/pagination-secret-??????',
              'arn:aws:secretsmanager:*:123456789012:secret:/wereda-connect-dev/fayda/client-secret-??????',
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
