#!/usr/bin/env node
import "source-map-support/register";
import * as cdk from "aws-cdk-lib";
import { DatabaseStack } from "../lib/stacks/database-stack";
import { ApiStack } from "../lib/stacks/api-stack";
import { WebStack } from "../lib/stacks/web-stack";
import { CertificateStack } from "../lib/stacks/certificate-stack";
import { GithubActionsStack } from "../lib/stacks/github-actions-stack";
import { MonitoringStack } from "../lib/stacks/monitoring-stack";
import { environments } from "../lib/config/environments";

const app = new cdk.App();

// Determine environment from context or default to dev
const envName = app.node.tryGetContext("environment") || "dev";
const envConfig = environments[envName] ?? environments["dev"];

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
  syncMetadataTable: databaseStack.syncMetadataTable,
  deliveriesTable: databaseStack.deliveriesTable,
  paymentsTable: databaseStack.paymentsTable,
  escrowsTable: databaseStack.escrowsTable,
  escrowAuditTable: databaseStack.escrowAuditTable,
  escrowIdempotencyTable: databaseStack.escrowIdempotencyTable,
  ordersTable: databaseStack.ordersTable,
  fraudSignalsTable: databaseStack.fraudSignalsTable,
  intentsTable: databaseStack.intentsTable,
  deviceTokensTable: databaseStack.deviceTokensTable,
  uploadsBucket: databaseStack.uploadsBucket,
});
apiStack.addDependency(databaseStack);

// 2b. Monitoring stack (depends on API) — CloudWatch dashboard, metric filters, alarms
const monitoringStack = new MonitoringStack(app, `${stackPrefix}-Monitoring`, {
  env: envProps,
  stackName: `wereda-connect-${envConfig.name}-monitoring`,
  description: `WeredaConnect ${envConfig.name} - CloudWatch dashboard, metric filters, and alarms`,
  tags: envConfig.tags,
  envConfig,
  apiUrl: apiStack.httpApiUrl,
  apiId: apiStack.httpApiId,
});
monitoringStack.addDependency(apiStack);

// 3a. Certificate stack (us-east-1 — hard AWS requirement for CloudFront)
const certStack = new CertificateStack(app, `${stackPrefix}-Certificate`, {
  env: {
    account: process.env.CDK_DEFAULT_ACCOUNT,
    region: "us-east-1",
  },
  stackName: `wereda-connect-${envConfig.name}-certificate`,
  description: `WeredaConnect ${envConfig.name} - ACM certificate (us-east-1, required by CloudFront)`,
  tags: envConfig.tags,
  crossRegionReferences: true,
  envConfig,
});

// 3b. Web stack (eu-north-1 for dev, me-south-1 for staging/prod — S3 + CloudFront)
const webStack = new WebStack(app, `${stackPrefix}-Web`, {
  env: envProps,
  stackName: `wereda-connect-${envConfig.name}-web`,
  description: `WeredaConnect ${envConfig.name} - S3 buckets and CloudFront distributions`,
  tags: envConfig.tags,
  crossRegionReferences: true,
  envConfig,
  certificate: certStack.certificate,
});
webStack.addDependency(certStack);

// 4. GitHub Actions OIDC deploy role (depends on Web for bucket/distribution names)
const githubActionsStack = new GithubActionsStack(
  app,
  `${stackPrefix}-GithubActions`,
  {
    env: envProps,
    stackName: `wereda-connect-${envConfig.name}-github-actions`,
    description: `WeredaConnect ${envConfig.name} - GitHub Actions OIDC deploy role`,
    tags: envConfig.tags,
    envConfig,
    githubRepo: "mitikutesh/WeredaConnect",
    websiteBucketName: webStack.websiteBucketName,
    webappBucketName: webStack.webappBucketName,
    adminBucketName: webStack.adminBucketName,
    websiteDistributionId: webStack.websiteDistributionId,
    webappDistributionId: webStack.webappDistributionId,
    adminDistributionId: webStack.adminDistributionId,
  },
);
githubActionsStack.addDependency(webStack);

app.synth();
