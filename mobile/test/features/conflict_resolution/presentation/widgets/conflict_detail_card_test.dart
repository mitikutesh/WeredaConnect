import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/features/conflict_resolution/data/models/conflict_event.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/widgets/conflict_detail_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

ConflictEvent _makeConflict({
  String entityId = 'crop-001-abcdef',
  String entityType = 'crop_listing',
  int serverVersion = 2,
  Map<String, dynamic>? serverData,
}) {
  final op = SyncOperation()
    ..operationId = 'op-$entityId'
    ..idempotencyKey = '$entityType:$entityId:update:1234567890'
    ..entityType = entityType
    ..entityId = entityId
    ..action = 'update'
    ..data = '{}'
    ..status = SyncStatus.synced
    ..retryCount = 0
    ..createdAt = DateTime.utc(2026, 2, 21);

  return ConflictEvent(
    operation: op,
    serverData: serverData ??
        {
          'cropType': 'Teff',
          'quantity': 50.0,
          'pricePerKg': 45.0,
          'status': 'active',
        },
    serverVersion: serverVersion,
    entityType: entityType,
    entityId: entityId,
    resolvedAt: DateTime.utc(2026, 2, 21),
  );
}

Widget _buildWidget(Widget child) {
  return MaterialApp(
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    locale: const Locale('en'),
    home: Scaffold(body: SingleChildScrollView(child: child)),
  );
}

void main() {
  group('ConflictDetailCard', () {
    testWidgets(
        'renders crop_listing conflict with correct icon, label, and fields',
        (WidgetTester tester) async {
      final conflict = _makeConflict(
        serverData: {
          'cropType': 'Teff',
          'quantity': 50.0,
          'pricePerKg': 45.0,
          'status': 'active',
        },
      );

      await tester.pumpWidget(
        _buildWidget(ConflictDetailCard(conflict: conflict)),
      );
      await tester.pumpAndSettle();

      // Correct icon for crop_listing
      expect(find.byIcon(Icons.grass), findsOneWidget);

      // Entity label
      expect(find.text('Crop Listing'), findsOneWidget);

      // Timestamp: Feb 21, 2026
      expect(find.text('Feb 21, 2026'), findsOneWidget);

      // Field diffs
      expect(find.textContaining('Crop Type'), findsOneWidget);
      expect(find.textContaining('Teff'), findsOneWidget);
      expect(find.textContaining('Quantity'), findsOneWidget);
      expect(find.textContaining('50.0'), findsOneWidget);
      expect(find.textContaining('Price/kg'), findsOneWidget);
      expect(find.textContaining('45.0'), findsOneWidget);
      expect(find.textContaining('Status'), findsOneWidget);
      expect(find.textContaining('active'), findsOneWidget);
    });

    testWidgets(
        'renders delivery conflict with correct icon and tripId truncation',
        (WidgetTester tester) async {
      final conflict = _makeConflict(
        entityId: 'del-001-abcdef',
        entityType: 'delivery',
        serverData: {
          'status': 'delivered',
          'tripId': 'trip-abc12345',
        },
      );

      await tester.pumpWidget(
        _buildWidget(ConflictDetailCard(conflict: conflict)),
      );
      await tester.pumpAndSettle();

      // Correct icon for delivery
      expect(find.byIcon(Icons.inventory_2), findsOneWidget);

      // Entity label
      expect(find.text('Delivery'), findsOneWidget);

      // Fields
      expect(find.textContaining('Status'), findsOneWidget);
      expect(find.textContaining('delivered'), findsOneWidget);
      expect(find.textContaining('Trip'), findsOneWidget);
      // tripId truncated to 8 chars: "trip-abc"
      expect(find.textContaining('trip-abc'), findsOneWidget);
    });

    testWidgets('renders trip conflict with correct icon and route display',
        (WidgetTester tester) async {
      final conflict = _makeConflict(
        entityId: 'trip-001-abcdef',
        entityType: 'trip',
        serverData: {
          'fromWoreda': 'Debre Birhan',
          'toWoreda': 'Addis Ababa',
          'status': 'in_transit',
          'capacity': 100.0,
        },
      );

      await tester.pumpWidget(
        _buildWidget(ConflictDetailCard(conflict: conflict)),
      );
      await tester.pumpAndSettle();

      // Correct icon for trip
      expect(find.byIcon(Icons.local_shipping), findsOneWidget);

      // Entity label
      expect(find.text('Trip'), findsOneWidget);

      // Route display: "Debre Birhan → Addis Ababa"
      expect(find.textContaining('Debre Birhan'), findsOneWidget);
      expect(find.textContaining('Addis Ababa'), findsOneWidget);
      expect(find.textContaining('→'), findsOneWidget);

      // Other fields
      expect(find.textContaining('Capacity'), findsOneWidget);
      expect(find.textContaining('100.0'), findsOneWidget);
    });

    testWidgets('shows "Server version applied" badge with check_circle icon',
        (WidgetTester tester) async {
      final conflict = _makeConflict();

      await tester.pumpWidget(
        _buildWidget(ConflictDetailCard(conflict: conflict)),
      );
      await tester.pumpAndSettle();

      // Badge text
      expect(find.text('Server version applied'), findsOneWidget);

      // check_circle icon in the badge
      expect(find.byIcon(Icons.check_circle), findsOneWidget);
    });

    testWidgets(
        'handles unknown entity type with sync_problem icon and empty fields',
        (WidgetTester tester) async {
      final conflict = _makeConflict(
        entityType: 'unknown_type',
        serverData: {'foo': 'bar'},
      );

      await tester.pumpWidget(
        _buildWidget(ConflictDetailCard(conflict: conflict)),
      );
      await tester.pumpAndSettle();

      // Falls back to sync_problem icon
      expect(find.byIcon(Icons.sync_problem), findsOneWidget);

      // Entity label falls back to raw entityType
      expect(find.text('unknown_type'), findsOneWidget);

      // No field diffs rendered (empty map), only the badge check icon remains
      // The "Server version applied" badge should still be present
      expect(find.text('Server version applied'), findsOneWidget);
    });

    testWidgets('short tripId (less than 8 chars) renders without crashing',
        (WidgetTester tester) async {
      final conflict = _makeConflict(
        entityId: 'del-002',
        entityType: 'delivery',
        serverData: {
          'status': 'pending',
          'tripId': 'abc',
        },
      );

      await tester.pumpWidget(
        _buildWidget(ConflictDetailCard(conflict: conflict)),
      );
      await tester.pumpAndSettle();

      // Short tripId displayed as-is
      expect(find.textContaining('abc'), findsOneWidget);
    });

    testWidgets('null server data values display as dash',
        (WidgetTester tester) async {
      final conflict = _makeConflict(
        serverData: {},
      );

      await tester.pumpWidget(
        _buildWidget(ConflictDetailCard(conflict: conflict)),
      );
      await tester.pumpAndSettle();

      // Each missing field renders as '-' in the field diff text
      // crop_listing has 4 fields: cropType, quantity, pricePerKg, status
      // They render as "Crop Type: -", "Quantity: -", etc.
      final dashFinder = find.textContaining(': -');
      expect(dashFinder, findsNWidgets(4));
    });

    testWidgets('card has correct shape with rounded corners',
        (WidgetTester tester) async {
      final conflict = _makeConflict();

      await tester.pumpWidget(
        _buildWidget(ConflictDetailCard(conflict: conflict)),
      );
      await tester.pumpAndSettle();

      final card = tester.widget<Card>(find.byType(Card));
      expect(card.elevation, equals(1));
      final shape = card.shape as RoundedRectangleBorder;
      expect(
        shape.borderRadius,
        equals(BorderRadius.circular(12)),
      );
    });
  });
}
