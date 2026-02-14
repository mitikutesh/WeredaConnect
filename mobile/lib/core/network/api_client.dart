import 'dart:io';

import 'package:dio/dio.dart';
import 'package:dio/io.dart';

/// Singleton HTTP client with certificate pinning for secure API communication.
///
/// This client provides:
/// - Certificate pinning to prevent MitM attacks on Ethiopian 3G networks
/// - Standardized timeout configuration (30s for rural 3G)
/// - JSON content negotiation
///
/// SECURITY NOTE:
/// Certificate pinning is currently DISABLED (foundation only).
/// Before production deployment:
/// 1. Deploy F-16 Lambda API Gateway
/// 2. Extract actual certificate SHA-256 hashes
/// 3. Update [certificatePins] with real values
/// 4. Uncomment certificate pinning code in [initialize]
/// 5. Test with production API
///
/// Ethiopian context:
/// - 3G networks are vulnerable to MitM attacks
/// - Certificate pinning is critical for auth token security
/// - 30s timeouts account for slow rural connectivity
class ApiClient {
  static final ApiClient _instance = ApiClient._internal();
  factory ApiClient() => _instance;
  ApiClient._internal();

  late final Dio dio;

  /// Pinned SHA-256 certificate hashes for API Gateway.
  ///
  /// TODO: Replace with actual certificate hashes after F-16 deployment.
  /// See /specs/decisions/cert-pinning.md for extraction procedure.
  ///
  /// Format: 'sha256/BASE64_ENCODED_HASH'
  /// Example: 'sha256/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA='
  static const List<String> certificatePins = [
    'sha256/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=', // Primary cert (placeholder)
    'sha256/BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=', // Backup cert (placeholder)
  ];

  /// Initialize Dio client with base URL and optional certificate pinning.
  ///
  /// Params:
  /// - [baseUrl]: API Gateway base URL (e.g., https://api.weredaconnect.et)
  /// - [enableCertPinning]: Whether to enable certificate pinning (default: false)
  ///
  /// IMPORTANT: Do not enable cert pinning until real certificates are deployed!
  void initialize(String baseUrl, {bool enableCertPinning = false}) {
    dio = Dio(
      BaseOptions(
        baseUrl: baseUrl,
        connectTimeout: const Duration(seconds: 30),
        receiveTimeout: const Duration(seconds: 30),
        headers: {
          'Content-Type': 'application/json',
          'Accept': 'application/json',
        },
      ),
    );

    // Certificate pinning (foundation only - disabled by default)
    if (enableCertPinning) {
      dio.httpClientAdapter = IOHttpClientAdapter(
        createHttpClient: () {
          final client = HttpClient();
          client.badCertificateCallback = (cert, host, port) {
            // TODO: Implement actual certificate pinning verification
            // This requires:
            // 1. Extract certificate DER bytes: cert.der
            // 2. Compute SHA-256 hash using package:crypto
            // 3. Base64 encode the hash
            // 4. Compare with certificatePins
            //
            // For now, return false to reject all certificates
            // when pinning is enabled (prevents accidental production use)
            return false;
          };
          return client;
        },
      );
    }
  }
}
