Implement or audit error handling and error mapping (translation) between backend and frontend.

The goal: hide internal backend errors and show user-friendly, localized messages in every UI surface. The user may provide: $ARGUMENTS (specific feature, domain, or scope — e.g. "escrow", "auth", "delivery", "full audit")

Steps:

1. **Understand the scope**
   - If the user specified a domain (e.g. "escrow"), focus on that domain's contract and code.
   - If no domain specified, audit all domains: auth, crop listings, trip listings, delivery verification, escrow, sync, orders, and admin.

2. **Read the API contract(s)**
   - Read the relevant contract(s) in `specs/contracts/` to get the canonical error codes and HTTP status mappings.
   - These contracts are the **single source of truth** for error codes.

3. **Audit the server error responses**
   - Read `server/src/Helpers/LambdaResponse.cs` — this is the centralized error response builder.
   - Check that every Function handler (in `server/src/Functions/`) uses `LambdaResponse.Error()` with the correct code from the contract.
   - Verify that **no raw exception messages or stack traces** leak to the client. The `message` field in error responses must be a safe, user-facing string — never an internal exception message.
   - Verify that 500 responses always use a generic message like "Internal server error" and log the real exception server-side only.
   - Flag any `catch` block that passes `ex.Message` directly to the response.

4. **Audit/implement mobile error mapping (Flutter)**
   - Check existing error code classes:
     - `mobile/lib/core/auth/models/auth_error.dart` (AuthErrorCode + extension)
     - `mobile/lib/features/delivery_verification/data/delivery_validation_exception.dart` (DeliveryErrorCode)
     - `mobile/lib/features/delivery_verification/presentation/delivery_error_l10n.dart`
     - `mobile/lib/features/escrow_payment/presentation/escrow_error_l10n.dart`
   - For each domain, verify or create:
     a. **Error code constants** — must match the server contract codes exactly
     b. **Localized error mapper** — a function that takes `AppLocalizations` + error code and returns a localized string
     c. **ARB keys** — ensure `.arb` files (`app_en.arb`, `app_am.arb`, `app_om.arb`) have entries for every error code
     d. **Fallback messages** — English fallbacks for when l10n is unavailable
   - Follow the established pattern (see `delivery_error_l10n.dart` and `escrow_error_l10n.dart`).
   - Mobile-only errors (network, offline, storage) must also be mapped.

5. **Audit/implement Flutter Web error mapping**
   - Flutter Web shares the same Dart codebase as the mobile app.
   - Verify that error mappers work correctly in web context (no platform-specific assumptions).
   - Check that offline-specific errors degrade gracefully on web (web has no Isar, no secure storage).

6. **Audit/implement admin dashboard error mapping (React/TypeScript)**
   - Read `admin/src/lib/api.ts` — the `ApiError` class and `request()` function.
   - Verify or implement:
     a. **Error response parsing** — extract the `{ error: { code, message, field } }` envelope from API responses.
     b. **Error code to i18n key mapping** — map server error codes to i18n keys in `admin/src/i18n/{en,am,om}.json`.
     c. **Generic fallback** — unknown error codes show "An unexpected error occurred" (never raw server text).
     d. **401/403 handling** — redirect to login on auth errors, show "Access denied" on forbidden.
     e. **Network error handling** — catch `fetch` failures and show "Unable to reach server".
   - The admin is used by internal staff, so error messages can be slightly more technical but must still not expose internals.

7. **Audit/implement marketing website error handling (Astro)**
   - The website (`website/src/`) is mostly static (Astro SSG) with no API calls.
   - If there are any contact forms or dynamic endpoints, verify error handling.
   - Verify that the 404 page (`website/src/pages/404.astro`) is localized.
   - This is typically the lightest audit — confirm and move on.

8. **Cross-cutting checks**
   - **No internal leaks**: Grep for patterns that might leak internals:
     - `ex.Message`, `ex.StackTrace`, `ex.ToString()` in server responses
     - Raw `error.message` from `ApiError` displayed directly in UI without mapping
     - Console.log of full error objects in production admin code
   - **Consistent error envelope**: All server endpoints must return `{ "error": { "code": "...", "message": "..." } }` for errors — never plain text or HTML.
   - **Retry guidance**: Errors that are retryable (429, 503, 500) should include `retryAfterSeconds` or the client should implement exponential backoff.
   - **Offline graceful degradation**: When the client is offline and gets a network error, it must show a friendly offline message, not a raw fetch/socket error.

9. **Output a report**
   - For each UI surface (mobile, web, admin, website), list:
     - PASS: Error codes that are correctly mapped end-to-end
     - MISSING: Error codes in the contract that have no client-side mapping
     - LEAK: Places where internal errors might reach the user
     - ACTION: Specific code changes needed (file:line references)
   - If implementing (not just auditing), make the changes and validate the build.

10. **Validate**
    - If any files were changed:
      - `cd mobile && flutter analyze` (if Dart files changed)
      - `cd server && dotnet build --no-restore` (if C# files changed)
      - `cd admin && npx tsc --noEmit` (if TypeScript files changed)
    - Ensure no regressions.

## Error Mapping Pattern Reference

### Server (C#) — always use LambdaResponse.Error():
```csharp
return LambdaResponse.Error(400, "AUTH_001", "Invalid phone number format", "phoneNumber");
// For 500s — NEVER pass ex.Message:
return LambdaResponse.Error(500, "AUTH_007", "Internal server error");
```

### Mobile (Dart) — localized error mapper pattern:
```dart
String localizeAuthError(AppLocalizations l10n, String code) {
  return switch (code) {
    AuthErrorCode.invalidPhoneFormat => l10n.authErrorInvalidPhone,
    AuthErrorCode.invalidPassword => l10n.authErrorInvalidPassword,
    _ => l10n.errorGeneric,
  };
}
```

### Admin (TypeScript) — parse error envelope:
```typescript
try {
  const data = await api.post('/escrow/initiate', body);
} catch (e) {
  if (e instanceof ApiError) {
    const { code, message } = parseErrorBody(e.message);
    const userMessage = t(`errors.${code}`, { defaultValue: t('errors.generic') });
    showToast(userMessage, 'error');
  }
}
```
