import 'dart:typed_data';
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
    Uint8List? binaryBody,
    String? accessToken,
    Map<String, String>? headers,
  }) async {
    try {
      final uri = Uri.parse('$baseUrl$path');
      final request = http.Request(method, uri);
      // Meeting uploads wait for synchronous transcription (up to 75 seconds)
      // in addition to transferring the PCM body and persisting the segment.
      final meetingAudio =
          method == 'POST' &&
          binaryBody != null &&
          RegExp(r'^/api/meetings/[^/]+/audio/segments$').hasMatch(uri.path);
      final timeout = Duration(seconds: meetingAudio ? 120 : 20);
      request.headers['Content-Type'] = binaryBody == null
          ? 'application/json'
          : 'application/octet-stream';
      if (headers != null) request.headers.addAll(headers);
      if (accessToken != null) {
        request.headers['Authorization'] = 'Bearer $accessToken';
      }
      if (binaryBody != null) {
        request.bodyBytes = binaryBody;
      } else if (body != null) {
        request.body = jsonEncode(body);
      }
      final response = await (() async {
        return http.Response.fromStream(await client.send(request));
      })().timeout(timeout);
      Map<String, dynamic> data = {};
      if (response.body.isNotEmpty) {
        try {
          final decoded = jsonDecode(response.body);
          if (decoded is Map<String, dynamic>) data = decoded;
          if (decoded is List) data = {'items': decoded};
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
      final code = (data['code'] ?? data['title'])?.toString();
      throw ApiException(
        message,
        response.statusCode,
        code,
        data['traceId']?.toString(),
      );
    } on TimeoutException {
      throw const ApiException(
        'The request timed out. Check your connection and try again.',
        null,
        'request_timeout',
      );
    } on http.ClientException {
      throw const ApiException(
        'Cannot reach Quacktion. Check your internet connection and try again.',
        null,
        'network_unavailable',
      );
    }
  }

  void dispose() => client.close();
}
