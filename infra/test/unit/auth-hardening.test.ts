import * as cdk from 'aws-cdk-lib';
import { Template, Match, Annotations } from 'aws-cdk-lib/assertions';
import { DatabaseStack } from '../../lib/stacks/database-stack';
import { ApiStack } from '../../lib/stacks/api-stack';
import { environments, EnvironmentConfig } from '../../lib/config/environments';

const devConfig = environments['dev'];

function buildApi(overrides: Partial<EnvironmentConfig> = {}): { stack: ApiStack; template: Template } {
  const app = new cdk.App();
  const dbStack = new DatabaseStack(app, 'AhDb', {
    env: { account: '123456789012', region: devConfig.region },
    envConfig: devConfig,
  });
  const stack = new ApiStack(app, 'AhApi', {
    env: { account: '123456789012', region: devConfig.region },
    envConfig: { ...devConfig, ...overrides },
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
  return { stack, template: Template.fromStack(stack) };
}

type Props = Record<string, unknown>;
const propsOf = (resource: unknown): Props => (resource as { Properties: Props }).Properties;

describe('ApiStack auth hardening (dev)', () => {
  let template: Template;

  beforeAll(() => {
    template = buildApi().template;
  });

  test('public auth routes use payload 1.0 so the handlers see the path and the caller IP', () => {
    // The auth handlers dispatch on request.Path and apply per-IP caps from the
    // v1.0 event shape; a v2.0 integration would misroute /auth/fayda-start and
    // /auth/resend-otp and blind the IP caps.
    const lambdas = template.findResources('AWS::Lambda::Function');
    const integrations = template.findResources('AWS::ApiGatewayV2::Integration');
    const routes = template.findResources('AWS::ApiGatewayV2::Route');
    const logicalIdOf = (functionName: string) =>
      Object.entries(lambdas).find(([, r]) => propsOf(r).FunctionName === functionName)?.[0];
    const integrationFor = (routeKey: string) => {
      const route = Object.values(routes).find((r) => propsOf(r).RouteKey === routeKey);
      const target = JSON.stringify(route ? propsOf(route).Target : null);
      const id = Object.keys(integrations).find((candidate) => target.includes(candidate));
      return id ? propsOf(integrations[id]) : undefined;
    };
    const expectations: Array<[string, string]> = [
      ['POST /auth/register', 'wereda-connect-dev-auth-register'],
      ['POST /auth/login', 'wereda-connect-dev-auth-login'],
      ['POST /auth/refresh', 'wereda-connect-dev-auth-refresh'],
      ['POST /auth/fayda-verify', 'wereda-connect-dev-auth-fayda-verify'],
      ['POST /auth/fayda-start', 'wereda-connect-dev-auth-fayda-verify'],
      ['POST /auth/verify-otp', 'wereda-connect-dev-auth-verify-otp'],
      ['POST /auth/resend-otp', 'wereda-connect-dev-auth-verify-otp'],
    ];
    for (const [routeKey, functionName] of expectations) {
      const integration = integrationFor(routeKey);
      expect(integration).toBeDefined();
      expect(integration!.PayloadFormatVersion).toBe('1.0');
      expect(JSON.stringify(integration!.IntegrationUri)).toContain(logicalIdOf(functionName));
    }
  });

  test('escrow cron can read profiles for payout resolution', () => {
    const policies = template.findResources('AWS::IAM::Policy');
    const cronPolicy = Object.entries(policies).find(([id]) => id.startsWith('EscrowCronFunction'))?.[1];
    expect(cronPolicy).toBeDefined();
    expect(JSON.stringify(cronPolicy)).toContain('UsersTable');
  });

  test('SMS publish is allowed on * and denied on every SNS ARN', () => {
    const policies = JSON.stringify(template.findResources('AWS::IAM::Policy'));
    expect(policies).toContain('"Effect":"Deny"');
    expect(policies).toContain('arn:aws:sns:*:*:*');
  });

  test('mock Fayda Lambda reserves no concurrency in dev (account limit is 10 total)', () => {
    template.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: 'wereda-connect-dev-fayda-mock',
      ReservedConcurrentExecutions: Match.absent(),
    });
  });
});

describe('ApiStack auth hardening (prod-like)', () => {
  test('faydaMode mock in prod: no mock resources and a synth error', () => {
    const { stack, template } = buildApi({ isProd: true, faydaMode: 'mock' });

    template.resourceCountIs('AWS::Lambda::Url', 0);
    for (const resource of Object.values(template.findResources('AWS::Lambda::Function'))) {
      expect(String(propsOf(resource).FunctionName)).not.toContain('fayda-mock');
    }
    Annotations.fromStack(stack).hasError('*', Match.stringLikeRegexp("faydaMode 'mock' is not allowed"));
  });
});
