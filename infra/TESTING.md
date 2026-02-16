# Infrastructure Testing Guide (AWS CDK)

> **Testing philosophy and priorities**: See `.ai/testing.md`

## Quick Start

```bash
npm test                  # All tests
npm test -- --watch       # Watch mode
npm test -- -u            # Update snapshots
npm test -- --coverage    # With coverage
```

---

## CDK Construct Tests

### Lambda Construct

```typescript
// test/unit/constructs/lambda-construct.test.ts
import { App, Stack } from 'aws-cdk-lib';
import { Template } from 'aws-cdk-lib/assertions';
import { LambdaConstruct } from '../../lib/constructs/lambda-construct';

describe('LambdaConstruct', () => {
  let app: App;
  let stack: Stack;
  let template: Template;

  beforeEach(() => {
    app = new App();
    stack = new Stack(app, 'TestStack');
  });

  test('creates Lambda function with ARM64 architecture', () => {
    new LambdaConstruct(stack, 'TestLambda', {
      functionName: 'test-function',
      handler: 'bootstrap',
      runtime: 'dotnet10',
    });
    template = Template.fromStack(stack);
    template.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: 'test-function',
      Runtime: 'dotnet10',
      Architectures: ['arm64'],
    });
  });

  test('sets memory size to 512MB by default', () => {
    new LambdaConstruct(stack, 'TestLambda', { functionName: 'test-function' });
    template = Template.fromStack(stack);
    template.hasResourceProperties('AWS::Lambda::Function', { MemorySize: 512 });
  });

  test('applies reserved concurrent executions limit', () => {
    new LambdaConstruct(stack, 'TestLambda', {
      functionName: 'test-function',
      reservedConcurrentExecutions: 5,
    });
    template = Template.fromStack(stack);
    template.hasResourceProperties('AWS::Lambda::Function', {
      ReservedConcurrentExecutions: 5,
    });
  });
});
```

### DynamoDB Table Construct

```typescript
// test/unit/constructs/dynamodb-construct.test.ts
describe('DynamoDBConstruct', () => {
  test('creates table with on-demand billing and PITR', () => {
    const app = new App();
    const stack = new Stack(app, 'TestStack');
    new DynamoDBConstruct(stack, 'TestTable', {
      tableName: 'Reports',
      partitionKey: { name: 'id', type: 'S' },
    });
    const template = Template.fromStack(stack);
    template.hasResourceProperties('AWS::DynamoDB::Table', {
      TableName: 'Reports',
      BillingMode: 'PAY_PER_REQUEST',
      PointInTimeRecoverySpecification: { PointInTimeRecoveryEnabled: true },
    });
  });

  test('enables encryption with AWS managed keys', () => {
    const app = new App();
    const stack = new Stack(app, 'TestStack');
    new DynamoDBConstruct(stack, 'TestTable', { tableName: 'Reports' });
    const template = Template.fromStack(stack);
    template.hasResourceProperties('AWS::DynamoDB::Table', {
      SSESpecification: { SSEEnabled: true },
    });
  });
});
```

---

## IAM Policy Validation

```typescript
test('Lambda has minimal IAM permissions', () => {
  template.hasResourceProperties('AWS::IAM::Role', {
    AssumeRolePolicyDocument: {
      Statement: [{
        Action: 'sts:AssumeRole',
        Effect: 'Allow',
        Principal: { Service: 'lambda.amazonaws.com' },
      }],
    },
  });

  // Verify no wildcard permissions
  template.allResourcesProperties('AWS::IAM::Policy', {
    PolicyDocument: {
      Statement: Match.arrayWith([
        Match.not(Match.objectLike({ Resource: '*', Action: '*' })),
      ]),
    },
  });
});
```

---

## Snapshot Tests

```typescript
// test/snapshot/api-stack.test.ts
describe('ApiStack Snapshot', () => {
  test('matches snapshot', () => {
    const app = new App();
    const stack = new ApiStack(app, 'TestApiStack', { env: 'dev' });
    const template = Template.fromStack(stack);
    expect(template.toJSON()).toMatchSnapshot();
  });
});
```

Update snapshots carefully:
```bash
npm test                  # Review changes
npm test -- -u            # Update if intentional
git add test/snapshot/__snapshots__/
git commit -m "test: update snapshots after adding encryption"
```

---

## Integration Tests (Multi-Stack)

```typescript
// test/integration/app.test.ts
describe('App Integration', () => {
  test('API stack can reference database stack', () => {
    const app = new App();
    const dbStack = new DatabaseStack(app, 'DbStack', { env: 'dev' });
    const apiStack = new ApiStack(app, 'ApiStack', {
      env: 'dev',
      reportsTable: dbStack.reportsTable,
    });
    const apiTemplate = Template.fromStack(apiStack);
    apiTemplate.hasResourceProperties('AWS::Lambda::Function', {
      Environment: {
        Variables: { REPORTS_TABLE_NAME: { Ref: expect.any(String) } },
      },
    });
  });

  test('all stacks synthesize for production environment', () => {
    const app = new App();
    expect(() => {
      new DatabaseStack(app, 'ProdDbStack', { env: 'prod' });
      new ApiStack(app, 'ProdApiStack', { env: 'prod' });
    }).not.toThrow();
  });
});
```

---

## Smoke Tests

```typescript
// test/smoke/synth.test.ts
describe('CDK Smoke Tests', () => {
  test('app synthesizes successfully', () => {
    const app = new App();
    expect(() => {
      cdk.createStacks(app, 'dev');
      app.synth();
    }).not.toThrow();
  });

  test('all environments can be synthesized', () => {
    ['dev', 'staging', 'prod'].forEach(env => {
      const app = new App();
      expect(() => { cdk.createStacks(app, env); app.synth(); }).not.toThrow();
    });
  });
});
```

---

## Best Practices

### Use fine-grained assertions
```typescript
// GOOD: Specific assertions
template.hasResourceProperties('AWS::Lambda::Function', {
  Runtime: 'dotnet10',
  Architectures: ['arm64'],
});

// BAD: Too broad
template.resourceCountIs('AWS::Lambda::Function', 1);
```

### Test cost optimization
```typescript
test('uses cost-optimized configurations', () => {
  template.hasResourceProperties('AWS::Lambda::Function', {
    Architectures: ['arm64'],
    ReservedConcurrentExecutions: Match.anyValue(),
  });
  template.hasResourceProperties('AWS::DynamoDB::Table', {
    BillingMode: 'PAY_PER_REQUEST',
  });
});
```

### Common assertion patterns
```typescript
// Resource exists / doesn't exist
template.resourceCountIs('AWS::Lambda::Function', 1);
template.resourceCountIs('AWS::EC2::Instance', 0);

// Partial match
template.hasResourceProperties('AWS::Lambda::Function',
  Match.objectLike({ Runtime: 'dotnet10' })
);

// Array contains
template.hasResourceProperties('AWS::Lambda::Function', {
  Architectures: Match.arrayWith(['arm64']),
});

// Stack output exists
template.hasOutput('ApiEndpoint', { Value: Match.anyValue() });
```

---

**Last Updated**: 2026-02-09
