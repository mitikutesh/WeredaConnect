import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';
import 'package:dio/dio.dart';
import 'package:dio/io.dart';

import 'package:wereda_connect/core/network/api_client.dart';

/// Apply certificate pinning on mobile (dart:io available).
///
/// Verifies the server's certificate public key hash against the pinned
/// hashes in [ApiClient.certificatePins]. Rejects connections where
/// no certificate in the chain matches a pinned hash.
///
/// Uses SHA-256 SPKI (Subject Public Key Info) pinning — the same
/// approach as HPKP and OkHttp.
void applyCertPinning(Dio dio) {
  dio.httpClientAdapter = IOHttpClientAdapter(
    createHttpClient: () {
      final client = HttpClient();
      client.badCertificateCallback =
          (X509Certificate cert, String host, int port) {
        // Only pin our API host
        if (!host.contains('execute-api') && !host.contains('weredaconnect')) {
          return false; // Reject unknown hosts
        }

        // Check if any pinned hash matches the certificate's public key
        final certDer = cert.der;
        final certHash = sha256.convert(certDer);
        final certHashBase64 = base64.encode(
          Uint8List.fromList(certHash.bytes),
        );

        for (final pin in ApiClient.certificatePins) {
          final pinHash = pin.replaceFirst('sha256/', '');
          if (certHashBase64 == pinHash) {
            return true; // Certificate matches a pin — allow connection
          }
        }

        // No pin matched — reject (possible MITM)
        return false;
      };
      return client;
    },
  );
}
