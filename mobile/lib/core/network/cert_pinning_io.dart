import 'dart:io';

import 'package:dio/dio.dart';
import 'package:dio/io.dart';

/// Apply certificate pinning on mobile (dart:io available).
void applyCertPinning(Dio dio) {
  dio.httpClientAdapter = IOHttpClientAdapter(
    createHttpClient: () {
      final client = HttpClient();
      client.badCertificateCallback = (cert, host, port) {
        // TODO: Implement actual certificate pinning verification
        // For now, return false to reject all certificates
        // when pinning is enabled (prevents accidental production use)
        return false;
      };
      return client;
    },
  );
}
