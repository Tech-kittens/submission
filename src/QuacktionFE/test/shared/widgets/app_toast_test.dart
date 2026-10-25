import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:quacktion_frontend/shared/widgets/app_toast.dart';

void main() {
  tearDown(AppToast.dismiss);

  testWidgets('errors appear once and can be retried after clearing', (
    tester,
  ) async {
    final errors = _Errors();
    addTearDown(errors.dispose);
    await tester.pumpWidget(
      MaterialApp(
        home: ErrorToastListener(
          listenable: errors,
          message: () => errors.message,
          child: const Scaffold(body: Text('Conversation')),
        ),
      ),
    );

    errors.report('Connection interrupted.');
    await tester.pumpAndSettle();
    expect(find.text('Connection interrupted.'), findsOneWidget);
    await tester.tap(find.byTooltip('Dismiss notification'));
    await tester.pumpAndSettle();

    errors.report('Connection interrupted.');
    await tester.pumpAndSettle();
    expect(find.text('Connection interrupted.'), findsNothing);

    errors.report(null);
    errors.report('Connection interrupted.');
    await tester.pumpAndSettle();
    expect(find.text('Connection interrupted.'), findsOneWidget);
    await tester.pump(const Duration(seconds: 7));
    expect(find.text('Connection interrupted.'), findsNothing);
  });

  testWidgets('retry is actionable and dismisses the toast', (tester) async {
    final errors = _Errors();
    addTearDown(errors.dispose);
    var retries = 0;
    await tester.pumpWidget(
      MaterialApp(
        home: ErrorToastListener(
          listenable: errors,
          message: () => errors.message,
          onRetry: () => retries++,
          child: const Scaffold(body: Text('Conversation')),
        ),
      ),
    );
    errors.report('Could not load conversations.');
    await tester.pumpAndSettle();
    await tester.tap(find.text('Retry'));
    await tester.pumpAndSettle();
    expect(retries, 1);
    expect(find.text('Could not load conversations.'), findsNothing);
  });

  testWidgets('toast appears above a setup sheet and replaces older notices', (
    tester,
  ) async {
    late BuildContext pageContext;
    await tester.pumpWidget(
      MaterialApp(
        home: Builder(
          builder: (context) {
            pageContext = context;
            return const Scaffold(body: Text('Devices'));
          },
        ),
      ),
    );
    showModalBottomSheet<void>(
      context: pageContext,
      builder: (_) => const SizedBox(height: 240, child: Text('Wi-Fi setup')),
    );
    await tester.pumpAndSettle();
    AppToast.show(pageContext, 'First error', isError: true);
    await tester.pump();
    AppToast.show(pageContext, 'Wi-Fi password is incorrect.', isError: true);
    await tester.pumpAndSettle();
    expect(find.text('First error'), findsNothing);
    expect(find.text('Wi-Fi setup'), findsOneWidget);
    expect(find.text('Wi-Fi password is incorrect.'), findsOneWidget);
    // The overlay notification remains tappable above the modal barrier.
    await tester.tap(find.byTooltip('Dismiss notification'));
    await tester.pumpAndSettle();
    expect(find.text('Wi-Fi password is incorrect.'), findsNothing);
    expect(tester.takeException(), isNull);
  });
}

class _Errors extends ChangeNotifier {
  String? message;
  void report(String? value) {
    message = value;
    notifyListeners();
  }
}
