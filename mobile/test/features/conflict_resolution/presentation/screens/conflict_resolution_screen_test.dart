import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/database/schemas/sync_operation.dart';
import 'package:wereda_connect/features/conflict_resolution/data/models/conflict_event.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_bloc.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_event.dart'
    as bloc_event;
import 'package:wereda_connect/features/conflict_resolution/presentation/bloc/conflict_state.dart';
import 'package:wereda_connect/features/conflict_resolution/presentation/screens/conflict_resolution_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockConflictBloc extends Mock implements ConflictBloc {}

class FakeConflictBlocEvent extends Fake
    implements bloc_event.ConflictBlocEvent {}

class FakeConflictState extends Fake implements ConflictState {}

/// Helper to build a test [ConflictEvent].
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

void main() {
  late MockConflictBloc conflictBloc;

  setUpAll(() {
    registerFallbackValue(FakeConflictBlocEvent());
    registerFallbackValue(FakeConflictState());
  });

  setUp(() {
    conflictBloc = MockConflictBloc();
    when(() => conflictBloc.state).thenReturn(const ConflictInitial());
    when(() => conflictBloc.stream)
        .thenAnswer((_) => const Stream<ConflictState>.empty());
  });

  /// Wrap the screen in MaterialApp with BlocProvider and l10n.
  Widget buildScreen(ConflictEvent conflict) {
    return BlocProvider<ConflictBloc>.value(
      value: conflictBloc,
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        locale: const Locale('en'),
        home: Scaffold(
          body: ConflictResolutionScreen(conflict: conflict),
        ),
      ),
    );
  }

  group('ConflictResolutionScreen', () {
    testWidgets('renders correctly with a crop_listing conflict',
        (WidgetTester tester) async {
      final conflict = _makeConflict(
        serverData: {
          'cropType': 'Teff',
          'quantity': 50.0,
          'pricePerKg': 45.0,
          'status': 'active',
        },
      );

      await tester.pumpWidget(buildScreen(conflict));
      await tester.pumpAndSettle();

      // Header: "Data Updated" title
      expect(find.text('Data Updated'), findsOneWidget);

      // Entity badge: "Crop Listing" label
      expect(find.textContaining('Crop Listing'), findsOneWidget);

      // Entity ID (first 8 chars) shown in badge
      expect(find.textContaining('crop-001'), findsOneWidget);

      // Server data fields displayed
      expect(find.text('Crop Type'), findsOneWidget);
      expect(find.text('Teff'), findsOneWidget);
      expect(find.text('Quantity'), findsOneWidget);
      expect(find.text('50.0'), findsOneWidget);
      expect(find.text('Price/kg'), findsOneWidget);
      expect(find.text('45.0'), findsOneWidget);

      // Buttons present
      expect(find.text('OK, Got It'), findsOneWidget);
      expect(find.text('Dismiss'), findsOneWidget);

      // Icons
      expect(find.byIcon(Icons.sync_problem), findsOneWidget);
      expect(find.byIcon(Icons.list_alt), findsOneWidget);
    });

    testWidgets('renders correctly with a delivery conflict',
        (WidgetTester tester) async {
      final conflict = _makeConflict(
        entityId: 'del-001-abcdef',
        entityType: 'delivery',
        serverData: {
          'status': 'delivered',
          'tripId': 'trip-abc12345',
        },
      );

      await tester.pumpWidget(buildScreen(conflict));
      await tester.pumpAndSettle();

      // Entity badge: "Delivery" label
      expect(find.textContaining('Delivery'), findsOneWidget);

      // Delivery-specific fields
      expect(find.text('Status'), findsOneWidget);
      expect(find.text('delivered'), findsOneWidget);
      expect(find.text('Trip'), findsOneWidget);
      // tripId truncated to 8 chars
      expect(find.text('trip-abc'), findsOneWidget);

      // Delivery icon
      expect(find.byIcon(Icons.inventory_2), findsOneWidget);
    });

    testWidgets('renders correctly with a trip conflict',
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

      await tester.pumpWidget(buildScreen(conflict));
      await tester.pumpAndSettle();

      // Entity badge: "Trip" label
      expect(find.textContaining('Trip'), findsOneWidget);

      // Trip-specific fields
      expect(find.text('Route'), findsOneWidget);
      expect(find.textContaining('Debre Birhan'), findsOneWidget);
      expect(find.textContaining('Addis Ababa'), findsOneWidget);
      expect(find.text('Capacity'), findsOneWidget);
      expect(find.text('100.0'), findsOneWidget);

      // Trip icon
      expect(find.byIcon(Icons.local_shipping), findsOneWidget);
    });

    testWidgets('"OK, Got It" button dispatches AcceptServerVersion and pops',
        (WidgetTester tester) async {
      final conflict = _makeConflict();

      await tester.pumpWidget(
        BlocProvider<ConflictBloc>.value(
          value: conflictBloc,
          child: MaterialApp(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            locale: const Locale('en'),
            home: Scaffold(
              body: Builder(
                builder: (context) => ElevatedButton(
                  onPressed: () {
                    Navigator.of(context).push(
                      MaterialPageRoute<void>(
                        builder: (_) => BlocProvider<ConflictBloc>.value(
                          value: conflictBloc,
                          child: ConflictResolutionScreen(conflict: conflict),
                        ),
                      ),
                    );
                  },
                  child: const Text('Open'),
                ),
              ),
            ),
          ),
        ),
      );

      // Navigate to the conflict screen
      await tester.tap(find.text('Open'));
      await tester.pumpAndSettle();

      // Tap "OK, Got It"
      await tester.tap(find.text('OK, Got It'));
      await tester.pumpAndSettle();

      // Verify AcceptServerVersion was added to the bloc
      verify(
        () => conflictBloc.add(const bloc_event.AcceptServerVersion()),
      ).called(1);
    });

    testWidgets('"Dismiss" button dispatches DismissConflict and pops',
        (WidgetTester tester) async {
      final conflict = _makeConflict();

      await tester.pumpWidget(
        BlocProvider<ConflictBloc>.value(
          value: conflictBloc,
          child: MaterialApp(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            locale: const Locale('en'),
            home: Scaffold(
              body: Builder(
                builder: (context) => ElevatedButton(
                  onPressed: () {
                    Navigator.of(context).push(
                      MaterialPageRoute<void>(
                        builder: (_) => BlocProvider<ConflictBloc>.value(
                          value: conflictBloc,
                          child: ConflictResolutionScreen(conflict: conflict),
                        ),
                      ),
                    );
                  },
                  child: const Text('Open'),
                ),
              ),
            ),
          ),
        ),
      );

      // Navigate to the conflict screen
      await tester.tap(find.text('Open'));
      await tester.pumpAndSettle();

      // Tap "Dismiss"
      await tester.tap(find.text('Dismiss'));
      await tester.pumpAndSettle();

      // Verify DismissConflict was added to the bloc
      verify(
        () => conflictBloc.add(const bloc_event.DismissConflict()),
      ).called(1);
    });

    testWidgets('shows "No details available" for unknown entity type',
        (WidgetTester tester) async {
      final conflict = _makeConflict(
        entityType: 'unknown_type',
        serverData: {'foo': 'bar'},
      );

      await tester.pumpWidget(buildScreen(conflict));
      await tester.pumpAndSettle();

      // Unknown type returns empty fields → "No details available" text
      expect(find.text('No details available.'), findsOneWidget);

      // Falls back to sync_problem icon
      // Two sync_problem icons: one in header, one in entity badge
      expect(find.byIcon(Icons.sync_problem), findsNWidgets(2));
    });

    testWidgets('field diff rows display server values correctly',
        (WidgetTester tester) async {
      final conflict = _makeConflict(
        serverData: {
          'cropType': 'Wheat',
          'quantity': 100.0,
          'pricePerKg': 35.0,
          'status': 'sold',
        },
      );

      await tester.pumpWidget(buildScreen(conflict));
      await tester.pumpAndSettle();

      // "Updated values from server:" label
      expect(find.text('Updated values from server:'), findsOneWidget);

      // All four fields from _extractDisplayFields for crop_listing
      expect(find.text('Crop Type'), findsOneWidget);
      expect(find.text('Wheat'), findsOneWidget);
      expect(find.text('Quantity'), findsOneWidget);
      expect(find.text('100.0'), findsOneWidget);
      expect(find.text('Price/kg'), findsOneWidget);
      expect(find.text('35.0'), findsOneWidget);
      expect(find.text('Status'), findsOneWidget);
      expect(find.text('sold'), findsOneWidget);
    });

    testWidgets('explanation text is displayed', (WidgetTester tester) async {
      final conflict = _makeConflict();

      await tester.pumpWidget(buildScreen(conflict));
      await tester.pumpAndSettle();

      expect(
        find.textContaining(
          'Another device or the server updated this record',
        ),
        findsOneWidget,
      );
    });

    testWidgets('handles short entity ID without crashing',
        (WidgetTester tester) async {
      // entityId shorter than 8 chars — should display as-is
      final conflict = _makeConflict(entityId: 'abc');

      await tester.pumpWidget(buildScreen(conflict));
      await tester.pumpAndSettle();

      expect(find.textContaining('abc'), findsOneWidget);
    });

    testWidgets('handles null server data values with dashes',
        (WidgetTester tester) async {
      final conflict = _makeConflict(
        serverData: {
          // All null — should display as '-'
        },
      );

      await tester.pumpWidget(buildScreen(conflict));
      await tester.pumpAndSettle();

      // Each missing field renders as '-'
      expect(find.text('-'),
          findsNWidgets(4)); // cropType, quantity, pricePerKg, status
    });
  });
}
