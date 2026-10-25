import 'dart:typed_data';

import 'package:quacktion_frontend/core/networking/api_exception.dart';
import 'package:quacktion_frontend/core/networking/http_transport.dart';

/// Feature-facing authenticated API; independent of account UI and state.
abstract interface class ApiClient {
  String get baseUrl;
  Future<String> accessToken();
  Future<Map<String, dynamic>> authorized(
    String method,
    String path, {
    Map<String, dynamic>? body,
    Uint8List? binaryBody,
    bool retryUnauthorized = true,
    Map<String, String>? headers,
  });
}

class AuthenticatedApiClient implements ApiClient {
  AuthenticatedApiClient({
    required this.transport,
    required this.token,
    required this.currentToken,
    required this.refresh,
    required this.invalidate,
  });
  final HttpTransport transport;
  final Future<String> Function() token;
  final String? Function() currentToken;
  final Future<void> Function() refresh, invalidate;
  @override
  String get baseUrl => transport.baseUrl;
  @override
  Future<String> accessToken() => token();
  @override
  Future<Map<String, dynamic>> authorized(
    String method,
    String path, {
    Map<String, dynamic>? body,
    Uint8List? binaryBody,
    bool retryUnauthorized = true,
    Map<String, String>? headers,
  }) async {
    final usedToken = await token();
    try {
      return await transport.request(
        method,
        path,
        body: body,
        binaryBody: binaryBody,
        accessToken: usedToken,
        headers: headers,
      );
    } on ApiException catch (error) {
      if (error.status != 401 || !retryUnauthorized) rethrow;
      // A concurrent request may already have rotated the single-use token.
      if (currentToken() == usedToken) await refresh();
      final replacement = currentToken();
      if (replacement == null) {
        throw const ApiException('Please sign in again.', 401);
      }
      try {
        return await transport.request(
          method,
          path,
          body: body,
          binaryBody: binaryBody,
          accessToken: replacement,
          headers: headers,
        );
      } on ApiException catch (retryError) {
        if (retryError.status == 401) await invalidate();
        rethrow;
      }
    }
  }
}
