# PR Scaffold — S7-02: Android Keystore platform channel (stretch)

> **Status:** Design ready · **Depends on:** S7-01 merged
> **Sprint:** HS-7 · **Epic:** E4 · **Closes HS-7 and the roadmap**
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s7-02`
> **Reviewer needed:** security + Android dev · **Story size:** 5 pts · **Risk:** Medium
> **STRETCH:** safe to defer to Q4 if HS-7 capacity runs short.

Routes AES encrypt/decrypt through a Kotlin platform channel into
Android Keystore. The raw 32-byte AES key never enters the Dart heap;
the channel exposes only encrypt/decrypt operations on opaque key
aliases.

---

## 0. TL;DR

A new Kotlin `MethodChannel` `wereda/keystore_crypto` exposes two
methods: `encrypt(aad, plaintext)` and `decrypt(aad, ciphertext)`.
Both operate on an Android Keystore-resident key (aliased
`wereda_aes_key_v1`). The key is generated inside the TEE and never
exfiltrated.

Dart-side `FieldEncryptor` delegates AES ops to the channel when
available; falls back to in-Dart `package:cryptography` on API < 23 or
unsupported devices.

---

## 1. How to ship this

```bash
git fetch origin
git log origin/main --oneline | grep S7-01

git checkout -b hs7/s7-02-keystore-platform-channel origin/main

# Apply:
#   - mobile/android/app/src/main/kotlin/com/weredaconnect/KeystoreCrypto.kt    (new)
#   - mobile/android/app/src/main/kotlin/com/weredaconnect/MainActivity.kt      (register channel)
#   - mobile/lib/core/database/keystore_crypto_channel.dart                      (new — Dart side)
#   - mobile/lib/core/database/field_encryptor.dart                              (delegate via channel)
#   - mobile/test/core/database/keystore_crypto_channel_test.dart                (new — mocked channel)
#   - mobile/integration_test/keystore_integration_test.dart                     (new — runs on device)

cd mobile && flutter analyze && flutter test
# Run integration test on a real Android device
flutter test integration_test/keystore_integration_test.dart -d <android-device-id>
cd ..

git add ... && git commit -m "feat(mobile): route AES through Android Keystore platform channel"
git push -u origin hs7/s7-02-keystore-platform-channel
gh pr create --title "feat(mobile): Android Keystore platform channel [S7-02]"
```

---

## 2. PR description

### Summary

Two new files:

1. **Kotlin: `KeystoreCrypto.kt`** — defines a `MethodChannel` named
   `wereda/keystore_crypto`. Methods: `encrypt(aad, plaintext)`,
   `decrypt(aad, ciphertext)`. Key alias: `wereda_aes_key_v1`. Key
   spec: AES-256-GCM, `KeyProperties.PURPOSE_ENCRYPT | DECRYPT`,
   `BLOCK_MODE_GCM`, `ENCRYPTION_PADDING_NONE`.

2. **Dart: `KeystoreCryptoChannel`** — thin wrapper exposing
   `Future<Uint8List> encrypt(...)` and `Future<Uint8List> decrypt(...)`.

`FieldEncryptor` checks `KeystoreCryptoChannel.isAvailable()` at
`initialize()` time. If yes, route every encrypt/decrypt through the
channel. If no (API < 23, iOS, web), fall back to the existing
in-Dart `package:cryptography` path.

### Why

The AES key currently lives as a 32-byte `List<int>` in Dart heap.
Two threats remain:

- **Heap dump on rooted device.** `/proc/<pid>/mem` exposes the
  process memory; the key bytes are recoverable.
- **Cold-boot DRAM extraction.** Same idea — bytes in RAM until GC
  cleans up.

Android Keystore, when available, generates the key inside the TEE
(Trusted Execution Environment) and never exposes the bytes to
userspace. Dart side gets only the key *alias* and uses it via
platform-channel calls. AES operations happen in the TEE.

This is the strongest defense against device-level key extraction.

### What changes

#### Kotlin (`KeystoreCrypto.kt`)

```kotlin
package com.weredaconnect

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import io.flutter.plugin.common.MethodChannel
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.spec.GCMParameterSpec

private const val ALIAS = "wereda_aes_key_v1"
private const val ANDROID_KEYSTORE = "AndroidKeyStore"

class KeystoreCryptoHandler : MethodChannel.MethodCallHandler {
    override fun onMethodCall(call: MethodCall, result: MethodChannel.Result) {
        try {
            when (call.method) {
                "encrypt" -> result.success(encrypt(
                    call.argument<ByteArray>("aad")!!,
                    call.argument<ByteArray>("plaintext")!!,
                ))
                "decrypt" -> result.success(decrypt(
                    call.argument<ByteArray>("aad")!!,
                    call.argument<ByteArray>("ciphertext")!!,
                ))
                "isAvailable" -> result.success(isAvailable())
                else -> result.notImplemented()
            }
        } catch (e: Exception) {
            result.error("KEYSTORE_ERROR", e.message, null)
        }
    }

    private fun getOrCreateKey(): SecretKey {
        val ks = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        if (ks.containsAlias(ALIAS)) return (ks.getEntry(ALIAS, null) as KeyStore.SecretKeyEntry).secretKey

        val kg = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE)
        kg.init(KeyGenParameterSpec.Builder(
            ALIAS,
            KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
        )
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
            .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
            .setKeySize(256)
            .setUserAuthenticationRequired(false)  // biometric gate is at app-open (S5-01)
            .build())
        return kg.generateKey()
    }

    private fun encrypt(aad: ByteArray, plaintext: ByteArray): ByteArray {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply {
            init(Cipher.ENCRYPT_MODE, getOrCreateKey())
            updateAAD(aad)
        }
        val ct = cipher.doFinal(plaintext)
        val iv = cipher.iv          // 12 bytes
        return iv + ct              // ct already has the 16-byte tag appended
    }

    private fun decrypt(aad: ByteArray, ciphertext: ByteArray): ByteArray {
        val iv = ciphertext.copyOfRange(0, 12)
        val body = ciphertext.copyOfRange(12, ciphertext.size)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply {
            init(Cipher.DECRYPT_MODE, getOrCreateKey(), GCMParameterSpec(128, iv))
            updateAAD(aad)
        }
        return cipher.doFinal(body)
    }

    private fun isAvailable(): Boolean = android.os.Build.VERSION.SDK_INT >= 23
}
```

Register in `MainActivity.kt`:

```kotlin
override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
    super.configureFlutterEngine(flutterEngine)
    MethodChannel(flutterEngine.dartExecutor.binaryMessenger,
        "wereda/keystore_crypto").setMethodCallHandler(KeystoreCryptoHandler())
}
```

#### Dart

```dart
class KeystoreCryptoChannel {
  static const _ch = MethodChannel('wereda/keystore_crypto');

  static Future<bool> isAvailable() async {
    try {
      return await _ch.invokeMethod<bool>('isAvailable') ?? false;
    } on PlatformException {
      return false;
    }
  }

  static Future<Uint8List> encrypt(Uint8List aad, Uint8List plaintext) async {
    final result = await _ch.invokeMethod<Uint8List>('encrypt', {
      'aad': aad, 'plaintext': plaintext,
    });
    return result!;
  }

  static Future<Uint8List> decrypt(Uint8List aad, Uint8List ciphertext) async {
    final result = await _ch.invokeMethod<Uint8List>('decrypt', {
      'aad': aad, 'ciphertext': ciphertext,
    });
    return result!;
  }
}
```

`FieldEncryptor`:

```dart
bool _useKeystoreChannel = false;

void initialize(List<int> keyBytes, {bool legacyPlaintextAllowed = false}) async {
  _useKeystoreChannel = await KeystoreCryptoChannel.isAvailable();
  if (_useKeystoreChannel) {
    // Keystore generated/managed the key itself; we don't need keyBytes
    debugPrint('[FieldEncryptor] using Android Keystore platform channel');
  } else {
    // Fall back to in-Dart AES-GCM (existing path)
    _secretKey = SecretKey(Uint8List.fromList(keyBytes));
  }
  _legacyPlaintextAllowed = legacyPlaintextAllowed;
  _initialized = true;
}

Future<String> encrypt(String plaintext, {...AAD args}) async {
  // ... existing prefix + AAD assembly
  if (_useKeystoreChannel) {
    final ct = await KeystoreCryptoChannel.encrypt(
      Uint8List.fromList(utf8.encode(_aad(collection, column, rowKey))),
      Uint8List.fromList(utf8.encode(plaintext)),
    );
    return '$cipherPrefix${base64Encode(ct)}';
  }
  // ... fall back to in-Dart AES
}
```

### What does NOT change

- AAD format. Same trio, same encoding.
- Ciphertext on disk: same `enc:v1:<base64(iv+ct+tag)>` shape. The
  bytes are produced by Keystore instead of `package:cryptography`,
  but they're interchangeable AES-GCM.
- iOS, web, desktop — fall back to in-Dart path.

### Backwards compatibility

**Existing installs:** the on-disk AES key (32 bytes in secure storage)
was generated by `Random.secure()` and lives outside Keystore. After
S7-02 ships, two options:

- **A. Keep existing installs on the in-Dart path.** New installs
  use Keystore. Migration: a follow-up PR that decrypts everything
  with the old key, generates a Keystore key, re-encrypts everything.
- **B. Migrate on next launch.** S7-02 itself triggers a one-time
  re-encryption from in-Dart-key to Keystore-key, using
  `rotateEncryptionKey()` from S7-01.

Default: **A**. The migration is a chunk of work; ship the capability
first; migrate existing installs in a follow-up if/when we judge
the in-Dart key materially weaker.

### Test plan

Unit (mocked channel):
1. `KeystoreCryptoChannel.isAvailable()` → routes to keystore path
2. `isAvailable()` returns false → routes to fallback path
3. Encrypt + decrypt round-trip via mocked channel returns plaintext

Integration (real Android device, `integration_test/`):
1. Encrypt + decrypt round-trip on a Pixel (API 33)
2. Same on a Tecno Spark 8 (API 28)
3. `adb shell` cannot dump the key bytes (sanity check —
   `/proc/<pid>/mem` should not contain the key)

iOS / web: skip (channel returns false on those platforms).

### Threat model

| Threat | Pre-PR | Post-PR (on supported Android) |
|---|---|---|
| Heap dump on rooted device extracts AES key | Bytes recoverable | Bytes never in heap; only alias |
| Cold-boot DRAM extraction | Bytes recoverable | Same as above |
| User physically extracts SoC, dumps secure-storage entry | (depends on hardware) | Key non-exportable from TEE |

### Risk

Medium:

1. **OEM divergence.** Some Android OEMs (Huawei, Xiaomi) have
   stricter Keystore quirks. Test on 3+ devices.

2. **Channel performance.** Crossing Dart ↔ Kotlin twice per
   encrypt/decrypt adds ~1ms per op. For 100-decrypt streams,
   that's noticeable. Mitigation: batch the calls.

3. **Migration story.** Existing installs need to switch to
   Keystore at some point. Follow-up PR.

### Acceptance criteria

- [ ] Kotlin: `KeyGenParameterSpec` with `KEY_PROPERTIES_KEY_ALGORITHM_AES`, `BLOCK_MODE_GCM`, `ENCRYPTION_PADDING_NONE`, `setUserAuthenticationRequired(false)`
- [ ] Dart side never sees raw key bytes — only the alias `wereda_aes_key_v1`
- [ ] `FieldEncryptor.encrypt/decrypt` calls the platform channel for AES, keeps AAD/prefix logic in Dart
- [ ] Fallback to in-Dart AES if API level < 23 (or any platform-channel failure) with telemetry warning
- [ ] iOS NOT in scope this story

---

## 3. Reviewer guide

1. **`updateAAD` order.** Must be called after `init()` but before
   any `update()` or `doFinal()`. Confirm in the Kotlin code.

2. **AES-GCM tag length.** Default is 128 bits (16 bytes). Explicit
   in `GCMParameterSpec(128, iv)`. Don't accept the default — set it.

3. **`setUserAuthenticationRequired(false).`** This is intentional.
   Biometric gating sits at app-open in S5-01. If we made Keystore
   require auth, every encrypt/decrypt would prompt — unusable for
   streaming reads.

---

## 4. Rollback plan

`git revert <merge-commit>`. Channel goes away; `FieldEncryptor`
always uses the in-Dart path. Existing-install behavior unchanged
(they were on the in-Dart path anyway).

Devices that started on Keystore (fresh installs post-S7-02) now
read garbage — the Keystore-encrypted ciphertext is unreadable to
the in-Dart path with the secure-storage key. **Migration
PR-pair this story** with a "fallback to keystore-channel-only"
mode if revert is contemplated.

---

## 5. Proposed diffs

See §"What changes." Kotlin + Dart + channel registration.

---

## 6. Open questions

1. **iOS Secure Enclave parity.** iOS hasn't shipped. When it does,
   add an iOS native module that exposes the same `MethodChannel`
   name; channel layer becomes platform-agnostic. Track separately.

2. **Key rotation in Keystore.** Generating a new key alias (e.g.,
   `wereda_aes_key_v2`) lets us rotate. Coordinate with S7-01: when
   rotation triggers, generate the new Keystore alias, re-encrypt
   all rows, delete the old alias. Not in this PR.

3. **Migration story for existing installs.** §"Backwards
   compatibility" defers it. Track as a final story `S7-03` if/when
   we ship Keystore broadly.

---

*PR scaffold v1.0 — 2026-05-13 — closes HS-7 and the roadmap*
