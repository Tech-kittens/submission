import 'dart:async';
import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:http/http.dart' as http;

import 'package:quacktion_frontend/core/config/app_config.dart';
import 'package:quacktion_frontend/core/networking/api_client.dart';
import 'package:quacktion_frontend/core/networking/api_exception.dart';
import 'package:quacktion_frontend/core/networking/http_transport.dart';
import 'package:quacktion_frontend/core/storage/token_store.dart';

class AuthController extends ChangeNotifier {
  AuthController({http.Client? client, TokenStore? store, String? baseUrl})
    : _transport = HttpTransport(
        baseUrl: baseUrl ?? AppConfig.apiBaseUrl,
        client: client,
      ),
      _store = store ?? SecureTokenStore();

  final HttpTransport _transport;
  final TokenStore _store;
  String get baseUrl => _transport.baseUrl;
  late final ApiClient api = AuthenticatedApiClient(
    transport: _transport,
    token: accessToken,
    currentToken: () => _tokens?['accessToken'] as String?,
    refresh: _refresh,
    invalidate: _clear,
  );
  Map<String, dynamic>? _tokens;
  Map<String, dynamic>? profile;
  Future<void>? _refreshing;

  Future<void> _save(Map<String, dynamic> tokens) async {
    if (tokens['accessToken'] is! String ||
        tokens['refreshToken'] is! String ||
        DateTime.tryParse(tokens['expiresAt']?.toString() ?? '') == null) {
      throw const ApiException(
        'The server returned an invalid session. Please sign in again.',
      );
    }
    await _store.write(jsonEncode(tokens));
    _tokens = tokens;
  }

  Future<void> _clear() async {
    _tokens = null;
    profile = null;
    notifyListeners();
    await _store.clear();
  }

  Future<void> restore() async {
    final saved = await _store.read();
    if (saved == null) return;
    try {
      _tokens = jsonDecode(saved) as Map<String, dynamic>;
      profile = await authorized('GET', '/api/accounts/me');
      notifyListeners();
    } on FormatException {
      await _clear();
    }
  }

  Future<void> login(String email, String password) async {
    await _save(
      await _transport.request(
        'POST',
        '/api/auth/login',
        body: {'email': email.trim(), 'password': password},
      ),
    );
    profile = await authorized('GET', '/api/accounts/me');
    notifyListeners();
  }

  Future<void> register(String email, String password, String name) async {
    await _transport.request(
      'POST',
      '/api/accounts',
      body: {'email': email.trim(), 'password': password, 'name': name.trim()},
    );
  }

  Future<void> _refresh() =>
      _refreshing ??= _rotate().whenComplete(() => _refreshing = null);

  Future<void> _rotate() async {
    final refreshToken = _tokens?['refreshToken'];
    if (refreshToken == null) {
      throw const ApiException('Please sign in again.', 401);
    }
    try {
      // Remove the persisted single-use token before sending it. A crash or lost
      // response must never cause this token to be replayed on the next launch.
      await _store.clear();
      final replacement = await _transport.request(
        'POST',
        '/api/auth/refresh',
        body: {'refreshToken': refreshToken},
      );
      await _save(replacement);
    } catch (_) {
      await _clear();
      throw const ApiException(
        'Your session could not be renewed. Please sign in again.',
        401,
      );
    }
  }

  Future<Map<String, dynamic>> authorized(
    String method,
    String path, {
    Map<String, dynamic>? body,
    Uint8List? binaryBody,
    bool retryUnauthorized = true,
    Map<String, String>? headers,
  }) => api.authorized(
    method,
    path,
    body: body,
    binaryBody: binaryBody,
    retryUnauthorized: retryUnauthorized,
    headers: headers,
  );

  Future<String> accessToken() async {
    if (_tokens == null) {
      throw const ApiException('Please sign in again.', 401);
    }
    final expiry = DateTime.tryParse(_tokens!['expiresAt']?.toString() ?? '');
    if (expiry == null ||
        expiry.isBefore(DateTime.now().add(const Duration(seconds: 30)))) {
      await _refresh();
    }
    return _tokens!['accessToken'] as String;
  }

  Future<void> changePassword(
    String currentPassword,
    String newPassword,
  ) async {
    try {
      await authorized(
        'PUT',
        '/api/accounts/me/password',
        retryUnauthorized: false,
        body: {'currentPassword': currentPassword, 'newPassword': newPassword},
      );
    } on ApiException catch (error) {
      if (error.status == 401 && profile != null) {
        throw const ApiException(
          'Your current password is incorrect, or your session has expired.',
          401,
        );
      }
      rethrow;
    }
    await _clear();
  }

  Future<void> logout() async {
    // Let an in-flight rotation finish before revoking its latest token.
    try {
      await _refreshing;
    } catch (_) {
      /* Already cleared. */
    }
    final token = _tokens?['refreshToken'];
    try {
      if (token != null) {
        await _transport.request(
          'POST',
          '/api/auth/logout',
          body: {'refreshToken': token},
        );
      }
    } finally {
      await _clear();
    }
  }

  @override
  void dispose() {
    _transport.dispose();
    super.dispose();
  }
}
