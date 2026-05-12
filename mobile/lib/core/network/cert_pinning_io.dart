import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:dio/dio.dart';
import 'package:dio/io.dart';
import 'package:flutter/foundation.dart';

import 'package:wereda_connect/core/network/api_client.dart';

/// Apply certificate pinning on mobile (dart:io available).
///
/// Uses SHA-256 SPKI (Subject Public Key Info) pinning — the same approach
/// as HPKP and OkHttp. Pins are stored in [ApiClient.certificatePins] and
/// were extracted using:
///   openssl s_client ... | openssl x509 -pubkey -noout | openssl dgst -sha256
///
/// Implementation: We use SecurityContext(withTrustedRoots: false) so that
/// ALL server certificates appear invalid to the OS, forcing every connection
/// through badCertificateCallback. This is the only way to enforce pinning
/// for CA-signed certificates in dart:io — without it, valid CA-signed certs
/// are accepted before the callback can run.
void applyCertPinning(Dio dio) {
  dio.httpClientAdapter = IOHttpClientAdapter(
    createHttpClient: () {
      // withTrustedRoots: false → no system CAs are trusted → every cert
      // triggers badCertificateCallback, giving us the hook for pin checking.
      final context =
          SecurityContext(); // withTrustedRoots defaults to false — no system CAs trusted
      final client = HttpClient(context: context);
      client.badCertificateCallback =
          (X509Certificate cert, String host, int port) {
        // Only pin our API host; reject everything else.
        // Use exact-suffix matching to prevent substring attacks
        // (e.g. malicious-execute-api.evil.com passing a contains() check).
        final isApiGateway = RegExp(
          r'\.execute-api\.[a-z0-9-]+\.amazonaws\.com$',
        ).hasMatch(host);
        final isWeredaConnect =
            host.endsWith('.weredaconnect.com') || host == 'weredaconnect.com';
        if (!isApiGateway && !isWeredaConnect) {
          debugPrint('[CertPinning] REJECTED unknown host: $host:$port');
          return false;
        }

        // Extract the SubjectPublicKeyInfo (SPKI) from the DER-encoded cert
        // and hash it. This matches pins produced by `openssl x509 -pubkey`.
        final spki = _extractSpki(cert.der);
        if (spki == null) {
          debugPrint(
            '[CertPinning] REJECTED $host:$port — could not parse SPKI',
          );
          return false;
        }

        final spkiHash = sha256.convert(spki);
        final spkiHashBase64 = base64.encode(spkiHash.bytes);

        for (final pin in ApiClient.certificatePins) {
          final pinHash = pin.replaceFirst('sha256/', '');
          if (spkiHashBase64 == pinHash) {
            return true; // Pin matched — allow connection
          }
        }

        // No pin matched — possible MITM attack.
        debugPrint(
          '[CertPinning] REJECTED $host:$port — no pin matched '
          '(hash=$spkiHashBase64)',
        );
        return false;
      };
      return client;
    },
  );
}

/// Extract the SubjectPublicKeyInfo (SPKI) byte sequence from a DER-encoded
/// X.509 certificate.
///
/// ASN.1 structure navigated:
///   Certificate SEQUENCE → TBSCertificate SEQUENCE →
///   skip: [version], serialNumber, signature, issuer, validity, subject →
///   subjectPublicKeyInfo SEQUENCE  ← returned bytes
///
/// Returns null if the DER is malformed or too short.
Uint8List? _extractSpki(Uint8List der) {
  var offset = 0;

  // Certificate SEQUENCE
  if (offset >= der.length || der[offset] != 0x30) return null;
  offset++;
  final s1 = _skipDerLength(der, offset);
  if (s1 == null) return null;
  offset = s1.$2;

  // TBSCertificate SEQUENCE
  if (offset >= der.length || der[offset] != 0x30) return null;
  offset++;
  final s2 = _skipDerLength(der, offset);
  if (s2 == null) return null;
  offset = s2.$2;

  // version [0] EXPLICIT (optional — present only in v2/v3 certs)
  if (offset >= der.length) return null;
  if (der[offset] == 0xa0) {
    final next = _derSkipElement(der, offset);
    if (next == null) return null;
    offset = next;
  }

  // serialNumber, signature, issuer, validity, subject
  for (var i = 0; i < 5; i++) {
    final next = _derSkipElement(der, offset);
    if (next == null) return null;
    offset = next;
  }

  // subjectPublicKeyInfo SEQUENCE — this is the SPKI we want
  if (offset >= der.length || der[offset] != 0x30) return null;
  final spkiStart = offset;
  offset++;
  final s3 = _skipDerLength(der, offset);
  if (s3 == null) return null;
  final spkiEnd = s3.$2 + s3.$1;
  if (spkiEnd > der.length) return null;

  return der.sublist(spkiStart, spkiEnd);
}

/// Parse the DER length at [offset]; returns null if out of bounds.
(int, int)? _skipDerLength(Uint8List der, int offset) {
  if (offset >= der.length) return null;
  final first = der[offset];
  if (first < 0x80) return (first, offset + 1);
  final numBytes = first & 0x7f;
  if (offset + 1 + numBytes > der.length) return null;
  var length = 0;
  for (var i = 0; i < numBytes; i++) {
    length = (length << 8) | der[offset + 1 + i];
  }
  return (length, offset + 1 + numBytes);
}

/// Skip the DER element at [offset]; returns next offset or null if malformed.
int? _derSkipElement(Uint8List der, int offset) {
  if (offset >= der.length) return null;
  offset++; // skip tag
  final result = _skipDerLength(der, offset);
  if (result == null) return null;
  return result.$2 + result.$1;
}
