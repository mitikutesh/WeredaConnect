#!/usr/bin/env node
import 'source-map-support/register';
import * as cdk from 'aws-cdk-lib';
import { WeredaConnectStack } from '../lib/stacks/wereda-connect-stack';

const app = new cdk.App();

// Development stack
new WeredaConnectStack(app, 'WeredaConnectDevStack', {
  env: {
    account: process.env.CDK_DEFAULT_ACCOUNT,
    region: process.env.CDK_DEFAULT_REGION || 'us-east-1',
  },
  stackName: 'wereda-connect-dev',
  description: 'WeredaConnect Development Stack - Offline-first agritech logistics platform',
  tags: {
    Environment: 'development',
    Project: 'WeredaConnect',
    ManagedBy: 'CDK',
  },
});

// Production stack (commented out for now)
// new WeredaConnectStack(app, 'WeredaConnectProdStack', {
//   env: {
//     account: process.env.CDK_DEFAULT_ACCOUNT,
//     region: 'us-east-1',
//   },
//   stackName: 'wereda-connect-prod',
//   description: 'WeredaConnect Production Stack',
//   tags: {
//     Environment: 'production',
//     Project: 'WeredaConnect',
//     ManagedBy: 'CDK',
//   },
// });

app.synth();

