import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/core/networking/backend_api.dart';
import 'package:quacktion_frontend/features/conversations/data/session_repository.dart';
import 'package:quacktion_frontend/features/conversations/models/conversation.dart';

import '../../helpers/fake_api.dart';

void main() {
  late Api client;
  late ApiSessionRepository repository;
  late List<Map<String, dynamic>> entries;

  setUp(() {
    entries = [];
    client = Api()
      ..respond = (_, path) async {
        final offset = int.parse(Uri.parse(path).queryParameters['offset']!);
        return {'items': entries.skip(offset).take(50).toList()};
      };
    repository = ApiSessionRepository(BackendApi(client));
  });

  tearDown(() => client.dispose());

  test(
    'streams assistant fragments into one message without altering text',
    () async {
      entries.addAll([
        _entry('agent', 'Hello! I\'m Quack'),
        _entry('agent', 'tion,', seconds: 1),
        _entry('agent', ' here to help', seconds: 2),
        _entry('agent', ' with research.', seconds: 3),
      ]);

      final messages = await repository.transcript('chat');

      expect(messages, hasLength(1));
      expect(
        messages.single.text,
        "Hello! I'm Quacktion, here to help with research.",
      );
      expect(messages.single.source, MessageSource.assistant);
      expect(messages.single.sentAt, DateTime.utc(2026));
    },
  );

  test(
    'user speech separates assistant replies and groups user fragments',
    () async {
      entries.addAll([
        _entry('agent', 'Hello.'),
        _entry('user', 'What is'),
        _entry('user', ' four plus four?'),
        _entry('agent', 'It is'),
        _entry('agent', ' eight.'),
        _entry('speaker', 'Thank you.'),
        _entry('agent', 'You are welcome.'),
      ]);

      final messages = await repository.transcript('chat');

      expect(messages.map((message) => message.text), [
        'Hello.',
        'What is four plus four?',
        'It is eight.',
        'Thank you.',
        'You are welcome.',
      ]);
      expect(messages.map((message) => message.source), [
        MessageSource.assistant,
        MessageSource.app,
        MessageSource.assistant,
        MessageSource.speaker,
        MessageSource.assistant,
      ]);
    },
  );

  test(
    'refresh grows the current reply without duplicating earlier text',
    () async {
      entries.add(_entry('agent', 'Hello'));
      expect((await repository.transcript('chat')).single.text, 'Hello');

      entries.add(_entry('agent', ' there.'));
      expect((await repository.transcript('chat')).single.text, 'Hello there.');
      expect((await repository.transcript('chat')).single.text, 'Hello there.');

      entries.addAll([
        _entry('user', 'Hi.'),
        _entry('agent', 'How can I help?'),
      ]);
      expect(
        (await repository.transcript('chat')).map((message) => message.text),
        ['Hello there.', 'Hi.', 'How can I help?'],
      );
    },
  );

  test('a reply stays in one message across transcript pages', () async {
    entries.addAll([
      ...List.generate(50, (_) => _entry('agent', 'word ')),
      _entry('agent', 'done.'),
      _entry('user', 'Next question.'),
      _entry('agent', 'Next reply.'),
    ]);

    final messages = await repository.transcript('chat');

    expect(messages, hasLength(3));
    expect(messages.first.text, '${List.filled(50, 'word ').join()}done.');
    expect(client.calls.map((call) => call.path), [
      '/api/sessions/chat/transcript?offset=0&limit=50',
      '/api/sessions/chat/transcript?offset=50&limit=50',
    ]);
  });
}

Map<String, dynamic> _entry(String speaker, String text, {int seconds = 0}) => {
  'speakerIdentifier': speaker,
  'source': 'gemini',
  'text': text,
  'createdAt': DateTime.utc(2026)
      .add(Duration(seconds: seconds))
      .toIso8601String(),
};
