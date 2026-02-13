#!/usr/bin/env node
import 'source-map-support/register';
import * as cdk from 'aws-cdk-lib';
import { DatabaseStack } from '../lib/stacks/database-stack';
import { ApiStack } from '../lib/stacks/api-stack';
import { MonitoringStack } from '../lib/stacks/monitoring-stack';
import { environments } from '../lib/config/environments';

const app = new cdk.App();

// Determine environment from context or default to dev
const envName = app.node.tryGetContext('env') || 'dev';
const envConfig = environments[envName] ?? environments['dev'];

const envProps: cdk.Environment = {
  account: process.env.CDK_DEFAULT_ACCOUNT,
  region: envConfig.region,
};

// Stack naming: wereda-connect-{env}-{layer}
const stackPrefix = `WeredaConnect-${envConfig.name}`;

// 1. Database stack (no dependencies)
const databaseStack = new DatabaseStack(app, `${stackPrefix}-Database`, {
  env: envProps,
  stackName: `wereda-connect-${envConfig.name}-database`,
  description: `WeredaConnect ${envConfig.name} - DynamoDB tables and S3 buckets`,
  tags: envConfig.tags,
  envConfig,
});

// 2. API stack (depends on Database) — Cognito, auth Lambdas, API Gateway, routes
const apiStack = new ApiStack(app, `${stackPrefix}-API`, {
  env: envProps,
  stackName: `wereda-connect-${envConfig.name}-api`,
  description: `WeredaConnect ${envConfig.name} - Cognito, API Gateway, and Lambda functions`,
  tags: envConfig.tags,
  envConfig,
  usersTable: databaseStack.usersTable,
  cropsTable: databaseStack.cropsTable,
  tripsTable: databaseStack.tripsTable,
  rateLimitsTable: databaseStack.rateLimitsTable,
  uploadsBucket: databaseStack.uploadsBucket,
});
apiStack.addDependency(databaseStack);

// 3. Monitoring stack (depends on API)
const monitoringStack = new MonitoringStack(app, `${stackPrefix}-Monitoring`, {
  env: envProps,
  stackName: `wereda-connect-${envConfig.name}-monitoring`,
  description: `WeredaConnect ${envConfig.name} - CloudWatch dashboards and alarms`,
  tags: envConfig.tags,
  envConfig,
  apiUrl: apiStack.httpApiUrl,
});
monitoringStack.addDependency(apiStack);

app.synth();
