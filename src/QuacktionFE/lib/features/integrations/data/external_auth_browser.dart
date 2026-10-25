import 'package:flutter/services.dart';

abstract interface class ExternalAuthBrowser {
  Future<void> open(Uri uri);
}

class SystemAuthBrowser implements ExternalAuthBrowser {
  static const _channel = MethodChannel('quacktion/external_auth');

  @override
  Future<void> open(Uri uri) async {
    if (uri.scheme != 'https' || uri.host.isEmpty || uri.userInfo.isNotEmpty) {
      throw const FormatException('Invalid authorization address');
    }
    await _channel.invokeMethod<void>('open', {'url': uri.toString()});
  }
}
