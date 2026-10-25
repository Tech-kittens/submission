import 'dart:async';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';

import 'package:quacktion_frontend/core/networking/api_exception.dart';
import 'package:quacktion_frontend/features/devices/data/backend_device_linker.dart';
import 'package:quacktion_frontend/features/devices/data/speaker_provisioning.dart';
import 'package:quacktion_frontend/features/devices/data/saved_speaker_store.dart';

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
    this.speakerStore,
    this.backendLinker,
    this.onBackendLinked,
    this.pollInterval = const Duration(seconds: 1),
  }) {
    final Object transport = provisioning;
    if (transport is DeviceConnectionEvents) {
      _connectionEvents = transport.connectionEvents.listen((connected) {
        if (_disposed || connected || !_linkConnected) return;
        _linkConnected = false;
        _statusPoll?.cancel();
        selected = null;
        stage = PairingStage.idle;
        _emit();
      }, onError: (_) {});
    }
    if (speakerStore != null) unawaited(_loadSavedSpeaker());
  }
  final DeviceProvisioning provisioning;
  final SavedSpeakerStore? speakerStore;
  final BackendDeviceLinker? backendLinker;
  final VoidCallback? onBackendLinked;
  final Duration pollInterval;
  StreamSubscription<bool>? _connectionEvents;
  Timer? _statusPoll;
  bool get configurationBusy => busy;
  bool get readyToComplete =>
      approved && wifiState == 'connected' && backendState == 'online';

  // Only poll an explicitly opened BLE connection; normal operation has no BLE.
  void _startStatusPolling() {
    _statusPoll?.cancel();
    _statusPoll = Timer.periodic(const Duration(seconds: 1), (_) {
      if (_linkConnected && !busy && !_disposed) {
        refreshStatus().catchError((_) {});
      }
    });
  }

  Future<void> configureSavedSpeaker() => reconnect();

  Future<void> finishConfiguration() async {
    if (configurationBusy || !_linkConnected || !readyToComplete) return;
    busy = true;
    error = null;
    _emit();
    try {
      await _request('setup.complete');
      await remove();
    } catch (failure) {
      error = _friendlyError(failure);
    } finally {
      busy = false;
      _emit();
    }
  }

  Future<void> cancelSetup() async {
    if (busy || !_linkConnected || !approved) return;
    busy = true;
    error = null;
    _emit();
    try {
      await _request('setup.cancel');
      await remove();
    } catch (failure) {
      error = _friendlyError(failure);
    } finally {
      busy = false;
      _emit();
    }
  }

  Future<void> retryBackendLink() async {
    if (busy || !approved || _linkingBackend) return;
    error = null;
    _backendLinkAttempted = false;
    await _linkBackendDevice();
  }

  PairingStage stage = PairingStage.idle;
  List<NearbySpeaker> nearby = const [];
  List<WifiNetwork> networks = const [];
  NearbySpeaker? selected;
  String? deviceIdentifier;
  String? deviceName;
  String firmwareVersion = '';
  String? wifiNetwork;
  String? wifiState;
  String? backendState;
  SavedSpeaker? savedSpeaker;
  String? error;
  bool dppSupported = false;
  bool busy = false;
  bool _refreshingStatus = false;
  bool _disposed = false;
  bool _linkConnected = false;
  bool _linkingBackend = false;
  bool _backendLinkAttempted = false;
  int _generation = 0;

  Future<void> _loadSavedSpeaker() async {
    try {
      savedSpeaker = await speakerStore!.read();
      _emit();
    } catch (_) {
      // Keep device setup available if secure storage is unavailable.
    }
  }

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
      if (nearby.isEmpty) error = 'No speaker found. Hold K2 for 3 seconds and release to open setup, then scan again.';
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
    _backendLinkAttempted = false;
    busy = true;
    selected = speaker;
    stage = PairingStage.connecting;
    error = null;
    _emit();
    try {
      await provisioning.connect(speaker);
      _linkConnected = true;
      final info = await _request('device.info');
      deviceIdentifier = info['deviceIdentifier']?.toString();
      deviceName = speaker.name;
      firmwareVersion = info['firmwareVersion']?.toString() ?? '';
      final identifier = deviceIdentifier;
      if (identifier != null && identifier.isNotEmpty && speakerStore != null) {
        final sameSpeaker = savedSpeaker?.identifier == identifier;
        savedSpeaker = SavedSpeaker(
          identifier: identifier,
          name: speaker.name,
          backendDeviceId: sameSpeaker ? savedSpeaker?.backendDeviceId : null,
          firmwareVersion: firmwareVersion,
        );
        await speakerStore!.write(savedSpeaker!);
      }
      dppSupported =
          await provisioning.isDppSupported() &&
          (info['capabilities'] is Map &&
              (info['capabilities'] as Map)['dpp'] == true);
      await refreshStatus(force: true);
      _startStatusPolling();
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

  Future<void> reconnect({bool quiet = false}) async {
    final remembered = savedSpeaker;
    if (busy || remembered == null) return;
    busy = true;
    _backendLinkAttempted = false;
    stage = PairingStage.scanning;
    error = null;
    _emit();
    try {
      nearby = await provisioning.scan();
      for (final speaker in nearby) {
        try {
          await provisioning.connect(speaker);
          _linkConnected = true;
          final info = await _request('device.info');
          if (info['deviceIdentifier'] != remembered.identifier) {
            await provisioning.disconnect();
            _linkConnected = false;
            continue;
          }
          selected = speaker;
          deviceIdentifier = remembered.identifier;
          deviceName = remembered.name;
          firmwareVersion =
              info['firmwareVersion']?.toString() ?? remembered.firmwareVersion;
          dppSupported =
              await provisioning.isDppSupported() &&
              (info['capabilities'] is Map &&
                  (info['capabilities'] as Map)['dpp'] == true);
          await refreshStatus(force: true);
          _startStatusPolling();
          return;
        } catch (_) {
          await provisioning.disconnect();
          _linkConnected = false;
        }
      }
      stage = PairingStage.idle;
      if (!quiet) {
        error = 'Speaker setup Bluetooth is off or out of range. Hold K2 for 3 seconds and release, then try again.';
      }
    } catch (exception) {
      stage = PairingStage.idle;
      if (!quiet) error = _friendlyError(exception);
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
      _linkConnected = true;
      wifiState = status['wifiState']?.toString();
      backendState = status['backendState']?.toString();
      wifiNetwork = status['ssid']?.toString();
      if (status['approval'] == 'approved') {
        _linkConnected = true;
        stage = wifiState == 'connected'
            ? PairingStage.wifiConfigured
            : PairingStage.paired;
        await _linkBackendDevice();
      } else {
        stage = PairingStage.awaitingApproval;
      }
      _emit();
    } catch (exception) {
      if (exception is PlatformException) {
        _resetAfterLinkLoss(exception);
      } else {
        error = _friendlyError(exception);
        _emit();
      }
      rethrow;
    } finally {
      _refreshingStatus = false;
    }
  }

  Future<void> _linkBackendDevice() async {
    final linker = backendLinker;
    final identifier = deviceIdentifier;
    final saved = savedSpeaker;
    if (linker == null ||
        identifier == null ||
        identifier.isEmpty ||
        saved == null ||
        _linkingBackend ||
        _backendLinkAttempted) {
      return;
    }
    _backendLinkAttempted = true;
    _linkingBackend = true;
    try {
      final link = await linker.link(
        identifier: identifier,
        name: deviceName ?? saved.name,
        firmwareVersion: firmwareVersion,
        speakerBackendState: backendState ?? 'unlinked',
      );
      if (link.credential != null) {
        await _request('backend.configure', {
          'deviceId': link.deviceId,
          'credential': link.credential!,
        });
      }
      savedSpeaker = SavedSpeaker(
        identifier: saved.identifier,
        name: deviceName ?? saved.name,
        backendDeviceId: link.deviceId,
        firmwareVersion: firmwareVersion,
      );
      await speakerStore?.write(savedSpeaker!);
      onBackendLinked?.call();
    } catch (exception) {
      error =
          'Speaker connected, but account linking failed: ${_friendlyError(exception)}';
    } finally {
      _linkingBackend = false;
      _emit();
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

  Future<void> forgetWifi() async {
    if (!approved || busy || wifiState != 'connected') return;
    busy = true;
    error = null;
    _emit();
    try {
      await _request('wifi.forget');
      wifiState = 'unconfigured';
      wifiNetwork = null;
      stage = PairingStage.paired;
    } catch (exception) {
      error = _friendlyError(exception);
      if (exception is PlatformException) _resetAfterLinkLoss(exception);
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
    // Keep the remembered identity and the speaker's saved configuration.
    await provisioning.disconnect();
    _linkConnected = false;
    _statusPoll?.cancel();
    ++_generation;
    stage = PairingStage.idle;
    nearby = const [];
    networks = const [];
    selected = null;
    deviceIdentifier = null;
    deviceName = null;
    firmwareVersion = '';
    wifiNetwork = null;
    wifiState = null;
    backendState = null;
    error = null;
    _emit();
  }

  Future<bool> forgetSavedSpeaker() async {
    if (busy || savedSpeaker == null) return false;
    try {
      await speakerStore?.delete();
      savedSpeaker = null;
      await remove();
      return true;
    } catch (exception) {
      error = _friendlyError(exception);
      _emit();
      return false;
    }
  }

  void cancelScan() => ++_generation;

  Future<void> cancelConnection() async {
    ++_generation;
    await provisioning.disconnect();
    _linkConnected = false;
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
    _linkConnected = true;
    if (response['ok'] != true) {
      throw FirmwareProvisioningException(
        response['error']?.toString() ?? 'internal_error',
        _firmwareError(response['error']?.toString() ?? 'internal_error'),
      );
    }
    return response;
  }

  void _resetAfterLinkLoss(PlatformException exception) {
    _linkConnected = false;
    _statusPoll?.cancel();
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
    if (exception is ApiException) return exception.message;
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
      'Briefly press K2 on the speaker to approve this setup connection.',
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
    _connectionEvents?.cancel();
    ++_generation;
    _statusPoll?.cancel();
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
