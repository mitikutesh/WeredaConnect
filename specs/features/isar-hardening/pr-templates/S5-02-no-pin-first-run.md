# PR Scaffold — S5-02: No-PIN first-run flow

> **Status:** Design ready · **Depends on:** S5-01 merged
> **Sprint:** HS-5 · **Epic:** E3
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s5-02`
> **Reviewer needed:** UX + 1 mobile dev · **Story size:** 3 pts · **Risk:** Low

The fallback for devices that S5-01 detected have no biometric AND no
PIN enrolled. Surfaces a one-time invitation to set up an OS-level PIN.

---

## 0. TL;DR

When `BiometricGate` throws `NoDeviceAuthAvailable`, the auth flow
routes to a new `SetupPinScreen`. Two CTAs: "Set up PIN now" (launches
the Android Security Settings intent) and "Skip for now" (persists a
flag in SecureStorage so the prompt doesn't reshow every launch).
After "Skip," a persistent warning banner shows on the home screen
until the user sets a PIN.

---

## 1. How to ship this

```bash
git fetch origin
git log origin/main --oneline | grep S5-01

git checkout -b hs5/s5-02-no-pin-first-run origin/main

# Apply:
#   - mobile/lib/features/onboarding/screens/setup_pin_screen.dart   (new)
#   - mobile/lib/features/onboarding/widgets/no_pin_warning_banner.dart (new)
#   - mobile/lib/core/security/device_security_intent.dart           (new — Android intent)
#   - mobile/lib/screens/home_screen.dart                             (show banner if flag set)
#   - mobile/lib/shared/l10n/*.arb                                    (3 new strings)
#   - mobile/test/features/onboarding/setup_pin_screen_test.dart      (widget test)

cd mobile && flutter analyze && flutter test
cd ..

git add ... && git commit -m "feat(mobile): setup-PIN first-run flow for no-biometric devices"
git push -u origin hs5/s5-02-no-pin-first-run
gh pr create --title "feat(mobile): setup-PIN first-run flow [S5-02]"
```

---

## 2. PR description

### Summary

`SetupPinScreen` shown on first launch when `BiometricGate` throws
`NoDeviceAuthAvailable`. Two buttons:

- **"Set up PIN"** — fires `Intent('android.app.action.SET_NEW_PASSWORD')`
  (or `Settings.ACTION_SECURITY_SETTINGS` as fallback). After the
  user returns, retry the gate.
- **"Skip for now"** — writes `device_security_dismissed = true` to
  `FlutterSecureStorage`. The home screen shows a persistent warning
  banner from then on.

Once the user sets a PIN, the next gate check succeeds; the flag is
cleared.

### Why

S5-01 hardens devices with biometric or PIN. The remaining gap is
the ~5% of low-end Android devices (and devices in factory-default
state) that have neither. Without S5-02, those users would be
bricked out of the app. This PR is the recovery path.

### What changes

- `mobile/lib/features/onboarding/screens/setup_pin_screen.dart` —
  Material screen, three localized labels:
  - Title: "Secure your data"
  - Body: "WeredaConnect protects your information with a device PIN.
    Tap Set up PIN to enable it in Settings."
  - Skip CTA: "Skip for now — show this warning on every launch"

- `mobile/lib/features/onboarding/widgets/no_pin_warning_banner.dart`
  — small `MaterialBanner` that wraps the home screen content when
  `device_security_dismissed == true` and the gate is still
  unavailable.

- `mobile/lib/core/security/device_security_intent.dart` — Android
  intent helper:
  ```dart
  Future<bool> launchSecuritySettings() async {
    final intent = AndroidIntent(
      action: 'android.app.action.SET_NEW_PASSWORD',
    );
    return intent.launch();
  }
  ```

- Auth-flow router checks the flag on every launch:
  ```dart
  try {
    await BiometricGate().authenticate(...);
    // Cleared if a PIN was set since last skip
    await _storage.delete(key: 'device_security_dismissed');
  } on NoDeviceAuthAvailable {
    final dismissed = await _storage.read(key: 'device_security_dismissed');
    if (dismissed == 'true') {
      // Skip the setup screen; show banner instead
      return openHomeScreen(showSecurityBanner: true);
    }
    return openSetupPinScreen();
  }
  ```

### What does NOT change

- The biometric gate itself.
- The Cognito auth flow.
- The home-screen layout aside from the optional banner.

### Backwards compatibility

First launch after S5-02 ships AND device has no PIN: setup-PIN
screen shows. Users with biometric or PIN never see it.

### Test plan

Widget tests:
1. Pump `SetupPinScreen` → assert title + two buttons render in each
   of three locales
2. Tap "Skip for now" → assert flag written to SecureStorage; assert
   `Navigator` pops to home
3. Mock the intent helper → tap "Set up PIN" → assert intent fired

### Risk

Low. UX-only.

### Acceptance criteria

- [ ] Shown once on first launch when `LocalAuthentication.isDeviceSupported() == false` or no biometrics enrolled
- [ ] Two CTAs: "Set up PIN now" (launches Android `Settings.ACTION_SECURITY_SETTINGS`) and "Skip for now"
- [ ] Skip persists a flag in `FlutterSecureStorage` so the prompt doesn't re-show every launch
- [ ] Persistent warning banner on home screen until PIN is set
- [ ] Localized

---

## 3. Reviewer guide

1. **Intent launch failure handling.** If the OEM blocks the intent
   (rare but documented on some Huawei builds), fall back to a snackbar
   explaining how to navigate to Settings manually. Confirm the
   fallback path exists.

2. **Banner dismissibility.** The banner is persistent — no dismiss
   button. Confirm by tapping around: the user can't accidentally
   dismiss the warning.

---

## 4. Rollback plan

Revert. No-PIN users hit `UnauthorizedException` from S5-01 with no
recovery → bricked. **Don't merge S5-01 in production without S5-02
also ready to ship.** Treat as paired stories.

---

## 5. Proposed diffs

See §"What changes." Mostly UI scaffolding + one Intent helper +
one router change in the auth flow.

---

## 6. Open questions

1. **Track no-PIN install count via Sentry.** Add a tag
   `device_security_status: none|pin|biometric` on every launch.
   If `none` stays > 10% of installs, revisit the gate strategy.

2. **iOS parity.** iOS hasn't shipped, but when it does, the
   equivalent is `LAContext.canEvaluatePolicy(.deviceOwnerAuthentication)`.
   No iOS device ships without a passcode option, so the no-PIN case
   is rarer there.

---

*PR scaffold v1.0 — 2026-05-13*
