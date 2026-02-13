import * as cdk from 'aws-cdk-lib';
import { Template } from 'aws-cdk-lib/assertions';
import { DatabaseStack } from '../../lib/stacks/database-stack';
import { environments } from '../../lib/config/environments';

describe('Database Stack Snapshot', () => {
  test('database stack matches snapshot', () => {
    const app = new cdk.App();
    const stack = new DatabaseStack(app, 'SnapshotDbStack', {
      env: { account: '123456789012', region: 'me-south-1' },
      envConfig: environments['dev'],
    });

    const template = Template.fromStack(stack);
    expect(template.toJSON()).toMatchSnapshot();
  });
});
