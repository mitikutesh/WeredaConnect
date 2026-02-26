import * as cdk from 'aws-cdk-lib';
import { Template } from 'aws-cdk-lib/assertions';
import { DatabaseStack } from '../../lib/stacks/database-stack';
import { ApiStack } from '../../lib/stacks/api-stack';
import { MonitoringStack } from '../../lib/stacks/monitoring-stack';
import { environments } from '../../lib/config/environments';

const devConfig = environments['dev'];

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

  test('creates eleven DynamoDB tables with PAY_PER_REQUEST billing', () => {
    template.resourceCountIs('AWS::DynamoDB::Table', 11);
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

  test('creates Cognito User Pool with phone sign-in', () => {
    template.hasResourceProperties('AWS::Cognito::UserPool', {
      UsernameAttributes: ['phone_number'],
      AutoVerifiedAttributes: ['phone_number'],
    });
  });

  test('creates Cognito User Pool Client', () => {
    template.resourceCountIs('AWS::Cognito::UserPoolClient', 1);
  });

  test('creates twelve Lambda functions on ARM64', () => {
    template.resourceCountIs('AWS::Lambda::Function', 12);
    template.hasResourceProperties('AWS::Lambda::Function', {
      Runtime: 'provided.al2023',
      Architectures: ['arm64'],
    });
  });

  test('creates API Gateway routes for all endpoints', () => {
    // 20 routes: /health, /auth/register, /auth/verify-otp, /auth/login, /auth/refresh, /auth/logout,
    //            /sync, /crops, /crops/{id}, /trips, /trips/{id},
    //            /deliveries, /deliveries/{id}, /deliveries/{id}/photos,
    //            /escrow/initiate, /escrow/release, /escrow/dispute, /escrow/refund, /escrow/{id}, /escrow/history
    template.resourceCountIs('AWS::ApiGatewayV2::Route', 20);
  });

  test('creates JWT authorizer for protected routes', () => {
    template.resourceCountIs('AWS::ApiGatewayV2::Authorizer', 1);
    template.hasResourceProperties('AWS::ApiGatewayV2::Authorizer', {
      AuthorizerType: 'JWT',
    });
  });
});

describe('MonitoringStack', () => {
  test('creates CloudWatch dashboard', () => {
    const app = new cdk.App();
    const stack = new MonitoringStack(app, 'TestMonitoringStack', {
      env: { account: '123456789012', region: devConfig.region },
      envConfig: devConfig,
      apiUrl: 'https://test.execute-api.me-south-1.amazonaws.com',
    });

    const template = Template.fromStack(stack);
    template.resourceCountIs('AWS::CloudWatch::Dashboard', 1);
  });
});
