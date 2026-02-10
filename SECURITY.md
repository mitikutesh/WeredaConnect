# Security Policy

## Supported Versions

Currently supported versions of WeredaConnect:

| Version | Supported          |
| ------- | ------------------ |
| 0.1.x   | :white_check_mark: |

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub issues.**

Instead, please report them via email to: **mitikutesh@gmail.com**

You should receive a response within 48 hours. If for some reason you do not, please follow up via email to ensure we received your original message.

Please include the following information:

- Type of issue (e.g., buffer overflow, SQL injection, cross-site scripting, etc.)
- Full paths of source file(s) related to the manifestation of the issue
- The location of the affected source code (tag/branch/commit or direct URL)
- Any special configuration required to reproduce the issue
- Step-by-step instructions to reproduce the issue
- Proof-of-concept or exploit code (if possible)
- Impact of the issue, including how an attacker might exploit it

## Security Measures

WeredaConnect implements the following security measures:

### Automated Security Scanning

- **Dependabot**: Automated dependency vulnerability scanning and updates
- **GitHub Secret Scanning**: Detects accidentally committed secrets
- **GitHub Code Scanning**: Static analysis security testing (planned)

### Development Practices

- All dependencies are regularly updated via Dependabot
- Security-focused code reviews for all PRs
- Principle of least privilege for AWS IAM roles
- Encryption at rest for all data stores
- HTTPS/TLS for all network communication

### Infrastructure Security

- AWS resources follow security best practices
- S3 buckets have public access blocked
- DynamoDB has point-in-time recovery enabled
- CloudWatch logging for audit trails

### Mobile App Security

- Offline-first architecture minimizes attack surface
- Local database encryption (planned)
- Certificate pinning for API calls (planned)
- Secure token storage (planned)

## Security Roadmap

See `SECURITY_IMPLEMENTATION_GAPS.md` for detailed security implementation plan.

Priority items:
1. Enable GitHub Secret Scanning (Week 1)
2. Implement authentication/authorization (Week 2-3)
3. Add database encryption (Week 3-4)
4. Configure SAST scanning with SonarQube (Week 4-5)
5. Implement certificate pinning (Week 5-6)

## Disclosure Policy

When we receive a security bug report, we will:

1. Confirm the problem and determine affected versions
2. Audit code to find any similar problems
3. Prepare fixes for all supported versions
4. Release patches as soon as possible

## Comments on This Policy

If you have suggestions on how this process could be improved, please submit a pull request.

