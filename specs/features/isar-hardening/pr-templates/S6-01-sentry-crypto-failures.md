# PR Scaffold — S6-01: Sentry/Crashlytics wiring for crypto failures

> **Status:** Design ready · **Depends on:** S1-02 merged
> **Sprint:** HS-6 · **Epic:** E3 — Observability & ops
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s6-01`
> **Reviewer needed:** observability + 1 mobile dev
> **Story size:** 3 pts · **Risk:** Low

⚠️ **Should ship BEFORE HS-3 (S3-01).** The migration's halt criteria
depend on `FieldDecryptionException` count being visible in Sentry.

---

## 0. TL;DR

Wires Sentry SDK to the `_report` hook in `FieldEncryptor` (currently
`debugPrint`). Every `FieldDecryptionException` and key-integrity
failure surfaces in the Sentry dashboard at warning level.

---

## 1. How to ship this

```bash
git fetch origin
git log origin/main --oneline | grep S1-02

git checkout -b hs6/s6-01-sentry-crypto origin/main

# Apply:
#   - mobile/pubspec.yaml                              (+ sentry_flutter ^8.0.0)
#   - mobile/lib/core/observability/sentry_wrapper.dart (new)
#   - mobile/lib/main.dart / run_mobile.dart           (Sentry.init)
#   - mobile/lib/core/database/field_encryptor.dart    (wire _report)
#   - mobile/lib/core/database/isar_service.dart       (wire KeyIntegrityFailure)
#   - mobile/test/core/observability/sentry_wrapper_test.dart  (new)

cd mobile && flutter pub get && flutter analyze && flutter test
cd ..

git add ... && git commit -m "feat(mobile): wire Sentry for FieldDecryptionException + KeyIntegrityFailure"
git push -u origin hs6/s6-01-sentry-crypto
gh pr create --title "feat(mobile): Sentry wiring for crypto failures [S6-01]"
```

---

## 2. PR description

### Summary

Replaces `_report(reason)` in `FieldEncryptor` (currently a `debugPrint`)
with a Sentry-aware wrapper. Same hook used for `KeyIntegrityFailure`.

Sentry init in `run_mobile.dart`:
```dart
await SentryFlutter.init(
  (options) {
    options.dsn = const String.fromEnvironment('SENTRY_DSN');
    options.environment = const String.fromEnvironment(
        'BUILD_ENV', defaultValue: 'dev');
    options.tracesSampleRate = 0.0;          // no perf transactions in v1
    options.beforeSend = _scrubPii;
  },
  appRunner: () => runApp(const App()),
);
```

`beforeSend` scrubber redacts:
- Any field that looks like a phone number (`+251...`)
- Any field that contains `Bearer ` or `eyJ`
- Any field labeled `rowKey` (per AAD discipline)

### Why

Today:
- `FieldDecryptionException` → `debugPrint` → silenced in release
- `KeyIntegrityFailure` → caller catches, calls `_wipeForIntegrityFailure`
  → no operator notification

For the HS-3 migration rollout (which gates promotion on
`FieldDecryptionException` rate < threshold), we need these events in
a dashboard. `debugPrint` doesn't cut it.

### What changes

- `mobile/pubspec.yaml` — add `sentry_flutter: ^8.0.0` (or current
  stable).
- `mobile/lib/core/observability/sentry_wrapper.dart` — wraps Sentry
  SDK so test code can mock it.
- `mobile/lib/core/database/field_encryptor.dart`:
  ```diff
  void _report(String reason) {
    debugPrint('[FieldEncryptor] $reason');
  - // S6-01: Sentry.captureMessage(reason, level: SentryLevel.warning);
  + SentryWrapper.instance.captureWarning(reason,
  +     category: 'field_decryption');
  }
  ```
- `mobile/lib/core/database/isar_service.dart` (KeyIntegrityFailure
  path):
  ```diff
  } on KeyIntegrityFailure catch (e) {
  + SentryWrapper.instance.captureWarning('key_integrity_failure',
  +     category: 'isar_init',
  +     extra: {'reason': e.reason});
    await _wipeForIntegrityFailure();
    rethrow;
  }
  ```

### What does NOT change

- The exception types or their throw points.
- Migration logic.
- Anything user-visible.

### Backwards compatibility

Sentry SDK is additive. Build without `SENTRY_DSN` → SDK initializes
in no-op mode (events go to a black hole). Production build CI must
inject `SENTRY_DSN` as a secret.

### Test plan

```bash
flutter test test/core/observability/sentry_wrapper_test.dart
flutter test
```

Required:
1. Mock `SentryWrapper`; trigger a `FieldDecryptionException` →
   assert `captureWarning` called with category `field_decryption`
2. Trigger `KeyIntegrityFailure` → assert called with category
   `isar_init`
3. `beforeSend` scrubs a phone-number-shaped string from event extras
4. `beforeSend` scrubs a `Bearer ` token

### Risk

Low. Sentry SDK is mature. Risk areas:
- Quota: estimate ~50 events/day at HS-3 migration tail. Free tier
  covers; paid is $26/month worst-case.
- PII leaks into Sentry: the `beforeSend` scrubber is the second
  line of defense. The first is never including PII in event
  payloads. Reviewer must verify.

### Acceptance criteria

- [ ] Sentry SDK initialized in `main.dart` with DSN from build env
- [ ] `_report(reason)` calls `Sentry.captureMessage(reason, level: SentryLevel.warning)`
- [ ] No raw plaintext or `rowKey` values in the message — only the abstract `reason`
- [ ] Sentry dashboard: alert if `FieldDecryptionException` > 0 per day after S3 migration tail

---

## 3. Reviewer guide

1. **`beforeSend` scrubber.** Read every regex in it. Confirm it
   catches `+251` phone-number format, `Bearer eyJ...` tokens, and
   any field with `password`, `pepper`, `key` in the key name.

2. **No PII at the source.** Walk every `captureWarning` call and
   confirm the arguments contain no plaintext PII. The
   `_report(reason)` calls in S1-02 already pass abstract reasons
   like `'Missing cipher prefix on read for User:displayName'` —
   the column name is fine, the rowKey would not be.

3. **DSN handling.** `SENTRY_DSN` is build-time-injected via
   `--dart-define`. Confirm release-CI sets it from secrets.

---

## 4. Rollback plan

Revert. Reports go back to `debugPrint`-only. HS-3 migration cannot
proceed without observability → HS-3 implicitly gated on S6-01.

---

## 5. Proposed diffs

See §2.

---

## 6. Open questions

1. **Sentry vs. Crashlytics.** Sentry is the default in this PR;
   Firebase Crashlytics is also free. PO decision needed:
   - Sentry: better Dart support, paid tier scales smoothly
   - Crashlytics: free unconditionally, Google ecosystem
   Default: Sentry. Revisit if cost grows.

2. **Should `beforeSend` also scrub `rowKey` even though we don't
   send it?** Belt-and-braces: yes. Costs nothing.

3. **Sample rate on traces.** Set to 0.0 in v1 (no perf monitoring).
   Revisit when traffic justifies it.

---

*PR scaffold v1.0 — 2026-05-13 — ship before HS-3*
