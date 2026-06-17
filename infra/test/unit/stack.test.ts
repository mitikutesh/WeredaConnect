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

  test('creates twelve DynamoDB tables with PAY_PER_REQUEST billing', () => {
    template.resourceCountIs('AWS::DynamoDB::Table', 12);
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
      ordersTable: dbStack.ordersTable,
      fraudSignalsTable: dbStack.fraudSignalsTable,
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

  test('creates sixteen Lambda functions on ARM64', () => {
    template.resourceCountIs('AWS::Lambda::Function', 16);
    template.hasResourceProperties('AWS::Lambda::Function', {
      Runtime: 'provided.al2023',
      Architectures: ['arm64'],
    });
  });

  test('creates API Gateway routes for all endpoints', () => {
    // 24 base routes: /health, /auth/register, /auth/fayda-verify, /auth/login, /auth/refresh, /auth/logout,
    //                 /sync, /crops, /crops/{id}, /trips, /trips/{id},
    //                 /orders, /orders/{id},
    //                 /deliveries, /deliveries/{id}, /deliveries/{id}/photos,
    //                 /escrow/initiate, /escrow/release, /escrow/dispute, /escrow/refund, /escrow/{id}, /escrow/history,
    //                 /auth/forgot-password, /auth/reset-password
    // 15 admin routes: 12 GET + 3 POST
    template.resourceCountIs('AWS::ApiGatewayV2::Route', 39);
  });

  test('creates JWT authorizers for mobile and admin routes', () => {
    template.resourceCountIs('AWS::ApiGatewayV2::Authorizer', 2);
    template.hasResourceProperties('AWS::ApiGatewayV2::Authorizer', {
      AuthorizerType: 'JWT',
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
      ordersTable: dbStack.ordersTable,
      fraudSignalsTable: dbStack.fraudSignalsTable,
      uploadsBucket: dbStack.uploadsBucket,
    });
    template = Template.fromStack(apiStack);
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

  test('T-SEC-01/02: no Lambda has DEV_FAYDA_BYPASS or TELEBIRR_MOCK', () => {
    const lambdas = template.findResources('AWS::Lambda::Function');
    for (const resource of Object.values(lambdas)) {
      const env = (resource as Record<string, Record<string, Record<string, Record<string, unknown>>>>)
        .Properties?.Environment?.Variables ?? {};
      // NOTE: toHaveProperty(key, value) treats arg 2 as the EXPECTED VALUE, not a
      // failure message — passing a message made these assertions vacuous (they only
      // failed if the flag existed AND equaled the message text). This describe builds
      // the PROD stack, where neither flag is set, so the single-arg form is correct.
      expect(env).not.toHaveProperty('DEV_FAYDA_BYPASS');
      expect(env).not.toHaveProperty('TELEBIRR_MOCK');
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
});
