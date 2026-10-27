import * as fs from 'fs';
import * as path from 'path';
import * as cdk from 'aws-cdk-lib';
import { Template, Match, Annotations } from 'aws-cdk-lib/assertions';
import { DatabaseStack } from '../../lib/stacks/database-stack';
import { ApiStack } from '../../lib/stacks/api-stack';
import { environments, EnvironmentConfig } from '../../lib/config/environments';

const devConfig = environments['dev'];

function buildApi(
  overrides: Partial<EnvironmentConfig> = {},
  context?: Record<string, string>,
): { stack: ApiStack; template: Template } {
  const app = new cdk.App({ context });
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
    notificationsTable: dbStack.notificationsTable,
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

  test('auth routes use payload 1.0 so the handlers see the path, the caller IP and the claims', () => {
    // The auth handlers dispatch on request.Path and apply per-IP caps from the
    // v1.0 event shape; a v2.0 integration would misroute /auth/fayda-start and
    // /auth/resend-otp and blind the IP caps. Logout (SIM-60) reads the caller's
    // sub from authorizer.claims, which only the 1.0 event carries.
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
      ['POST /auth/logout', 'wereda-connect-dev-auth-logout'],
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

  test('every FUNCTION_HANDLER is routed by the server AOT bootstrap', () => {
    // A handler missing from Program.cs crashes the Lambda at init ("Unknown
    // handler"), which API Gateway reports as a bare 500. verify-otp shipped
    // that way and blocked every OTP sign-up.
    const program = fs.readFileSync(path.join(__dirname, '../../../server/src/Program.cs'), 'utf8');
    const handlers = Object.values(template.findResources('AWS::Lambda::Function'))
      .map((r) => (propsOf(r).Environment as { Variables?: Props } | undefined)?.Variables?.FUNCTION_HANDLER)
      .filter((h): h is string => typeof h === 'string');
    expect(handlers).toContain('VerifyOtpFunction');
    for (const handler of handlers) {
      expect(program).toContain(`h.Contains("${handler}")`);
    }
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

describe('ApiStack Fayda sandbox mode (dev)', () => {
  // The shared VeriFayda sandbox test client from environments.ts (public client_id).
  const sandboxClientId = 'aQ7xN2kLm9_Vp4RzT8cY-hW3sJ6FdE0uXbKqP1';
  const verifyEnv = (template: Template): Props => {
    const fn = Object.values(template.findResources('AWS::Lambda::Function'))
      .find((r) => String(propsOf(r).FunctionName) === 'wereda-connect-dev-auth-fayda-verify');
    return ((propsOf(fn!).Environment as { Variables?: Props } | undefined)?.Variables ?? {}) as Props;
  };

  test('sandbox: no mock provider, the real eSignet issuer, the shared test client and the relay redirect', () => {
    const { template } = buildApi({ faydaMode: 'sandbox' });

    template.resourceCountIs('AWS::Lambda::Url', 0);
    for (const resource of Object.values(template.findResources('AWS::Lambda::Function'))) {
      expect(String(propsOf(resource).FunctionName)).not.toContain('fayda-mock');
    }
    template.hasResourceProperties('AWS::SSM::Parameter', {
      Name: '/wereda-connect-dev/fayda/client-id',
      Value: sandboxClientId,
    });
    template.hasResourceProperties('AWS::SSM::Parameter', {
      Name: '/wereda-connect-dev/fayda/issuer-url',
      Value: 'https://esignet.ida.fayda.et',
    });
    const env = verifyEnv(template);
    expect(env.FAYDA_SANDBOX).toBe('true');
    expect(env.FAYDA_SANDBOX_REDIRECT_URI).toBe('http://localhost:3000/callback');
    expect(String(env.FAYDA_MOCK_WEB_REDIRECT_URIS)).toContain('http://localhost:8080/fayda-callback.html');
    expect(env).not.toHaveProperty('FAYDA_MOCK');
    // The RP private key stays the seeded parameter the operator fills with the test client's key.
    expect(env.FAYDA_CLIENT_PRIVATE_KEY_ARN).toBe('/wereda-connect-dev/fayda/client-private-key');
  });

  test('sandbox: the mode is switched per deploy with -c faydaMode', () => {
    const { template } = buildApi({}, { faydaMode: 'sandbox' });
    expect(verifyEnv(template).FAYDA_SANDBOX).toBe('true');

    const { template: mock } = buildApi({ faydaMode: 'sandbox' }, { faydaMode: 'mock' });
    expect(verifyEnv(mock).FAYDA_MOCK).toBe('true');
    expect(verifyEnv(mock)).not.toHaveProperty('FAYDA_SANDBOX');
  });

  test('sandbox: an unknown -c faydaMode is a synth error (the template itself keeps the config mode)', () => {
    const { stack, template } = buildApi({}, { faydaMode: 'staging-sandbox' });
    Annotations.fromStack(stack).hasError('*', Match.stringLikeRegexp('is not one of mock, sandbox, live'));
    expect(verifyEnv(template).FAYDA_MOCK).toBe('true');
  });

  test('sandbox without a configured test client id is a synth error', () => {
    const { stack } = buildApi({ faydaMode: 'sandbox', faydaSandboxClientId: undefined });
    Annotations.fromStack(stack).hasError('*', Match.stringLikeRegexp('needs faydaSandboxClientId'));
  });

  const noDevProvider = (template: Template): void => {
    for (const resource of Object.values(template.findResources('AWS::Lambda::Function'))) {
      expect(String(propsOf(resource).FunctionName)).not.toContain('fayda-mock');
      const env = (propsOf(resource).Environment as { Variables?: Props } | undefined)?.Variables ?? {};
      expect(env).not.toHaveProperty('FAYDA_SANDBOX');
      expect(env).not.toHaveProperty('FAYDA_SANDBOX_REDIRECT_URI');
      expect(env).not.toHaveProperty('FAYDA_MOCK');
    }
  };

  test('staging: -c faydaMode=sandbox (or mock) is refused and deploys no dev provider', () => {
    for (const mode of ['sandbox', 'mock']) {
      // The real staging config has no faydaMode; clear the dev base's 'mock' explicitly.
      const { stack, template } = buildApi({ ...environments['staging'], faydaMode: undefined }, { faydaMode: mode });
      Annotations.fromStack(stack).hasError('*', Match.stringLikeRegexp('context override is only allowed in dev'));
      noDevProvider(template);
    }
  });

  test('staging: faydaMode sandbox in the config is refused too', () => {
    const { stack, template } = buildApi({ ...environments['staging'], faydaMode: 'sandbox' });
    Annotations.fromStack(stack).hasError('*', Match.stringLikeRegexp("faydaMode 'sandbox' is not allowed"));
    noDevProvider(template);
  });

  test('dev: -c faydaMode=live without a configured faydaClientId is a synth error', () => {
    const { stack } = buildApi({}, { faydaMode: 'live' });
    Annotations.fromStack(stack).hasError('*', Match.stringLikeRegexp("faydaMode 'live' needs faydaClientId"));
  });

  test('sandbox in prod: refused, and no sandbox flag on any Lambda', () => {
    const { stack, template } = buildApi({ isProd: true, faydaMode: 'sandbox' });
    Annotations.fromStack(stack).hasError('*', Match.stringLikeRegexp("faydaMode 'sandbox' is not allowed"));
    for (const resource of Object.values(template.findResources('AWS::Lambda::Function'))) {
      const env = (propsOf(resource).Environment as { Variables?: Props } | undefined)?.Variables ?? {};
      expect(env).not.toHaveProperty('FAYDA_SANDBOX');
      expect(env).not.toHaveProperty('FAYDA_SANDBOX_REDIRECT_URI');
    }
  });
});
