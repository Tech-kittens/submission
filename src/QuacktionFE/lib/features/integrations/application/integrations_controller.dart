import 'package:flutter/foundation.dart';

import 'package:quacktion_frontend/features/integrations/models/mock_connection.dart';

class IntegrationsController extends ChangeNotifier {
  final connections = MockConnection.examples();
  void changed() => notifyListeners();
}
