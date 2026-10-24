import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';

import 'package:quacktion_frontend/features/devices/data/speaker_provisioning.dart';

enum PairingStage {
  idle,
  scanning,
  found,
  connecting,
  awaitingApproval,
  paired,
  configuringWifi,
  wifiConfigured,
}

class DeviceSetupController extends ChangeNotifier {
  DeviceSetupController(
    this.provisioning, {
    this.pollInterval = const Duration(seconds: 1),
  });
  final DeviceProvisioning provisioning;
  final Duration pollInterval;

  PairingStage stage = PairingStage.idle;
  List<NearbySpeaker> nearby = const [];
  List<WifiNetwork> networks = const [];
  NearbySpeaker? selected;
  String? deviceIdentifier;
  String? deviceName;
  String? wifiNetwork;
  String? wifiState;
  String? backendState;
  String? error;
  bool dppSupported = false;
  bool busy = false;
  bool _refreshingStatus = false;
  bool _disposed = false;
  int _generation = 0;

  bool get approved =>
      stage == PairingStage.paired ||
      stage == PairingStage.configuringWifi ||
      stage == PairingStage.wifiConfigured;
  bool get paired => approved;

  void _emit() {
    if (!_disposed) notifyListeners();
  }

  Future<void> scan() async {
    final generation = ++_generation;
    stage = PairingStage.scanning;
    error = null;
    nearby = const [];
    _emit();
    try {
      nearby = await provisioning.scan();
      if (_disposed || generation != _generation) return;
      stage = PairingStage.found;
      if (nearby.isEmpty) error = 'No speaker found. Hold its setup button for 3 seconds and scan again.';
    } catch (exception) {
      if (_disposed || generation != _generation) return;
      stage = PairingStage.idle;
      error = _friendlyError(exception);
    }
    _emit();
  }

  Future<void> pair(NearbySpeaker speaker) async {
    if (busy) return;
    ++_generation;
    busy = true;
    selected = speaker;
    stage = PairingStage.connecting;
    error = null;
    _emit();
    try {
      await provisioning.connect(speaker);
      final info = await _request('device.info');
      deviceIdentifier = info['deviceIdentifier']?.toString();
      deviceName = speaker.name;
      dppSupported =
          await provisioning.isDppSupported() &&
          (info['capabilities'] is Map &&
              (info['capabilities'] as Map)['dpp'] == true);
      await refreshStatus(force: true);
    } catch (exception) {
      error = _friendlyError(exception);
      if (exception is PlatformException) {
        _resetAfterLinkLoss(exception);
      } else {
        stage = PairingStage.found;
        await provisioning.disconnect();
      }
    } finally {
      busy = false;
      _emit();
    }
  }

  Future<void> identify() async {
    if (busy) return;
    busy = true;
    error = null;
    _emit();
    try {
      await _request('device.identify');
    } catch (exception) {
      error = _friendlyError(exception);
      if (exception is PlatformException) _resetAfterLinkLoss(exception);
    } finally {
      busy = false;
      _emit();
    }
  }

  Future<void> refreshStatus({bool force = false}) async {
    if (_refreshingStatus || (busy && !force)) return;
    _refreshingStatus = true;
    try {
      final status = await _request('setup.status');
      wifiState = status['wifiState']?.toString();
      backendState = status['backendState']?.toString();
      wifiNetwork = status['ssid']?.toString();
      if (status['approval'] == 'approved') {
        stage = wifiState == 'connected'
            ? PairingStage.wifiConfigured
            : PairingStage.paired;
      } else {
        stage = PairingStage.awaitingApproval;
      }
      _emit();
    } catch (exception) {
      if (exception is PlatformException) {
        _resetAfterLinkLoss(exception);
      }
      rethrow;
    } finally {
      _refreshingStatus = false;
    }
  }

  Future<void> scanWifi() async {
    if (!approved || busy) return;
    busy = true;
    error = null;
    networks = const [];
    _emit();
    try {
      final current = await _request('setup.status');
      wifiState = current['wifiState']?.toString();
      if (wifiState != 'scanning') {
        try {
          await _request('wifi.scan', {'action': 'start'});
        } on FirmwareProvisioningException catch (exception) {
          if (exception.code != 'busy') rethrow;
          // A scan may have started in another setup view just before this one.
          final status = await _request('setup.status');
          wifiState = status['wifiState']?.toString();
          if (wifiState != 'scanning') rethrow;
        }
      }
      await _waitForScan();
      final results = <WifiNetwork>[];
      int? offset = 0;
      while (offset != null) {
        Map<String, dynamic> page;
        try {
          page = await _request('wifi.scan', {
            'action': 'results',
            'offset': offset,
          });
        } on FirmwareProvisioningException catch (exception) {
          if (exception.code != 'busy') rethrow;
          await _waitForScan();
          page = await _request('wifi.scan', {
            'action': 'results',
            'offset': offset,
          });
        }
        for (final raw in (page['networks'] as List? ?? const [])) {
          if (raw is Map) {
            final entry = Map<String, dynamic>.from(raw);
            final ssid = entry['ssid']?.toString();
            if (ssid != null && ssid.isNotEmpty) {
              results.add(
                WifiNetwork(
                  ssid: ssid,
                  rssi: (entry['rssi'] as num?)?.toInt() ?? -100,
                  auth: entry['auth']?.toString() ?? 'unknown',
                  supported: entry['supported'] == true,
                ),
              );
            }
          }
        }
        offset = (page['next'] as num?)?.toInt();
      }
      networks = results;
    } catch (exception) {
      error = _friendlyError(exception);
      if (exception is PlatformException) _resetAfterLinkLoss(exception);
    } finally {
      busy = false;
      _emit();
    }
  }

  Future<void> _waitForScan() async {
    final deadline = DateTime.now().add(const Duration(seconds: 40));
    while (DateTime.now().isBefore(deadline)) {
      await Future<void>.delayed(pollInterval);
      final status = await _request('setup.status');
      wifiState = status['wifiState']?.toString();
      if (wifiState != 'scanning') return;
    }
    throw StateError('The speaker Wi-Fi scan timed out. Try again.');
  }

  Future<void> configureWifi(
    String ssid,
    String password, {
    bool hidden = false,
  }) async {
    if (!approved || busy) return;
    busy = true;
    stage = PairingStage.configuringWifi;
    error = null;
    _emit();
    try {
      await _request('wifi.configure', {
        'ssid': ssid,
        'password': password,
        'hidden': hidden,
      });
      await _waitForWifi();
    } catch (exception) {
      error = _friendlyError(exception);
      if (exception is PlatformException) _resetAfterLinkLoss(exception);
      if (exception is! PlatformException) stage = PairingStage.paired;
    } finally {
      busy = false;
      _emit();
    }
  }

  Future<void> configureWifiWithDpp() async {
    if (!approved || busy || !dppSupported) return;
    busy = true;
    stage = PairingStage.configuringWifi;
    error = null;
    _emit();
    try {
      final response = await _request('wifi.dpp.start');
      final uri = response['uri']?.toString();
      if (uri == null || uri.isEmpty) {
        throw StateError(
          'The speaker did not provide a Wi-Fi Easy Connect code.',
        );
      }
      await provisioning.openDpp(uri);
      await _waitForWifi(timeout: const Duration(seconds: 130));
    } catch (exception) {
      error = _friendlyError(exception);
      if (exception is PlatformException) {
        _resetAfterLinkLoss(exception);
      } else {
        stage = PairingStage.paired;
      }
    } finally {
      busy = false;
      _emit();
    }
  }

  Future<void> _waitForWifi({
    Duration timeout = const Duration(seconds: 45),
  }) async {
    final deadline = DateTime.now().add(timeout);
    while (DateTime.now().isBefore(deadline)) {
      await Future<void>.delayed(pollInterval);
      final status = await _request('setup.status');
      wifiState = status['wifiState']?.toString();
      backendState = status['backendState']?.toString();
      wifiNetwork = status['ssid']?.toString();
      if (wifiState == 'connected') {
        stage = PairingStage.wifiConfigured;
        return;
      }
      if (wifiState == 'failed') {
        final code = status['errorCode']?.toString() ?? 'unknown';
        throw StateError(_wifiFailure(code));
      }
    }
    throw StateError(
      'The speaker is still connecting. Check its status and try again.',
    );
  }

  Future<void> remove() async {
    try {
      if (paired) {
        await _request('setup.cancel');
      }
    } catch (_) {
      // The link may already have dropped; always release the local BLE connection.
    }
    await provisioning.disconnect();
    ++_generation;
    stage = PairingStage.idle;
    nearby = const [];
    networks = const [];
    selected = null;
    deviceIdentifier = null;
    deviceName = null;
    wifiNetwork = null;
    wifiState = null;
    backendState = null;
    error = null;
    _emit();
  }

  void cancelScan() => ++_generation;

  Future<void> cancelConnection() async {
    ++_generation;
    await provisioning.disconnect();
    if (_disposed) return;
    selected = null;
    stage = PairingStage.idle;
    _emit();
  }

  Future<Map<String, dynamic>> _request(
    String endpoint, [
    Map<String, dynamic> fields = const {},
  ]) async {
    final id = newProvisioningRequestId();
    final response = await provisioning.exchange(endpoint, {
      'v': 1,
      'id': id,
      ...fields,
    });
    if (response['ok'] != true) {
      throw FirmwareProvisioningException(
        response['error']?.toString() ?? 'internal_error',
        _firmwareError(response['error']?.toString() ?? 'internal_error'),
      );
    }
    return response;
  }

  void _resetAfterLinkLoss(PlatformException exception) {
    selected = null;
    deviceIdentifier = null;
    deviceName = null;
    wifiNetwork = null;
    wifiState = null;
    backendState = null;
    stage = PairingStage.idle;
    error = _friendlyError(exception);
    unawaited(provisioning.disconnect());
    _emit();
  }

  String _friendlyError(Object exception) {
    if (exception is PlatformException) {
      return exception.message ?? exception.code;
    }
    if (exception is FirmwareProvisioningException) {
      return exception.message;
    }
    if (exception is StateError) {
      return exception.message;
    }
    return 'Could not communicate with the speaker. Disconnect and try again.';
  }

  String _firmwareError(String code) => switch (code) {
    'busy' => 'The speaker is already scanning or connecting. Wait a moment, then rescan.',
    'approval_required' =>
      'Press the setup button on the speaker to approve this phone.',
    'setup_expired' => 'Speaker setup mode expired. Hold its setup button for 3 seconds and try again.',
    'invalid_password' => 'That Wi-Fi password is not valid for this network.',
    'unsupported_auth' =>
      'This network uses a security type the speaker cannot join.',
    'network_not_found' => 'The speaker cannot see that 2.4 GHz Wi-Fi network.',
    'wrong_password' =>
      'That Wi-Fi password was rejected. Check it and try again.',
    'no_ip_address' =>
      'The speaker joined Wi-Fi but did not receive an IP address.',
    'wifi_timeout' => 'The speaker could not connect to Wi-Fi in time.',
    'dpp_unavailable' => 'Wi-Fi Easy Connect is not available. Use Wi-Fi name and password instead.',
    'dpp_failed' || 'dpp_timeout' =>
      'Wi-Fi sharing did not complete. Try again or enter the password.',
    'backend_unreachable' =>
      'Wi-Fi connected, but the speaker cannot reach the backend.',
    _ => 'Speaker setup failed ($code). Try again.',
  };

  String _wifiFailure(String code) => _firmwareError(code);

  @override
  void dispose() {
    _disposed = true;
    ++_generation;
    unawaited(provisioning.disconnect());
    super.dispose();
  }
}

class FirmwareProvisioningException implements Exception {
  const FirmwareProvisioningException(this.code, this.message);

  final String code;
  final String message;

  @override
  String toString() => message;
}
