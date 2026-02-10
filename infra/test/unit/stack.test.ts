import * as cdk from 'aws-cdk-lib';
import { Template } from 'aws-cdk-lib/assertions';

describe('WeredaConnect Stack Unit Tests', () => {
  test('Stack can be synthesized', () => {
    const app = new cdk.App();
    const stack = new cdk.Stack(app, 'TestStack');
    
    // Basic assertion - stack should be created
    const template = Template.fromStack(stack);
    expect(template).toBeDefined();
  });

  test('Stack has no resources initially', () => {
    const app = new cdk.App();
    const stack = new cdk.Stack(app, 'TestStack');
    
    const template = Template.fromStack(stack);
    const resources = template.toJSON().Resources;
    
    // Empty stack should have no resources
    expect(resources).toBeUndefined();
  });
});

