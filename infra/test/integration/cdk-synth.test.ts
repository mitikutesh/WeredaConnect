import * as cdk from 'aws-cdk-lib';

describe('CDK Integration Tests', () => {
  test('CDK app can be instantiated', () => {
    const app = new cdk.App();
    expect(app).toBeDefined();
  });

  test('Stack can be added to app', () => {
    const app = new cdk.App();
    const stack = new cdk.Stack(app, 'IntegrationTestStack');
    
    expect(stack).toBeDefined();
    expect(stack.stackName).toBe('IntegrationTestStack');
  });

  test('App can synthesize stacks', () => {
    const app = new cdk.App();
    new cdk.Stack(app, 'TestStack1');
    new cdk.Stack(app, 'TestStack2');
    
    const assembly = app.synth();
    expect(assembly.stacks.length).toBeGreaterThanOrEqual(2);
  });
});

