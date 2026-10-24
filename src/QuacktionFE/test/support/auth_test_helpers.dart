import 'dart:convert';

import 'package:http/http.dart' as http;
import 'package:quacktion_frontend/core/storage/token_store.dart';

class MemoryStore implements TokenStore {
  String? value;
  @override
  Future<String?> read() async => value;
  @override
  Future<void> write(String value) async {
    this.value = value;
  }

  @override
  Future<void> clear() async {
    value = null;
  }
}

Map<String, dynamic> pair({bool expired = false, String refresh = 'old'}) => {
  'accessToken': 'access-$refresh',
  'refreshToken': refresh,
  'expiresAt': DateTime.now()
      .add(Duration(minutes: expired ? -1 : 10))
      .toIso8601String(),
};
http.Response jsonResponse(Object body, [int status = 200]) =>
    http.Response(jsonEncode(body), status);
