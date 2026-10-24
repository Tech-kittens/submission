class SpeakerDevice {
  SpeakerDevice.fromJson(Map<String, dynamic> data)
    : id = data['id'] as String,
      identifier = data['deviceIdentifier']?.toString() ?? '',
      name = data['name']?.toString() ?? 'Speaker',
      firmwareVersion = data['firmwareVersion']?.toString() ?? '',
      status = data['status']?.toString() ?? 'Unknown',
      lastSeen = DateTime.tryParse(data['lastSeenAt']?.toString() ?? '')
          ?.toLocal();
  final String id, identifier, name, firmwareVersion;
  String status;
  DateTime? lastSeen;
  bool? connected;
  String get statusLabel => status == 'Unpaired'
      ? 'Not linked'
      : connected == true
      ? 'Online'
      : connected == false
      ? 'Offline'
      : 'Connection unknown';
}
