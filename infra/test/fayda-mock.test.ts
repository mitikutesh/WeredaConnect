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
          CLIENT_SECRET: 'dev-fayda-mock-secret',
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

  test('grants SSM get/put on the signing key parameter only', () => {
    template.hasResourceProperties('AWS::IAM::Policy', {
      PolicyDocument: {
        Statement: Match.arrayWith([
          Match.objectLike({
            Action: ['ssm:GetParameter', 'ssm:PutParameter'],
            Resource: Match.objectLike({
              'Fn::Join': Match.arrayWith([
                Match.arrayWith([':ssm:eu-north-1:123456789012:parameter/wereda-dev/fayda-mock/signing-key']),
              ]),
            }),
          }),
        ]),
      },
    });
    // The construct must not create the parameter itself (key stays out of CFN).
    template.resourceCountIs('AWS::SSM::Parameter', 0);
  });

  test('exposes issuerUrl, clientId and clientSecret', () => {
    expect(mock.clientId).toBe('weredaconnect-wereda-dev');
    expect(mock.clientSecret).toBe('dev-fayda-mock-secret');
    const resolved = JSON.stringify(stack.resolve(mock.issuerUrl));
    expect(resolved).toContain('https://');
    expect(resolved).toContain('FunctionUrl');
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
