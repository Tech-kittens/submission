import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:quacktion_frontend/core/networking/api_exception.dart';
import 'package:quacktion_frontend/features/conversations/application/conversation_history.dart';
import 'package:quacktion_frontend/features/conversations/models/conversation.dart';
import 'package:quacktion_frontend/features/conversations/models/session_conversation.dart';
import 'package:quacktion_frontend/features/conversations/data/session_repository.dart';
import 'package:quacktion_frontend/features/conversations/data/session_event_client.dart';

export 'package:quacktion_frontend/features/conversations/models/session_conversation.dart';

/// Conversation history and live transcript state.
class ConversationController extends ConversationHistory {
  ConversationController({this.repository, this.events});
  final SessionRepository? repository;
  final SessionEventClient? events;
  bool get hasBackend => repository != null;
  final List<SessionConversation> _items = [];
  @override
  List<SessionConversation> get conversations =>
      List.of(_items)..sort((a, b) => b.updatedAt.compareTo(a.updatedAt));

  SessionConversation? active;
  bool loading = false, busy = false;
  @override
  String? error;
  Timer? _polling;
  bool _pollingNow = false;

  @override
  bool canDelete(Conversation conversation) =>
      repository == null ||
      conversation is! SessionConversation ||
      conversation.status == 'Created';

  Future<void> initialize() async {
    if (repository != null) {
      var loadPhase = 'session list';
      loading = true;
      error = null;
      notifyListeners();
      try {
        final activeId = active?.id;
        _items
          ..clear()
          ..addAll(await repository!.load());
        final latestChats = conversations;
        active = null;
        for (final chat in latestChats) {
          if (chat.id == activeId) {
            active = chat;
            break;
          }
        }
        active ??= latestChats.isEmpty ? null : latestChats.first;
        if (active != null) {
          loadPhase = 'selected conversation transcript';
          await _loadTranscript(active!);
          try {
            await events?.join(active!.id);
          } catch (_) {
            // REST transcript loading and the polling fallback still work.
          }
        } else {
          await events?.connect();
        }
        _updatePolling();
      } catch (failure) {
        final status = failure is ApiException ? failure.status : null;
        final code = failure is ApiException ? failure.code : null;
        debugPrint(
          'Conversation $loadPhase load failed${status == null ? '' : ' (HTTP $status)'}${code == null ? '' : ' [$code]'}: ${failure.runtimeType}',
        );
        error = status == 401
            ? 'Your sign-in has expired. Sign out and back in to load conversations.'
            : loadPhase == 'selected conversation transcript'
            ? 'Could not open this transcript. Please try again.'
            : 'Could not load your conversations. Please try again.';
      } finally {
        loading = false;
        notifyListeners();
      }
      return;
    }
    if (_items.isEmpty) {
      final now = DateTime.now();
      _items.addAll([
        SessionConversation(
            id: 'demo-1',
            title: 'Planning the weekend',
            updatedAt: now,
          )
          ..status = 'Completed'
          ..messages.addAll([
            ChatMessage(
              text: 'Help me plan a relaxed weekend.',
              source: MessageSource.app,
              sentAt: now.subtract(const Duration(days: 1)),
            ),
            ChatMessage(
              text: 'A walk, a good lunch, and some free time sounds like a nice balance.',
              source: MessageSource.assistant,
              sentAt: now.subtract(const Duration(days: 1, minutes: -1)),
            ),
          ]),
        SessionConversation(
            id: 'demo-2',
            title: 'Ideas for dinner',
            updatedAt: now.subtract(const Duration(hours: 3)),
          )
          ..status = 'Completed'
          ..messages.addAll([
            ChatMessage(
              text: 'Give me a few easy dinner ideas.',
              source: MessageSource.app,
              sentAt: now.subtract(const Duration(hours: 3)),
            ),
            ChatMessage(
              text: 'How about veggie tacos, lemon pasta, or a quick chickpea curry?',
              source: MessageSource.assistant,
              sentAt: now.subtract(const Duration(hours: 3, minutes: -1)),
            ),
          ]),
      ]);
      active = conversations.first;
    }
    notifyListeners();
  }

  @override
  Future<void> refresh() => initialize();

  Future<void> select(SessionConversation? chat) async {
    final previousId = active?.id;
    _polling?.cancel();
    _polling = null;
    if (previousId != null && previousId != chat?.id) {
      try {
        await events?.leave(previousId);
      } catch (_) {}
    }
    active = chat;
    error = null;
    notifyListeners();
    if (chat != null && repository != null) {
      loading = true;
      notifyListeners();
      try {
        await _loadTranscript(chat);
        try {
          await events?.join(chat.id);
        } catch (_) {
          // REST transcript loading and the polling fallback still work.
        }
      } catch (_) {
        error = 'Could not load this conversation transcript.';
      } finally {
        loading = false;
        _updatePolling();
        notifyListeners();
      }
    }
  }

  Future<void> _loadTranscript(SessionConversation chat) async {
    chat.messages
      ..clear()
      ..addAll(await repository!.transcript(chat.id));
  }

  Future<SessionConversation?> createConversation(String title) async {
    if (repository == null || busy) return null;
    busy = true;
    loading = true;
    notifyListeners();
    try {
      final chat = await repository!.create(title);
      _items.add(chat);
      active = chat;
      error = null;
      return chat;
    } catch (_) {
      error = 'Could not create a conversation. Please try again.';
      return null;
    } finally {
      busy = false;
      loading = false;
      notifyListeners();
    }
  }

  void addVoiceSession(SessionConversation chat) {
    _items.removeWhere((item) => item.id == chat.id);
    _items.add(chat);
    active = chat;
    error = null;
    _updatePolling();
    notifyListeners();
    unawaited(events?.join(chat.id));
  }

  void updateVoiceSession(SessionConversation chat) {
    final index = _items.indexWhere((item) => item.id == chat.id);
    if (index < 0) {
      _items.add(chat);
    } else {
      _items[index]
        ..status = chat.status
        ..updatedAt = DateTime.now();
    }
    if (active?.id == chat.id) active = chat;
    _updatePolling();
    notifyListeners();
  }

  @override
  Future<bool> renameConversation(
    Conversation conversation,
    String title,
  ) async {
    final chat = conversation;
    if (title.trim().isEmpty) return false;
    try {
      if (repository != null) await repository!.rename(chat.id, title.trim());
      chat.title = title.trim();
      error = null;
      notifyListeners();
      return true;
    } catch (_) {
      error = 'Could not rename this conversation.';
      notifyListeners();
      return false;
    }
  }

  @override
  Future<bool> deleteConversation(Conversation conversation) async {
    final chat = conversation;
    if (!canDelete(chat)) return false;
    try {
      if (repository != null) await repository!.delete(chat.id);
      _items.removeWhere((item) => item.id == chat.id);
      if (active?.id == chat.id) {
        final remaining = conversations;
        active = remaining.isEmpty ? null : remaining.first;
        if (active != null && repository != null) {
          await _loadTranscript(active!);
        }
      }
      error = null;
      notifyListeners();
      _updatePolling();
      return true;
    } catch (_) {
      error = 'Could not delete this conversation.';
      notifyListeners();
      return false;
    }
  }

  void _updatePolling() {
    _polling?.cancel();
    final chat = active;
    if (repository == null || chat == null || !chat.shouldPoll) return;
    _polling = Timer.periodic(const Duration(seconds: 3), (_) {
      unawaited(_refreshActiveSession(chat));
    });
  }

  Future<void> _refreshActiveSession(SessionConversation chat) async {
    if (_pollingNow || active?.id != chat.id) return;
    _pollingNow = true;
    try {
      final latest = await repository!.loadOne(chat.id);
      if (active?.id != chat.id) return;
      chat.status = latest.status;
      chat.updatedAt = latest.updatedAt;
      chat.messages
        ..clear()
        ..addAll(await repository!.transcript(chat.id));
      if (!chat.shouldPoll) _updatePolling();
      notifyListeners();
    } catch (_) {
      // Keep the last known transcript visible during temporary connectivity loss.
    } finally {
      _pollingNow = false;
    }
  }

  void onSessionEvent(String sessionId) {
    final chat = active;
    if (chat != null && chat.id == sessionId) {
      unawaited(_refreshActiveSession(chat));
    }
  }

  void beginMockVoiceConversation() {
    final now = DateTime.now();
    final chat =
        active ??
        SessionConversation(
          id: 'demo-${now.microsecondsSinceEpoch}',
          title: 'Voice conversation',
          updatedAt: now,
        );
    if (!_items.contains(chat)) _items.add(chat);
    if (repository == null) chat.status = 'Active';
    active = chat;
    notifyListeners();
  }

  void addMockReply() {
    final chat = active;
    if (chat == null) return;
    final now = DateTime.now();
    chat.messages.add(
      ChatMessage(
        text:
            'I’m listening. Voice conversations are currently shown as a demo.',
        source: MessageSource.assistant,
        sentAt: now,
      ),
    );
    chat.updatedAt = now;
    notifyListeners();
  }

  Future<void> end() async {
    if (active case final chat?) {
      if (repository != null && chat.canEnd) {
        try {
          await repository!.end(chat.id);
          chat.status = 'Completed';
          _polling?.cancel();
          _polling = null;
        } catch (_) {
          error = 'Could not end this conversation.';
        }
      } else if (repository == null) {
        chat.status = 'Completed';
      }
    }
    notifyListeners();
  }

  @override
  void dispose() {
    _polling?.cancel();
    unawaited(events?.dispose());
    super.dispose();
  }
}
