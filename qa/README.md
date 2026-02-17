# Quality Assurance & Security

Threat models, security reviews, and QA processes for WeredaConnect.

## Directory Structure

```
qa/
├── threat_models/         # STRIDE threat models per feature
├── security_reviews/      # Security review checklists
├── test_plans/            # Manual test plans
└── penetration_tests/     # Pentest reports
```

## When to Create a Threat Model

- New feature handling user data
- Authentication/authorization changes
- API endpoints accepting user input
- Data sync operations or file uploads

Use **STRIDE** framework: Spoofing, Tampering, Repudiation, Information Disclosure, DoS, Elevation of Privilege.

## Security Review Checklist

### Authentication & Authorization
- [ ] JWT tokens validated and not expired
- [ ] Authorization checks on all API endpoints
- [ ] No hardcoded credentials in code

### Data Privacy
- [ ] Sensitive data encrypted at rest and in transit (TLS 1.2+)
- [ ] Minimal data collection; user consent obtained

### Input Validation
- [ ] All user input validated (length, type, format)
- [ ] Injection prevention (SQL, XSS)
- [ ] File upload validation (type, size, content)

### API Security
- [ ] Rate limiting enabled
- [ ] Idempotency keys for write operations
- [ ] Error messages don't leak sensitive info

### Mobile Security
- [ ] No sensitive data in logs
- [ ] Secure storage for tokens (Keychain/Keystore)
- [ ] Obfuscation enabled in release builds

### Infrastructure
- [ ] IAM roles follow least privilege
- [ ] Secrets in AWS Secrets Manager
- [ ] CloudWatch alarms for suspicious activity

## Vulnerability Scanning

```bash
# Mobile
flutter pub outdated

# Server
dotnet list package --vulnerable

# Infrastructure
npm audit
```

## Ethiopian Context

- **Privacy**: Users may have government surveillance concerns → minimize server-side storage
- **Low digital literacy**: Phishing risk → in-app security education
- **Compliance**: No comprehensive data privacy law (as of 2026); follow GDPR principles as best practice

---

**Last Updated**: 2026-02-16
