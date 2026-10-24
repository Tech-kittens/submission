abstract final class AppConfig {
  static const apiBaseUrl = String.fromEnvironment(
    'API_BASE_URL',
    defaultValue: 'https://quackback.ofneill.com',
    // defaultValue: 'http://localhost:5188',
  );
  static const voiceInputRate = int.fromEnvironment(
    'VOICE_INPUT_RATE',
    defaultValue: 16000,
  );
  static const voiceOutputRate = int.fromEnvironment(
    'VOICE_OUTPUT_RATE',
    defaultValue: 16000,
  );
}
