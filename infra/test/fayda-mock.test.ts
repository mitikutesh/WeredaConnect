import * as cdk from 'aws-cdk-lib';
import { Template, Match } from 'aws-cdk-lib/assertions';
import { FaydaMockConstruct } from '../lib/constructs/fayda-mock-construct';

describe('FaydaMockConstruct', () => {
  let template: Template;
  let mock: FaydaMockConstruct;
  let stack: cdk.Stack;

  beforeAll(() => {
    const app = new cdk.App();
    stack = new cdk.Stack(app, 'TestFaydaMockStack', {
      env: { account: '123456789012', region: 'eu-north-1' },
    });
    mock = new FaydaMockConstruct(stack, 'FaydaMock', {
      prefix: 'wereda-dev',
      removalPolicy: cdk.RemovalPolicy.DESTROY,
    });
    template = Template.fromStack(stack);
  });

  test('codes table is on-demand with TTL on expiresAt', () => {
    template.hasResourceProperties('AWS::DynamoDB::Table', {
      TableName: 'wereda-dev-fayda-mock-codes',
      BillingMode: 'PAY_PER_REQUEST',
      KeySchema: [{ AttributeName: 'code', KeyType: 'HASH' }],
      TimeToLiveSpecification: { AttributeName: 'expiresAt', Enabled: true },
    });
  });

  test('function is Node 24 on ARM64 with the expected env', () => {
    template.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: 'wereda-dev-fayda-mock',
      Runtime: 'nodejs24.x',
      Architectures: ['arm64'],
      Handler: 'index.handler',
      MemorySize: 256,
      Timeout: 10,
      Environment: {
        Variables: Match.objectLike({
          CLIENT_ID: 'weredaconnect-wereda-dev',
          CLIENT_KEY_PARAM: '/wereda-dev/fayda/client-private-key',
          SIGNING_KEY_PARAM: '/wereda-dev/fayda-mock/signing-key',
          ALLOWED_REDIRECT_URIS: 'weredaconnect://fayda-callback',
          REQUIRE_PKCE: 'true',
        }),
      },
    });
  });

  test('function URL is public (auth type NONE)', () => {
    template.hasResourceProperties('AWS::Lambda::Url', { AuthType: 'NONE' });
  });

  test('log retention is one week', () => {
    template.hasResourceProperties('AWS::Logs::LogGroup', {
      LogGroupName: '/aws/lambda/wereda-dev-fayda-mock',
      RetentionInDays: 7,
    });
  });

  test('grants SSM get/put on the signing key and client key parameters only', () => {
    const statements = Object.values(template.findResources('AWS::IAM::Policy'))
      .flatMap((p) => (p as { Properties: { PolicyDocument: { Statement: Array<Record<string, unknown>> } } })
        .Properties.PolicyDocument.Statement);
    const ssm = statements.filter((st) => JSON.stringify(st.Action).includes('ssm:'));
    expect(ssm).toHaveLength(1);
    expect(ssm[0].Action).toEqual(['ssm:GetParameter', 'ssm:PutParameter']);
    const resources = JSON.stringify(ssm[0].Resource);
    expect(resources).toContain('parameter/wereda-dev/fayda-mock/signing-key');
    expect(resources).toContain('parameter/wereda-dev/fayda/client-private-key');
    expect((ssm[0].Resource as unknown[]).length).toBe(2);
    // The construct must not create the parameter itself (key stays out of CFN).
    template.resourceCountIs('AWS::SSM::Parameter', 0);
  });

  test('exposes issuerUrl, clientId and the client key parameter', () => {
    expect(mock.clientId).toBe('weredaconnect-wereda-dev');
    expect(mock.clientPrivateKeyParameterName).toBe('/wereda-dev/fayda/client-private-key');
    expect(JSON.stringify(template.toJSON())).not.toContain('CLIENT_SECRET');
    const resolved = JSON.stringify(stack.resolve(mock.issuerUrl));
    expect(resolved).toContain('https://');
    expect(resolved).toContain('FunctionUrl');
  });

  test('skips reserved concurrency in dev (account limit is 10 total)', () => {
    const fns = template.findResources('AWS::Lambda::Function');
    for (const fn of Object.values(fns)) {
      expect((fn as { Properties: Record<string, unknown> }).Properties.ReservedConcurrentExecutions).toBeUndefined();
    }
  });

  test('caps concurrency at 5 outside dev', () => {
    const app = new cdk.App({ context: { environment: 'staging' } });
    const stagingStack = new cdk.Stack(app, 'StagingStack');
    new FaydaMockConstruct(stagingStack, 'FaydaMock', {
      prefix: 'wereda-staging',
      removalPolicy: cdk.RemovalPolicy.DESTROY,
    });
    Template.fromStack(stagingStack).hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: 'wereda-staging-fayda-mock',
      ReservedConcurrentExecutions: 5,
    });
  });

  test('throws when isProd is true', () => {
    const app = new cdk.App();
    const prodStack = new cdk.Stack(app, 'ProdStack');
    expect(() => new FaydaMockConstruct(prodStack, 'FaydaMock', {
      prefix: 'wereda-prod',
      removalPolicy: cdk.RemovalPolicy.RETAIN,
      isProd: true,
    })).toThrow(/production/);
  });
});
