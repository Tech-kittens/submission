class ConversationMeeting {
  ConversationMeeting(
    Map<String, dynamic> data,
    String expectedSession, {
    String? expectedMeetingId,
  }) : id = data['id']?.toString() ?? '',
       sessionId = expectedSession,
       status = data['status']?.toString() ?? 'Unknown',
       recordingStatus = data['recordingStatus']?.toString() ?? 'Unknown',
       failureCode = data['analysisFailureCode']?.toString() {
    if (id.isEmpty ||
        (expectedMeetingId != null && id != expectedMeetingId) ||
        (data['sessionId'] != null && data['sessionId'] != expectedSession) ||
        (data['deviceId'] != null && data['deviceId'] != '')) {
      throw const FormatException('Invalid conversation meeting response');
    }
  }

  final String id, sessionId, status, recordingStatus;
  final String? failureCode;
  bool get recording => status == 'Active' && recordingStatus == 'Recording';
  bool get canEnd => status == 'Active' || status == 'Created';
  bool get processing => status == 'Ending' || status == 'Processing';
  bool get terminal => const {
    'Completed',
    'Failed',
    'Cancelled',
    'Interrupted',
  }.contains(status);

  String get label {
    if (recording) return 'Meeting mode on · Recording';
    if (canEnd) return 'Starting meeting mode…';
    if (processing) return 'Meeting ended · Preparing report';
    return switch (status) {
      'Completed' => 'Meeting complete',
      'Failed' => 'Meeting processing failed',
      'Interrupted' => 'Meeting interrupted',
      'Cancelled' => 'Meeting cancelled',
      _ => 'Meeting status unavailable',
    };
  }
}
