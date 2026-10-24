import 'package:quacktion_frontend/features/devices/models/speaker_device.dart';

class DeviceList {
  const DeviceList(this.devices, {this.statusIncomplete = false});
  final List<SpeakerDevice> devices;
  final bool statusIncomplete;
}

abstract interface class DeviceRepository {
  Future<DeviceList> load();
  Future<void> rename(SpeakerDevice device, String name);
  Future<void> remove(SpeakerDevice device);
}
