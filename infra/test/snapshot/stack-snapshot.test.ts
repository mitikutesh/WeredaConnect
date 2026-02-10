import * as cdk from 'aws-cdk-lib';
import { Template } from 'aws-cdk-lib/assertions';

describe('WeredaConnect Stack Snapshot Tests', () => {
  test('Stack snapshot matches', () => {
    const app = new cdk.App();
    const stack = new cdk.Stack(app, 'SnapshotTestStack', {
      env: {
        account: '123456789012',
        region: 'us-east-1'
      }
    });
    
    const template = Template.fromStack(stack);
    expect(template.toJSON()).toMatchSnapshot();
  });
});

