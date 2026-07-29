# WeredaConnect Infrastructure

AWS CDK (TypeScript) infrastructure as code for WeredaConnect. Defines all AWS resources with type safety and reusability.

> **Setup instructions**: See `docs/developer/getting-started.md`

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
└── test/                 # Infrastructure tests
```

---

## AWS Services

| Category | Service | Notes |
|----------|---------|-------|
| Compute | Lambda (.NET 10 AOT, ARM64) | 20% cheaper than x86 |
| API | API Gateway (HTTP API) | 70% cheaper than REST API |
| Database | DynamoDB (on-demand billing) | Scale to zero |
| Storage | S3 + CloudFront CDN | Static assets |
| DNS/TLS | Route 53 + ACM | SSL certificates |
| Monitoring | CloudWatch + X-Ray (optional) | Logs, metrics, alarms |

---

## Architecture Patterns

### Cost-Optimized Lambda
```typescript
new Function(this, 'ApiFunction', {
  runtime: Runtime.DOTNET_10,
  architecture: Architecture.ARM_64,
  memorySize: 512,
  timeout: Duration.seconds(10),
  reservedConcurrentExecutions: 5,  // prevent runaway costs
});
```

### DynamoDB On-Demand
```typescript
new Table(this, 'ReportsTable', {
  partitionKey: { name: 'id', type: AttributeType.STRING },
  sortKey: { name: 'createdAt', type: AttributeType.NUMBER },
  billingMode: BillingMode.PAY_PER_REQUEST,
  pointInTimeRecovery: true,
  encryption: TableEncryption.AWS_MANAGED,
});
```

### API Gateway with Throttling
```typescript
new HttpApi(this, 'Api', {
  defaultThrottle: { rateLimit: 100, burstLimit: 200 },
  corsPreflight: {
    allowOrigins: ['*'],  // restrict in production
    allowMethods: [CorsHttpMethod.GET, CorsHttpMethod.POST],
  },
});
```

---

## Deployment Commands

```bash
cdk deploy --context env=dev          # Development
cdk deploy --context env=staging      # Staging
cdk deploy --context env=prod --require-approval broadening  # Production
cdk destroy --context env=dev         # Tear down (DANGEROUS)
```

---

## Cost Optimization

- **Lambda**: ARM64 (20% cheaper), start at 512MB, low timeouts, reserved concurrency
- **DynamoDB**: On-demand billing, TTL for old data, minimal indexes
- **S3**: Lifecycle policies (Glacier), intelligent tiering, CloudFront caching
- **API Gateway**: HTTP API (70% cheaper than REST)

---

## Monitoring & Alarms

| Alarm | Threshold |
|-------|-----------|
| Lambda errors | >1% |
| API latency (p95) | >1s |
| DynamoDB throttles | >0 |
| Daily cost | >$10 |

Logs: Structured JSON, correlation IDs, retention 7d (dev) / 30d (prod)

---

## Security

- **IAM**: Least privilege, no wildcard policies, explicit resource ARNs
- **Encryption**: At rest (DynamoDB, S3 AWS-managed keys), in transit (TLS 1.2+)
- **Secrets**: AWS Secrets Manager (prod) / SSM Parameter Store SecureString (dev+staging, free tier — seeded by `scripts/seed-ssm-secrets.sh`); readers cache per Lambda container. See `specs/decisions/secrets-backend-per-environment.md`
- **Network**: VPC optional (adds cost), WAF optional for production

---

## Ethiopian Context Optimizations

- **On-demand billing**: Scale to zero when idle
- **API Gateway timeouts**: 29s max for intermittent connectivity
- **Lambda retries**: Exponential backoff
- **DynamoDB**: Eventually consistent reads (cheaper)
- **CloudFront + compression**: Gzip/Brotli, regional endpoints

---

**Last Updated**: 2026-02-09
**Version**: 0.1.0 (pre-alpha)
