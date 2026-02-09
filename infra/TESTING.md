# 🧪 Infrastructure Testing Guide (AWS CDK)

## Overview
Comprehensive testing guide for WeredaConnect AWS CDK infrastructure. Covers unit tests, snapshot tests, and integration tests for infrastructure as code.

---

## Quick Start

```bash
# Install dependencies
npm install

# Run all tests
npm test

# Run tests in watch mode
npm test -- --watch

# Update snapshots
npm test -- -u

# Run with coverage
npm test -- --coverage
```

---

## 1. Unit Tests

### What to Test
- CDK constructs create expected resources
- Stack configurations are correct
- Resource properties match requirements
- IAM policies are properly configured

### Example: Testing Lambda Construct

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
    // Arrange & Act
    new LambdaConstruct(stack, 'TestLambda', {
      functionName: 'test-function',
      handler: 'bootstrap',
      runtime: 'dotnet10',
    });

    template = Template.fromStack(stack);

    // Assert
    template.hasResourceProperties('AWS::Lambda::Function', {
      FunctionName: 'test-function',
      Runtime: 'dotnet10',
      Architectures: ['arm64'],
    });
  });

  test('sets memory size to 512MB by default', () => {
    new LambdaConstruct(stack, 'TestLambda', {
      functionName: 'test-function',
    });

    template = Template.fromStack(stack);

    template.hasResourceProperties('AWS::Lambda::Function', {
      MemorySize: 512,
    });
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

### Example: Testing DynamoDB Table Construct

```typescript
// test/unit/constructs/dynamodb-construct.test.ts
import { Template, Match } from 'aws-cdk-lib/assertions';

describe('DynamoDBConstruct', () => {
  test('creates table with on-demand billing', () => {
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
      PointInTimeRecoverySpecification: {
        PointInTimeRecoveryEnabled: true,
      },
    });
  });

  test('enables encryption with AWS managed keys', () => {
    const app = new App();
    const stack = new Stack(app, 'TestStack');

    new DynamoDBConstruct(stack, 'TestTable', {
      tableName: 'Reports',
    });

    const template = Template.fromStack(stack);

    template.hasResourceProperties('AWS::DynamoDB::Table', {
      SSESpecification: {
        SSEEnabled: true,
      },
    });
  });
});
```

---

## 2. Snapshot Tests

### What to Test
- Complete CloudFormation template structure
- Detect unintended infrastructure changes
- Validate resource relationships

### Example: Stack Snapshot Test

```typescript
// test/snapshot/api-stack.test.ts
import { App } from 'aws-cdk-lib';
import { Template } from 'aws-cdk-lib/assertions';
import { ApiStack } from '../../lib/stacks/api-stack';

describe('ApiStack Snapshot', () => {
  test('matches snapshot', () => {
    const app = new App();
    const stack = new ApiStack(app, 'TestApiStack', {
      env: 'dev',
    });

    const template = Template.fromStack(stack);

    // This will create/update snapshot file
    expect(template.toJSON()).toMatchSnapshot();
  });
});
```

### Snapshot File Example
```json
// test/snapshot/__snapshots__/api-stack.test.ts.snap
{
  "Resources": {
    "ApiFunction": {
      "Type": "AWS::Lambda::Function",
      "Properties": {
        "Runtime": "dotnet10",
        "Architectures": ["arm64"],
        "MemorySize": 512
      }
    }
  }
}
```

---

## 3. Integration Tests

### What to Test
- Stacks can be synthesized without errors
- Cross-stack references work correctly
- Environment-specific configurations are correct

### Example: Multi-Stack Integration Test

```typescript
// test/integration/app.test.ts
import { App } from 'aws-cdk-lib';
import { Template } from 'aws-cdk-lib/assertions';
import { DatabaseStack } from '../../lib/stacks/database-stack';
import { ApiStack } from '../../lib/stacks/api-stack';

describe('App Integration', () => {
  test('API stack can reference database stack', () => {
    const app = new App();
    
    const dbStack = new DatabaseStack(app, 'DbStack', {
      env: 'dev',
    });
    
    const apiStack = new ApiStack(app, 'ApiStack', {
      env: 'dev',
      reportsTable: dbStack.reportsTable,
    });

    // Should synthesize without errors
    const apiTemplate = Template.fromStack(apiStack);
    
    // Verify Lambda has environment variable for table name
    apiTemplate.hasResourceProperties('AWS::Lambda::Function', {
      Environment: {
        Variables: {
          REPORTS_TABLE_NAME: { Ref: expect.any(String) },
        },
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

## 4. Smoke Tests

### What to Test
- CDK app can be synthesized
- No breaking changes in dependencies
- Environment configurations are valid

### Example: Smoke Test Suite

```typescript
// test/smoke/synth.test.ts
import { App } from 'aws-cdk-lib';
import * as cdk from '../../bin/app';

describe('CDK Smoke Tests', () => {
  test('app synthesizes successfully', () => {
    const app = new App();
    
    expect(() => {
      cdk.createStacks(app, 'dev');
      app.synth();
    }).not.toThrow();
  });

  test('all environments can be synthesized', () => {
    const environments = ['dev', 'staging', 'prod'];
    
    environments.forEach(env => {
      const app = new App();
      
      expect(() => {
        cdk.createStacks(app, env);
        app.synth();
      }).not.toThrow();
    });
  });
});
```

---

## Test Configuration

### package.json
```json
{
  "scripts": {
    "test": "jest",
    "test:watch": "jest --watch",
    "test:coverage": "jest --coverage"
  },
  "devDependencies": {
    "@types/jest": "^29.5.0",
    "@types/node": "^20.0.0",
    "jest": "^29.5.0",
    "ts-jest": "^29.1.0",
    "aws-cdk-lib": "^2.170.0"
  }
}
```

### jest.config.js
```javascript
module.exports = {
  preset: 'ts-jest',
  testEnvironment: 'node',
  roots: ['<rootDir>/test'],
  testMatch: ['**/*.test.ts'],
  collectCoverageFrom: [
    'lib/**/*.ts',
    '!lib/**/*.d.ts',
  ],
  coverageThreshold: {
    global: {
      branches: 70,
      functions: 70,
      lines: 70,
      statements: 70,
    },
  },
};
```

---

## Best Practices

### 1. Use Fine-Grained Assertions
```typescript
// ✅ GOOD: Specific assertions
template.hasResourceProperties('AWS::Lambda::Function', {
  Runtime: 'dotnet10',
  Architectures: ['arm64'],
});

// ❌ BAD: Too broad
template.resourceCountIs('AWS::Lambda::Function', 1);
```

### 2. Test IAM Policies
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
        Match.not(Match.objectLike({
          Resource: '*',
          Action: '*',
        })),
      ]),
    },
  });
});
```

### 3. Test Cost Optimization
```typescript
test('uses cost-optimized configurations', () => {
  template.hasResourceProperties('AWS::Lambda::Function', {
    Architectures: ['arm64'], // 20% cheaper
    ReservedConcurrentExecutions: Match.anyValue(), // Prevent runaway costs
  });

  template.hasResourceProperties('AWS::DynamoDB::Table', {
    BillingMode: 'PAY_PER_REQUEST', // No wasted capacity
  });
});
```

### 4. Update Snapshots Carefully
```bash
# Review changes before updating
npm test

# If changes are intentional, update snapshots
npm test -- -u

# Commit snapshot changes with explanation
git add test/snapshot/__snapshots__/
git commit -m "test: update snapshots after adding encryption"
```

---

## CI/CD Integration

### GitHub Actions Example
```yaml
name: CDK Tests

on: [push, pull_request]

jobs:
  test:
    runs-on: ubuntu-latest

    steps:
      - uses: actions/checkout@v3

      - name: Setup Node.js
        uses: actions/setup-node@v3
        with:
          node-version: '20'

      - name: Install dependencies
        run: npm ci

      - name: Run tests
        run: npm test

      - name: Synthesize stacks
        run: npm run cdk synth

      - name: Upload coverage
        uses: codecov/codecov-action@v3
        with:
          files: ./coverage/lcov.info
```

---

## Common Assertions

### Resource Existence
```typescript
// Check resource exists
template.resourceCountIs('AWS::Lambda::Function', 1);

// Check resource doesn't exist
template.resourceCountIs('AWS::EC2::Instance', 0);
```

### Resource Properties
```typescript
// Exact match
template.hasResourceProperties('AWS::Lambda::Function', {
  Runtime: 'dotnet10',
});

// Partial match
template.hasResourceProperties('AWS::Lambda::Function',
  Match.objectLike({
    Runtime: 'dotnet10',
  })
);

// Array contains
template.hasResourceProperties('AWS::Lambda::Function', {
  Architectures: Match.arrayWith(['arm64']),
});
```

### Outputs
```typescript
// Check stack output exists
template.hasOutput('ApiEndpoint', {
  Value: Match.anyValue(),
});
```

---

**Last Updated**: 2026-02-09
**Version**: 1.0.0

