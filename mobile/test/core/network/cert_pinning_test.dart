@TestOn('vm')
library;

import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';
import 'package:dio/dio.dart';
import 'package:dio/io.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/auth/token_refresh.dart';
import 'package:wereda_connect/core/network/amazon_trust_roots.dart';
import 'package:wereda_connect/core/network/cert_pinning_io.dart';

/// Guards the CA-pinning trust bundle (SIM-01). A tampered, truncated or
/// wrongly regenerated bundle would either brick every Android build (no
/// trusted root -> every request rejected) or silently widen trust.
void main() {
  final pemBlock = RegExp(
    r'-----BEGIN CERTIFICATE-----([\s\S]*?)-----END CERTIFICATE-----',
  );

  List<List<int>> derBlocks() => pemBlock
      .allMatches(AmazonTrustRoots.pem)
      .map((m) => base64.decode(m.group(1)!.replaceAll(RegExp(r'\s'), '')))
      .toList();

  group('AmazonTrustRoots bundle', () {
    test('contains exactly the five published Amazon Trust Services roots', () {
      final ders = derBlocks();
      expect(ders, hasLength(5));

      final hashes = ders.map((der) {
        final spki = extractSpki(Uint8List.fromList(der));
        expect(spki, isNotNull, reason: 'root DER must parse');
        return base64.encode(sha256.convert(spki!).bytes);
      }).toSet();

      expect(hashes, equals(AmazonTrustRoots.spkiSha256.values.toSet()));
    });

    test('includes Amazon Root CA 1 (the root every AWS chain leads to today)',
        () {
      expect(
        AmazonTrustRoots.spkiSha256['Amazon Root CA 1'],
        '++MBgDH5WGvL9Bcn5Be30cRcL0f5O+NyoXuWtQdX1aI=',
      );
    });

    test('is accepted by SecurityContext as a trust store', () {
      final context = SecurityContext();
      expect(
        () => context
            .setTrustedCertificatesBytes(utf8.encode(AmazonTrustRoots.pem)),
        returnsNormally,
      );
    });
  });

  group('applyCertPinning', () {
    test('installs an IO adapter whose client builds with the pinned roots',
        () {
      final dio = Dio();
      applyCertPinning(dio);

      expect(dio.httpClientAdapter, isA<IOHttpClientAdapter>());
      final adapter = dio.httpClientAdapter as IOHttpClientAdapter;
      // Building the client applies the trust bundle to a SecurityContext;
      // a malformed bundle would throw here.
      final client = adapter.createHttpClient!();
      expect(client, isA<HttpClient>());
      client.close(force: true);
    });
  });

  // MOB-02: the refresh token used to go out on a separate plain Dio that
  // trusted the system roots.
  group('buildRefreshDio', () {
    Dio pinnedApiDio() {
      final dio = Dio(
        BaseOptions(
          baseUrl: 'https://api.test',
          connectTimeout: const Duration(seconds: 7),
          receiveTimeout: const Duration(seconds: 9),
          headers: {'X-App-Build': '42'},
        ),
      );
      applyCertPinning(dio);
      return dio;
    }

    test('sends the refresh call through the API client\'s pinned adapter', () {
      final apiDio = pinnedApiDio();

      final refreshDio = buildRefreshDio(apiDio);

      expect(refreshDio.httpClientAdapter, isA<IOHttpClientAdapter>());
      expect(
        refreshDio.httpClientAdapter,
        same(apiDio.httpClientAdapter),
        reason: 'a separate adapter would fall back to the system trust store',
      );
      final adapter = refreshDio.httpClientAdapter as IOHttpClientAdapter;
      expect(adapter.createHttpClient, isNotNull);
    });

    test('copies the base configuration and adds only the given interceptors',
        () {
      final apiDio = pinnedApiDio();
      final apiOnly = InterceptorsWrapper();
      apiDio.interceptors.add(apiOnly);
      final extra = InterceptorsWrapper();

      final refreshDio = buildRefreshDio(apiDio, interceptors: [extra]);

      expect(refreshDio.options.baseUrl, 'https://api.test');
      expect(refreshDio.options.connectTimeout, const Duration(seconds: 7));
      expect(refreshDio.options.receiveTimeout, const Duration(seconds: 9));
      expect(refreshDio.options.headers['X-App-Build'], '42');
      expect(refreshDio.interceptors, contains(extra));
      expect(refreshDio.interceptors, isNot(contains(apiOnly)));
    });
  });

  group('extractSpki', () {
    test('returns null for malformed DER', () {
      expect(extractSpki(Uint8List.fromList([0x30])), isNull);
      expect(extractSpki(Uint8List.fromList([0x02, 0x01, 0x00])), isNull);
      expect(extractSpki(Uint8List(0)), isNull);
    });
  });
}
