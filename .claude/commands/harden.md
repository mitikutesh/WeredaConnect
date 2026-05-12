Audit security posture and generate hardening tasks. Checks pentest findings, OWASP compliance, and WeredaConnect-specific security requirements.

The user may provide: $ARGUMENTS (specific area: "auth", "api", "mobile", "infra", "full", or a specific finding ID like "T-SEC-04")

Steps:

1. **Determine scope**:
   - If "full" or no argument: audit everything
   - If a specific area: focus on that area
   - If a finding ID: check status of that specific finding

2. **Check pentest findings status** (read `TASK.md` TIER 3.5 section):

   Known findings from pentest (2026-03-05):
   | ID | Finding | Expected Status |
   |---|---|---|
   | T-SEC-01 | Dev bypass guard in CI | Should be DONE |
   | T-SEC-02 | TELEBIRR_MOCK bypass guard | Should be DONE |
   | T-SEC-03 | Sensitive health endpoint data | Should be DONE |
   | T-SEC-04 | AWS WAF on API Gateway | PENDING — HIGH priority |
   | T-SEC-05 | /version.json exposure | Should be DONE |
   | T-SEC-06 | Localhost CORS in prod | Should be DONE |
   | T-SEC-07 | Admin token validity | Should be DONE |
   | T-SEC-08 | Admin MFA | Should be DONE |
   | T-SEC-09 | SNS Sandbox exit | PENDING — MEDIUM |
   | T-SEC-10 | E2E Audit Logging | PENDING — HIGH |

   For each DONE finding, verify the fix is still in place (grep for the fix pattern).
   For each PENDING finding, report status and next steps.

3. **Check GA security blockers** (read `TASK.md` TIER 4):
   - T-GA-03: Certificate pinning — is it activated? Check `mobile/lib/core/network/cert_pinning_io.dart`
   - T-GA-04: Git history purge — has it been executed?

4. **Scan for common vulnerabilities**:

   **Server (.NET):**
   - Grep for `ex.Message` or `ex.ToString()` in HTTP responses (info leak)
   - Grep for `Exception` catches that don't log properly
   - Verify `LambdaResponse.Error()` used for all error responses (never raw text)
   - Check rate limiting exists on auth endpoints
   - Verify input validation on all endpoints (check `RequestValidator.cs` coverage)

   **Mobile (Flutter):**
   - Grep for hardcoded URLs, API keys, or passwords
   - Check if `flutter_secure_storage` is used for tokens (not SharedPreferences)
   - Verify no `dart:developer` or `print()` statements with sensitive data in release code
   - Check if ProGuard/R8 obfuscation is configured for release builds

   **Infrastructure (CDK):**
   - Check for `Resource: "*"` in IAM policies (overly permissive)
   - Verify DynamoDB encryption at rest
   - Check S3 bucket policies (no public access)
   - Verify API Gateway CORS is not `*` in prod
   - Check if WAF is attached

5. **Role-based access control audit**:

   Read `.ai/completeness-rules.md` Rule 1.

   Verify server endpoints enforce role-based filtering:
   - GET /crops: Farmer → filtered by userId, Buyer → all
   - GET /trips: Trucker → filtered by userId, Buyer → all
   - Admin endpoints: require admin JWT from separate Cognito pool

6. **Output security report**:

   ```
   ## Security Hardening Report

   ### Pentest Findings Status
   - [DONE/PENDING/REGRESSED] T-SEC-XX: description

   ### GA Security Blockers
   - [DONE/PENDING] T-GA-XX: description

   ### Vulnerability Scan
   - [PASS/FAIL] No info leaks in error responses
   - [PASS/FAIL] No hardcoded secrets
   - [PASS/FAIL] Token storage is secure
   - [PASS/FAIL] Input validation complete
   - [PASS/FAIL] IAM least privilege
   - [PASS/FAIL] Role-based access control enforced

   ### Action Items (prioritized)
   1. [CRITICAL] ...
   2. [HIGH] ...
   3. [MEDIUM] ...
   ```

7. If issues found, offer to fix them (within scope) or create TASK.md entries for larger items.
