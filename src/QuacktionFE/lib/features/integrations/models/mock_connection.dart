import 'package:flutter/material.dart';

class MockConnection {
  MockConnection(this.name, this.description, this.icon, this.slug);
  String name;
  final String description;
  final IconData icon;
  final String slug;
  bool connected = false;
  bool allowActions = false;
  String? endpoint;

  static List<MockConnection> examples() => [
    MockConnection(
      'Google Calendar',
      'Bring your schedule into the conversation.',
      Icons.calendar_month_outlined,
      'calendar',
    ),
    MockConnection(
      'Notion',
      'Keep notes and knowledge within reach.',
      Icons.description_outlined,
      'notion',
    ),
    MockConnection(
      'Slack',
      'Catch up on conversations with your team.',
      Icons.forum_outlined,
      'slack',
    ),
    MockConnection(
      'GitHub',
      'Explore your projects, issues, and pull requests.',
      Icons.code_rounded,
      'github',
    ),
  ];
}
