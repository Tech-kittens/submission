import 'dart:async';
import 'dart:convert';

import 'package:http/http.dart' as http;

import 'package:quacktion_frontend/core/networking/api_exception.dart';

/// JSON HTTP transport. Owns its client; never logs credentials or bodies.
class HttpTransport {
  HttpTransport({required this.baseUrl, http.Client? client})
    : client = client ?? http.Client();
  final String baseUrl;
  final http.Client client;
  Future<Map<String, dynamic>> request(
    String method,
    String path, {
    Map<String, dynamic>? body,
    String? accessToken,
    Map<String, String>? headers,
  }) async {
    try {
      final request = http.Request(method, Uri.parse('$baseUrl$path'));
      request.headers['Content-Type'] = 'application/json';
      if (headers != null) request.headers.addAll(headers);
      if (accessToken != null) {
        request.headers['Authorization'] = 'Bearer $accessToken';
      }
      if (body != null) request.body = jsonEncode(body);
      final response = await (() async {
        return http.Response.fromStream(await client.send(request));
      })().timeout(const Duration(seconds: 20));
      Map<String, dynamic> data = {};
      if (response.body.isNotEmpty) {
        try {
          final decoded = jsonDecode(response.body);
          if (decoded is Map<String, dynamic>) data = decoded;
        } on FormatException {
          /* Some proxies return HTML errors. */
        }
      }
      if (response.statusCode >= 200 && response.statusCode < 300) return data;
      final message = switch (response.statusCode) {
        401 => 'Your credentials are incorrect or your session has expired. Please sign in again.',
        409 =>
          data['title'] == 'duplicate_key'
              ? 'An account with this email already exists.'
              : 'Your account changed. Please try again.',
        429 => 'Too many attempts. Wait a minute before trying again.',
        >= 500 =>
          'The server is temporarily unavailable. Please try again later.',
        _ => 'Check your details and try again.',
      };
      throw ApiException(message, response.statusCode);
    } on TimeoutException {
      throw const ApiException(
        'The request timed out. Check your connection and try again.',
      );
    } on http.ClientException {
      throw const ApiException(
        'Cannot reach Quacktion. Check your connection and that the API is running.',
      );
    }
  }

  void dispose() => client.close();
}
