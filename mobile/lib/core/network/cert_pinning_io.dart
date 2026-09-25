import 'dart:convert';
import 'dart:io';

import 'package:dio/dio.dart';
import 'package:dio/io.dart';
import 'package:flutter/foundation.dart';

import 'package:wereda_connect/core/network/amazon_trust_roots.dart';

/// Apply certificate pinning on mobile (dart:io available).
///
/// Strategy: **CA pinning**. The HttpClient trusts ONLY the Amazon Trust
/// Services root CAs bundled in [AmazonTrustRoots.pem] (no system roots), so
/// BoringSSL verifies the whole chain (signatures, validity, hostname) against
/// Amazon's roots and rejects certificates issued by any other CA: corporate
/// proxies, rogue CAs, MITM boxes on 3G networks.
///
/// Why not leaf / intermediate SPKI pins (the previous design):
/// - API Gateway and CloudFront rotate leaf certificates every few months
///   (observed 2026-04 -> 2026-06); a stale leaf pin bricked every Android
///   build (SIM-01).
/// - With an empty trust store dart:io hands `badCertificateCallback` only
///   the certificate that failed verification, which is the untrusted ROOT,
///   so a leaf or intermediate pin can never be evaluated there.
/// - Matching a root's SPKI inside the callback would be bypassable: the
///   callback never proves that the leaf actually chains to that root.
///
/// AWS documents this approach (pin to the Amazon Trust Services roots) for
/// API Gateway and CloudFront; the roots expire between 2037 and 2040.
///
/// Rotation runbook: `docs/runbooks/f05-cert-pinning-activation.md`.
/// Live-chain guard: `scripts/check_cert_pins.sh` (runs in mobile-tests.yml).
void applyCertPinning(Dio dio) {
  dio.httpClientAdapter = IOHttpClientAdapter(
    createHttpClient: () {
      // SecurityContext() trusts no system roots; only the bundle below.
      final context = SecurityContext()
        ..setTrustedCertificatesBytes(utf8.encode(AmazonTrustRoots.pem));
      final client = HttpClient(context: context);
      // Reached only when chain validation against the bundled roots failed.
      // Always fail closed; log enough to tell a CA change from an attack.
      client.badCertificateCallback =
          (X509Certificate cert, String host, int port) {
        debugPrint(
          '[CertPinning] REJECTED $host:$port: certificate chain does not '
          'lead to a bundled Amazon Trust Services root '
          '(subject=${cert.subject}, issuer=${cert.issuer}, '
          'valid=${cert.startValidity.toIso8601String()}..'
          '${cert.endValidity.toIso8601String()})',
        );
        return false;
      };
      return client;
    },
  );
}

/// Extract the SubjectPublicKeyInfo (SPKI) byte sequence from a DER-encoded
/// X.509 certificate. Used by tests and tooling to check that the bundled
/// roots still hash to the published pins in [AmazonTrustRoots.spkiSha256].
///
/// ASN.1 structure navigated:
///   Certificate SEQUENCE -> TBSCertificate SEQUENCE ->
///   skip: [version], serialNumber, signature, issuer, validity, subject ->
///   subjectPublicKeyInfo SEQUENCE  <- returned bytes
///
/// Returns null if the DER is malformed or too short.
Uint8List? extractSpki(Uint8List der) {
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

  // version [0] EXPLICIT (optional; present only in v2/v3 certs)
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

  // subjectPublicKeyInfo SEQUENCE: this is the SPKI we want
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
