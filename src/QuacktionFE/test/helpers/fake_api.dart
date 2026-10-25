import 'dart:typed_data';

import 'package:quacktion_frontend/core/networking/api_client.dart';
import 'package:quacktion_frontend/features/auth/application/auth_controller.dart';

class Api extends AuthController implements ApiClient {
  final calls =
      <
        ({
          String method,
          String path,
          Map<String, dynamic>? body,
          Map<String, String>? headers,
        })
      >[];
  final binaryCalls = <({String path, Uint8List bytes})>[];
  late Future<Map<String, dynamic>> Function(String, String) respond;

  @override
  Future<Map<String, dynamic>> authorized(
    String method,
    String path, {
    Map<String, dynamic>? body,
    Uint8List? binaryBody,
    bool retryUnauthorized = true,
    Map<String, String>? headers,
  }) {
    if (binaryBody != null) {
      binaryCalls.add((path: path, bytes: Uint8List.fromList(binaryBody)));
    }
    calls.add((method: method, path: path, body: body, headers: headers));
    return respond(method, path);
  }
}
