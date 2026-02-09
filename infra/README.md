# ☁️ WeredaConnect Infrastructure

## Overview
AWS CDK (Cloud Development Kit) infrastructure as code for WeredaConnect. Defines all AWS resources using TypeScript for type safety and reusability.

---

## Project Structure

```
infra/
├── lib/
│   ├── stacks/           # CDK stack definitions
│   │   ├── api-stack.ts
│   │   ├── database-stack.ts
│   │   └── monitoring-stack.ts
│   ├── constructs/       # Reusable CDK constructs
│   │   ├── lambda-construct.ts
│   │   └── dynamodb-construct.ts
│   └── config/           # Environment-specific configs
│       ├── dev.ts
│       ├── staging.ts
│       └── prod.ts
├── bin/
│   └── app.ts            # CDK app entry point
├── test/                 # Infrastructure tests
└── README.md             # This file
```

---

## Getting Started

### Prerequisites
- Node.js 20.x LTS
- AWS CLI configured
- AWS CDK CLI: `npm install -g aws-cdk`
- AWS account with appropriate permissions

### Setup
```bash
# Install dependencies
npm install

# Bootstrap CDK (first time only)
cdk bootstrap

# Synthesize CloudFormation template
cdk synth

# Deploy to dev environment
cdk deploy --context env=dev

# Deploy all stacks
cdk deploy --all
```

---

## Key Technologies

### Infrastructure as Code
- **AWS CDK**: TypeScript-based IaC
- **CloudFormation**: Underlying deployment engine

### AWS Services

#### Compute
- **Lambda**: Serverless functions (.NET 10 AOT, ARM64)
- **API Gateway**: HTTP API (cheaper than REST API)

#### Storage
- **DynamoDB**: NoSQL database (on-demand billing)
- **S3**: Object storage for user uploads
- **CloudFront**: CDN for static assets

#### Networking
- **Route 53**: DNS management
- **ACM**: SSL/TLS certificates

#### Monitoring
- **CloudWatch**: Logs, metrics, alarms
- **X-Ray**: Distributed tracing (optional)

---

## Architecture Patterns

### 1. Cost-Optimized Lambda
```typescript
new Function(this, 'ApiFunction', {
  runtime: Runtime.DOTNET_10,
  architecture: Architecture.ARM_64, // 20% cheaper than x86
  memorySize: 512, // Start small, scale if needed
  timeout: Duration.seconds(10),
  reservedConcurrentExecutions: 5, // Prevent runaway costs
  environment: {
    POWERTOOLS_SERVICE_NAME: 'wereda-api',
  },
});
```

### 2. DynamoDB with On-Demand Billing
```typescript
new Table(this, 'ReportsTable', {
  partitionKey: { name: 'id', type: AttributeType.STRING },
  sortKey: { name: 'createdAt', type: AttributeType.NUMBER },
  billingMode: BillingMode.PAY_PER_REQUEST, // No provisioned capacity
  pointInTimeRecovery: true, // Backup
  encryption: TableEncryption.AWS_MANAGED,
});
```

### 3. API Gateway with Throttling
```typescript
new HttpApi(this, 'Api', {
  defaultThrottle: {
    rateLimit: 100, // Requests per second
    burstLimit: 200, // Burst capacity
  },
  corsPreflight: {
    allowOrigins: ['*'], // Restrict in production
    allowMethods: [CorsHttpMethod.GET, CorsHttpMethod.POST],
  },
});
```

---

## Environment Configuration

### Development
- **Purpose**: Local testing, rapid iteration
- **Cost**: Minimal (on-demand resources)
- **Data**: Synthetic test data

### Staging
- **Purpose**: Pre-production validation
- **Cost**: Low (similar to dev)
- **Data**: Anonymized production-like data

### Production
- **Purpose**: Live user traffic
- **Cost**: Optimized for low traffic
- **Data**: Real user data (encrypted)

---

## Deployment

### Deploy to Dev
```bash
cdk deploy --context env=dev
```

### Deploy to Staging
```bash
cdk deploy --context env=staging
```

### Deploy to Production
```bash
# Requires manual approval
cdk deploy --context env=prod --require-approval broadening
```

### Destroy Environment
```bash
# ⚠️ DANGEROUS: Deletes all resources
cdk destroy --context env=dev
```

---

## Cost Optimization

### Lambda
- **ARM64**: 20% cheaper than x86
- **Memory**: Start at 512MB, optimize based on metrics
- **Timeout**: Keep low (10s for API, 60s for jobs)
- **Reserved concurrency**: Prevent runaway costs

### DynamoDB
- **On-demand billing**: No wasted provisioned capacity
- **TTL**: Auto-delete old data
- **Indexes**: Only create when necessary (each costs money)

### S3
- **Lifecycle policies**: Move old data to Glacier
- **Intelligent tiering**: Auto-optimize storage class
- **CloudFront**: Reduce S3 GET requests

### API Gateway
- **HTTP API**: 70% cheaper than REST API
- **Caching**: Reduce Lambda invocations (if traffic increases)

---

## Monitoring

### CloudWatch Dashboards
- Lambda invocations, errors, duration
- DynamoDB read/write capacity
- API Gateway request count, latency
- Cost metrics (daily spend)

### Alarms
- **Lambda errors** >1%
- **API latency** >1 second (p95)
- **DynamoDB throttles** >0
- **Daily cost** >$10 (adjust threshold)

### Logs
- Structured JSON logging
- Correlation IDs for tracing
- Retention: 7 days (dev), 30 days (prod)

---

## Security

### IAM
- **Least privilege**: Lambda functions have minimal permissions
- **No wildcard policies**: Explicit resource ARNs

### Encryption
- **At rest**: DynamoDB, S3 (AWS-managed keys)
- **In transit**: TLS 1.2+ (API Gateway, CloudFront)

### Secrets
- **AWS Secrets Manager**: API keys, database credentials
- **Environment variables**: Non-sensitive config only

### Network
- **VPC**: Optional (adds cost and complexity)
- **Security groups**: Restrict inbound/outbound traffic
- **WAF**: Optional (adds cost, consider for production)

---

## Testing

### Unit Tests
```bash
npm test
```

### Integration Tests
```bash
# Deploy to test environment
cdk deploy --context env=test

# Run integration tests
npm run test:integration

# Destroy test environment
cdk destroy --context env=test
```

### Snapshot Tests
```bash
# Update snapshots
npm run test -- -u
```

---

## Common Issues

### CDK Bootstrap Failed
```bash
# Ensure AWS credentials are configured
aws sts get-caller-identity

# Bootstrap with explicit account/region
cdk bootstrap aws://ACCOUNT-ID/REGION
```

### Deployment Stuck
- Check CloudFormation console for errors
- Look for resource limits (e.g., Lambda concurrency)
- Verify IAM permissions

### Lambda Cold Starts
- Use ARM64 + AOT for faster starts
- Increase memory (more CPU = faster init)
- Consider provisioned concurrency (costs money)

---

## CI/CD

### GitHub Actions Workflow
```yaml
name: Deploy Infrastructure

on:
  push:
    branches: [main]
    paths: ['infra/**']

jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3
      - uses: actions/setup-node@v3
        with:
          node-version: '20'
      - run: npm ci
      - run: npm test
      - run: cdk deploy --context env=staging --require-approval never
```

---

## Ethiopian Context Optimizations

### Low Traffic Patterns
- **On-demand billing**: No wasted provisioned capacity
- **Auto-scaling**: Scale to zero when idle
- **Reserved concurrency**: Prevent cost spikes

### Intermittent Connectivity
- **API Gateway timeouts**: 29 seconds (max)
- **Lambda retries**: Exponential backoff
- **DynamoDB**: Eventually consistent reads (cheaper)

### Data Transfer Costs
- **CloudFront**: Cache static assets (reduce origin requests)
- **Compression**: Gzip/Brotli enabled
- **Regional endpoints**: Use closest AWS region (Africa/Middle East)

---

**Last Updated**: 2026-02-09  
**Version**: 0.1.0 (pre-alpha)

