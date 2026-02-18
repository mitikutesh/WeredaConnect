import * as cdk from 'aws-cdk-lib';
import { DatabaseStack } from '../../lib/stacks/database-stack';
import { ApiStack } from '../../lib/stacks/api-stack';
import { MonitoringStack } from '../../lib/stacks/monitoring-stack';
import { environments } from '../../lib/config/environments';

describe('CDK Integration Tests', () => {
  test('full stack wiring synthesizes without errors', () => {
    const app = new cdk.App();
    const envConfig = environments['dev'];
    const envProps = { account: '123456789012', region: envConfig.region };

    const dbStack = new DatabaseStack(app, 'IntegDb', {
      env: envProps,
      envConfig,
    });

    const apiStack = new ApiStack(app, 'IntegApi', {
      env: envProps,
      envConfig,
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
      uploadsBucket: dbStack.uploadsBucket,
    });
    apiStack.addDependency(dbStack);

    const monitoringStack = new MonitoringStack(app, 'IntegMonitoring', {
      env: envProps,
      envConfig,
      apiUrl: apiStack.httpApiUrl,
    });
    monitoringStack.addDependency(apiStack);

    const assembly = app.synth();
    expect(assembly.stacks.length).toBe(3);
  });

  test('all environments can be instantiated', () => {
    for (const [name, envConfig] of Object.entries(environments)) {
      const app = new cdk.App();
      const envProps = { account: '123456789012', region: envConfig.region };

      const dbStack = new DatabaseStack(app, `${name}-Db`, {
        env: envProps,
        envConfig,
      });

      const apiStack = new ApiStack(app, `${name}-Api`, {
        env: envProps,
        envConfig,
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
        uploadsBucket: dbStack.uploadsBucket,
      });

      new MonitoringStack(app, `${name}-Mon`, {
        env: envProps,
        envConfig,
        apiUrl: apiStack.httpApiUrl,
      });

      const assembly = app.synth();
      expect(assembly.stacks.length).toBe(3);
    }
  });
});
