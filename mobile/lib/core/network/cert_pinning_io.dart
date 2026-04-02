import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

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
      final context = SecurityContext(withTrustedRoots: false);
      final client = HttpClient(context: context);
      client.badCertificateCallback =
          (X509Certificate cert, String host, int port) {
        // Only pin our API host; reject everything else.
        // Use exact-suffix matching to prevent substring attacks
        // (e.g. malicious-execute-api.evil.com passing a contains() check).
        final isApiGateway = RegExp(
          r'\.execute-api\.[a-z0-9-]+\.amazonaws\.com$',
        ).hasMatch(host);
        final isWeredaConnect = host.endsWith('.weredaconnect.com') ||
            host == 'weredaconnect.com';
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
  try {
    var offset = 0;

    // Certificate SEQUENCE
    if (der[offset] != 0x30) return null;
    offset++;
    offset = _skipDerLength(der, offset).$2;

    // TBSCertificate SEQUENCE
    if (der[offset] != 0x30) return null;
    offset++;
    offset = _skipDerLength(der, offset).$2;

    // version [0] EXPLICIT (optional — present only in v2/v3 certs)
    if (der[offset] == 0xa0) {
      offset = _derSkipElement(der, offset);
    }

    // serialNumber INTEGER
    offset = _derSkipElement(der, offset);
    // signature AlgorithmIdentifier SEQUENCE
    offset = _derSkipElement(der, offset);
    // issuer Name SEQUENCE
    offset = _derSkipElement(der, offset);
    // validity Validity SEQUENCE
    offset = _derSkipElement(der, offset);
    // subject Name SEQUENCE
    offset = _derSkipElement(der, offset);

    // subjectPublicKeyInfo SEQUENCE — this is the SPKI we want
    if (der[offset] != 0x30) return null;
    final spkiStart = offset;
    offset++;
    final (spkiContentLength, afterLength) = _skipDerLength(der, offset);
    final spkiEnd = afterLength + spkiContentLength;
    if (spkiEnd > der.length) return null;

    return der.sublist(spkiStart, spkiEnd);
  } on RangeError {
    return null;
  }
}

/// Parse the DER length at [offset].
/// Returns (content length, offset of content start).
(int, int) _skipDerLength(Uint8List der, int offset) {
  final first = der[offset];
  if (first < 0x80) {
    return (first, offset + 1);
  }
  final numBytes = first & 0x7f;
  var length = 0;
  for (var i = 0; i < numBytes; i++) {
    length = (length << 8) | der[offset + 1 + i];
  }
  return (length, offset + 1 + numBytes);
}

/// Skip the DER element (tag + length + content) at [offset].
/// Returns the offset of the next element.
int _derSkipElement(Uint8List der, int offset) {
  offset++; // skip tag
  final (length, contentStart) = _skipDerLength(der, offset);
  return contentStart + length;
}
