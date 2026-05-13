# PR Scaffold — S6-03: Migration kill switch (remote config)

> **Status:** Design ready · **Depends on:** — (must ship BEFORE HS-3 S3-01)
> **Sprint:** HS-6 · **Epic:** E3
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s6-03`
> **Reviewer needed:** ops + 1 mobile dev
> **Story size:** 3 pts · **Risk:** Low (but blocks HS-3)

⛔ **This PR is a prerequisite for HS-3 S3-01.** Per
`sprint-3-rollout.md` §1 pre-flight, the kill switch must exist AND
be verified in production before the migration ships.

---

## 0. TL;DR

Adds `RemoteConfig` — a thin HTTPS-fetch client that reads
`https://api.weredaconnect.com/config/mobile.json` on app start. The
v2 migration in S3-01 checks `migrationDisabled` before running. PO
can flip the flag server-side to halt rollout within 15 minutes
without a new app release.

---

## 1. How to ship this

```bash
git fetch origin
git checkout -b hs6/s6-03-migration-kill-switch origin/main

# Apply:
#   - mobile/lib/core/config/remote_config.dart        (new)
#   - mobile/lib/core/config/remote_config_cache.dart  (new — SecureStorage)
#   - mobile/lib/app/run_mobile.dart                    (init at startup)
#   - mobile/test/core/config/remote_config_test.dart  (new)
#   - server-side: a static JSON file or Lambda+S3 path (out of scope for this PR,
#                   wire up with infra team before merge)

cd mobile && flutter analyze && flutter test
cd ..

git add ... && git commit -m "feat(mobile): RemoteConfig with migration kill switch"
git push -u origin hs6/s6-03-migration-kill-switch
gh pr create --title "feat(mobile): migration kill switch [S6-03]"

# DEPLOY VERIFICATION BEFORE MERGE INTO HS-3:
#   1. Deploy mobile build to dev
#   2. Confirm RemoteConfig fetches the dev config endpoint
#   3. Flip migration_disabled = true; relaunch app; verify the runner skips
#   4. Flip back; relaunch; verify the runner runs
#   5. Test offline behavior: cached value used; default false applied if no cache
```

---

## 2. PR description

### Summary

`RemoteConfig.instance` is a singleton that:

1. On app start, attempts to fetch `mobile.json` from the API.
2. On success, caches the JSON in `FlutterSecureStorage` with a
   1-hour TTL.
3. On failure or offline, uses the cached value if fresh, else falls
   back to a hardcoded default.

The first flag: `migrationDisabled: bool` — default `false`. If
`true`, `MigrationsRunner.runIfNeeded()` returns early without
running any migrations.

### Why

The HS-3 v2 migration (S3-01) is the highest-risk PR in the roadmap.
If a bug escapes the dev-cohort gate and reaches the 5% staged
rollout, we need a way to stop new installs from running it —
**without requiring a new app release**, which takes hours-to-days
on Play Console.

Remote config is the standard mechanism. A static JSON file served
from API Gateway or S3+CloudFront is sufficient.

### What changes

```dart
// mobile/lib/core/config/remote_config.dart
class RemoteConfig {
  static final RemoteConfig instance = RemoteConfig._();
  RemoteConfig._();

  bool _migrationDisabled = false;
  DateTime? _lastFetch;
  Duration get _ttl => const Duration(hours: 1);

  bool get migrationDisabled => _migrationDisabled;

  Future<void> initialize() async {
    // Try cache first
    final cached = await RemoteConfigCache.read();
    if (cached != null && _isFresh(cached.fetchedAt)) {
      _apply(cached.json);
      return;
    }
    // Try network
    try {
      final response = await _api.get('/config/mobile.json',
          timeout: const Duration(seconds: 2));
      _apply(response.body);
      await RemoteConfigCache.write(response.body, DateTime.now().toUtc());
    } catch (_) {
      // Network failed; use stale cache or defaults
      if (cached != null) _apply(cached.json);
      // else: leave defaults (migrationDisabled = false)
    }
  }

  void _apply(Map<String, dynamic> json) {
    _migrationDisabled = json['migration_disabled'] as bool? ?? false;
  }

  bool _isFresh(DateTime fetchedAt) =>
      DateTime.now().toUtc().difference(fetchedAt) < _ttl;
}
```

`MigrationsRunner` consults it:

```diff
   Future<void> runIfNeeded() async {
+    if (_remoteConfig.migrationDisabled) {
+      debugPrint('[migrations_runner] kill switch active; skipping');
+      return;
+    }
     final version = await _readVersion();
     // ... rest
   }
```

`run_mobile.dart`:

```diff
+await RemoteConfig.instance.initialize();
 await IsarService.instance.initialize();
```

### What does NOT change

- The migration logic.
- The kill-switch is fail-safe — default `false` means migrations
  run; a flipped flag means they skip. A network failure that
  prevents fetching the config doesn't accidentally enable the
  kill switch.

### Server-side

Static JSON file. Two options:

- **A.** S3 + CloudFront. Simple, cheap, fast.
- **B.** Lambda + API Gateway. More flexible (per-user / per-version
  config later), more cost.

Default: **A** for v1. Path: `https://config.weredaconnect.com/mobile.json`.
File contents:

```json
{
  "migration_disabled": false,
  "schema_version": 1
}
```

PO flips the flag by editing the S3 file (which CloudFront caches
for 5 min). Track as a separate infra story before HS-3 ships.

### Test plan

```bash
flutter test test/core/config/remote_config_test.dart
flutter test
```

Required:
1. Fresh fetch, flag `true` → `migrationDisabled` true
2. Fresh fetch, flag `false` → false
3. Network fails, cache fresh → cache used
4. Network fails, no cache → default false
5. Cache stale, network success → re-fetch + new cache

### Risk

Low. The flag is fail-safe. Worst case: a network outage on first
launch means the user runs the migration even though the PO has
flipped the flag — bounded by the 2-second timeout.

### Acceptance criteria

- [ ] On app start, fetch `https://api.weredaconnect.com/config/mobile.json` with 2s timeout
- [ ] On success: cache locally in secure storage with 1-hour TTL
- [ ] On failure: use cached value, else default `{migration_disabled: false}`
- [ ] If `migration_disabled == true`: skip `_migrateToV2`, log warning, continue with legacy reads enabled
- [ ] Flag can also be flipped by editing a hardcoded constant in a hotfix build

---

## 3. Reviewer guide

1. **2-second timeout.** Confirm the fetch is bounded — a slow
   network on cold start mustn't block app launch for more than
   2 seconds.

2. **Fail-safe defaults.** `migrationDisabled = false` is the
   default in every error path. The flag is a kill switch, not an
   enable switch.

3. **Cache TTL discipline.** 1 hour means PO flips take ≤ 1 hour to
   propagate. Acceptable for staged rollout windows (which run 3-7
   days per stage).

---

## 4. Rollback plan

Revert. Migration runs unconditionally. HS-3 cannot proceed without
this PR — treat as paired-with-S3-01.

---

## 5. Proposed diffs

See §"What changes."

---

## 6. Open questions

1. **CloudFront cache invalidation on flag flip.** The 5-min default
   means flag changes take ≤ 5 min to reach mobile clients via CDN
   + ≤ 1 hour via mobile cache. PO call: acceptable. If we need
   faster, set Cache-Control to `max-age=60`.

2. **Authenticated config endpoint?** Currently open. Anyone can
   read the file. Pro: no auth dependency at startup. Con: someone
   could enumerate our flags. Default: open — flags are not PII.

3. **Versioned config.** Should mobile send its app version to a
   /config/mobile/v1.json endpoint? Future-proofing. Out of scope.

---

*PR scaffold v1.0 — 2026-05-13 — MUST ship before HS-3 S3-01*
