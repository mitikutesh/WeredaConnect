# PR Scaffold — S5-01: Biometric authentication before key load

> **Status:** Design ready · **Depends on:** HS-3 complete
> **Sprint:** HS-5 · **Epic:** E3 — Observability & ops
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s5-01`
> **Reviewer needed:** security + UX + 1 mobile dev
> **Story size:** 5 pts · **Risk:** Medium (UX impact on no-PIN devices)

Requires biometric or device PIN before `FieldEncryptor.initialize`
loads the AES key. A lost or stolen phone with no app PIN is the
realistic adversary; this PR closes that surface.

---

## 0. TL;DR

`LocalAuthentication.authenticate(...)` runs inside
`IsarService.initialize()` BEFORE `FieldEncryptor.initialize(keyBytes)`.
Fail → throw `UnauthorizedException`; do not load the key. The app
shows a "Unlock" screen instead of the dashboard.

Devices with no biometric and no enrolled PIN see a first-run flow
(S5-02) inviting them to set up a PIN; if they decline, a persistent
warning banner shows on the home screen.

---

## 1. How to ship this

```bash
git fetch origin
git checkout -b hs5/s5-01-biometric-gate origin/main

# Apply:
#   - mobile/lib/core/security/biometric_gate.dart      (new)
#   - mobile/lib/core/database/isar_service.dart        (call gate before key load)
#   - mobile/lib/shared/l10n/app_en.arb                 (+ unlock strings)
#   - mobile/lib/shared/l10n/app_am.arb / app_om.arb     (+ translations)
#   - mobile/test/core/security/biometric_gate_test.dart (new — mocked LocalAuth)

cd mobile && flutter analyze && flutter test
cd ..

git add ... && git commit -m "feat(mobile): biometric/PIN gate before AES key load"
git push -u origin hs5/s5-01-biometric-gate
gh pr create --title "feat(mobile): biometric gate [S5-01]"
```

---

## 2. PR description

### Summary

Adds `BiometricGate.authenticate()` called from `IsarService.initialize()`
before key load. Uses `local_auth: ^2.1.0` already in `pubspec.yaml`.

```dart
class BiometricGate {
  Future<void> authenticate({required String localizedReason}) async {
    final auth = LocalAuthentication();
    final canCheck = await auth.canCheckBiometrics ||
                     await auth.isDeviceSupported();
    if (!canCheck) {
      // No biometric and no PIN. Surface to UX layer (see S5-02).
      throw NoDeviceAuthAvailable();
    }
    final ok = await auth.authenticate(
      localizedReason: localizedReason,
      options: const AuthenticationOptions(
        biometricOnly: false,         // PIN fallback acceptable
        stickyAuth: true,
        useErrorDialogs: true,
      ),
    );
    if (!ok) throw const UnauthorizedException('Biometric auth failed');
  }
}
```

`IsarService.initialize()`:

```diff
   Future<Isar> initialize() async {
     if (_isar != null) return _isar!;

     PlatformGuard.assertNotWeb();

+    // Block before any key material is loaded.
+    await BiometricGate().authenticate(
+      localizedReason: AppLocalizations.of(navigatorKey.currentContext!)!
+          .unlockWeredaConnect,
+    );

     final dir = await getApplicationDocumentsDirectory();
     final keyBytes = await _getEncryptionKey();
     FieldEncryptor.instance.initialize(keyBytes);
     // ... rest unchanged
   }
```

### Why

`docs/mobile/isar-implementation.md` §7 threat-model table row 1:
"Lost-phone finder, no PIN — opens app." Today, picking up an unlocked
device opens the app to the dashboard with full Isar access. With the
gate, the finder hits a biometric/PIN prompt and cannot proceed.

For rural Ethiopian users on devices that may not have biometric
hardware, the OS-level PIN fallback covers the gap. For the smallest
slice with neither, S5-02 prompts them to set up a PIN.

### What changes

- `mobile/lib/core/security/biometric_gate.dart` — new.
- `mobile/lib/core/database/isar_service.dart` — call the gate.
- `mobile/lib/shared/l10n/` — three localized prompt strings:
  Amharic, Afan Oromo, English.
- Tests with mocked `LocalAuthentication`.

### What does NOT change

- The AES key handling.
- Anything about the auth/Cognito flow (this is a SECOND auth on top
  of Cognito — device-presence verification).
- iOS code paths — `local_auth` covers iOS too but iOS hasn't shipped.

### Backwards compatibility

First launch after S5-01 ships: users see the unlock prompt. If
their device has biometric → swipe finger → in. If it has only PIN
→ enter PIN → in. If it has neither (rare on Android 8+) →
`NoDeviceAuthAvailable` thrown, S5-02 first-run flow takes over.

### Test plan

```bash
flutter test test/core/security/biometric_gate_test.dart
flutter test
```

Mock `LocalAuthentication`:
1. `canCheckBiometrics = true`, `authenticate → true` → `initialize`
   completes normally.
2. `canCheckBiometrics = true`, `authenticate → false` → throws
   `UnauthorizedException`. Key not loaded.
3. `canCheckBiometrics = false`, `isDeviceSupported = false` →
   throws `NoDeviceAuthAvailable`. Caller (auth flow) routes to S5-02.

**Manual on a real device** (post-merge, pre-staged-rollout):
- Pixel with fingerprint: prompt → swipe → app opens
- Cheap Android with PIN only: prompt → enter PIN → app opens
- Cheap Android with no PIN: prompt fails → setup-PIN screen shows

### Threat model

| Threat | Pre-PR | Post-PR |
|---|---|---|
| Found unlocked phone, attacker opens app | Reads all PII immediately | Hits biometric prompt; cannot proceed |
| Sleeping phone, attacker boots it | Locked at OS level until PIN/biometric | Same + app gate |
| Adversary observes user enter PIN once, takes phone | Same access | Same access (PIN-leak is OS-level threat) |

### Risk

Medium. UX impact:

- Cold-start latency: +1-2s for biometric prompt.
- First-launch friction: users may be confused by an additional
  unlock step on top of Cognito login.
- Devices with no PIN AND no biometric: the S5-02 fallback path is
  the recovery — without it, users are bricked.

### Acceptance criteria

- [ ] `LocalAuthentication.authenticate(...)` runs before `FieldEncryptor.initialize`
- [ ] Fallback to device PIN if no biometric enrolled
- [ ] On failure: throw `UnauthorizedException`, do not load key
- [ ] On no-biometric-available + no-PIN-set: surface a first-run flow inviting the user to set up a PIN (S5-02), fall back to no-gate with persistent warning banner
- [ ] Localization: prompt in Amharic, Afan Oromo, English

---

## 3. Reviewer guide

1. **`AndroidOptions(stickyAuth: true)`.** Verify it doesn't keep
   the auth alive across cold starts. Per `local_auth` docs, sticky
   only applies to backgrounding within the same session.

2. **Don't load the key on failure.** Even on
   `UnauthorizedException`, `FieldEncryptor` should remain
   uninitialized. Confirm the early return in `initialize()`.

3. **Localization keys present in all three .arb files.** Run
   `flutter gen-l10n` and confirm no missing-translation warnings.

---

## 4. Rollback plan

`git revert <merge-commit>`. Gate removed; cold-start latency returns
to baseline.

Feature flag option: `enableBiometricGate` (RemoteConfig). Default
`true`. Flip off for emergency rollback without code revert.

---

## 5. Proposed diffs

See §2. Three small new files (gate, test, l10n strings).

---

## 6. Open questions

1. **OS-level PIN strength.** Android allows PINs as short as 4
   digits. Should we require a minimum? Default: no — that's an OS
   setting, out of our control.

2. **Re-prompt on app resume after long background?** Currently only
   on cold start. For high-sensitivity flows (Telebirr payment), maybe
   re-prompt. Default: no for v1; revisit.

3. **`String.fromEnvironment('BIOMETRIC_PROMPT_TIMEOUT_SECONDS', defaultValue: '30')`.**
   Should there be a timeout on the prompt? Default: rely on OS
   default (~30s).

---

*PR scaffold v1.0 — 2026-05-13*
