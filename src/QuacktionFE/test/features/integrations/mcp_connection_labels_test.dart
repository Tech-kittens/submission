import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/features/integrations/models/mcp_models.dart';

void main() {
  test('legacy generated names reflect the actual provider', () {
    for (final entry in {
      'google': 'Google',
      'github': 'GitHub',
      'jira': 'Jira',
    }.entries) {
      final connection = McpConnection({
        'id': 'abc123456',
        'provider': entry.key,
        'displayName': 'Google connection abc123',
        'enabledTools': ['github/create_issue'],
      });
      expect(connection.name, '${entry.value} connection abc123');
      expect(connection.provider, entry.key);
      expect(connection.id, 'abc123456');
      expect(connection.enabledTools, ['github/create_issue']);
    }
  });

  test('custom labels and future backend names stay intact', () {
    for (final name in [
      'Work GitHub',
      'alex@example.com',
      'GitHub connection abc123',
      'Google connection for work',
    ]) {
      expect(
        McpConnection({'id': '1', 'provider': 'github', 'displayName': name})
            .name,
        name,
      );
    }
  });

  test(
    'missing labels use provider without pretending a grant ID is an identity',
    () {
      expect(
        McpConnection({
          'id': '1',
          'provider': 'github',
          'providerAccountId': 'grant:private',
        }).name,
        'GitHub account',
      );
      expect(McpConnection({'id': '1'}).name, 'Connected account');
      expect(
        McpConnection({'id': '1', 'displayName': 'Google connection abc123'})
            .name,
        'Google connection abc123',
      );
      expect(
        McpProvider({'id': 'github', 'displayName': 'github'}).label,
        'GitHub',
      );
    },
  );
}
