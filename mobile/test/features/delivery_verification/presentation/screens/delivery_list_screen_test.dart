import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/delivery.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/delivery_list_screen.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/widgets/delivery_card.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockDeliveryBloc extends Mock implements DeliveryBloc {}

class FakeAuthEvent extends Fake implements AuthEvent {}

class FakeAuthState extends Fake implements AuthState {}

class FakeDeliveryEvent extends Fake implements DeliveryEvent {}

class FakeDeliveryState extends Fake implements DeliveryState {}

const _testTokens = AuthTokens(
  accessToken: 'fake-access-token',
  refreshToken: 'fake-refresh-token',
  idToken: 'fake-id-token',
);

Delivery _makeDelivery({
  String deliveryId = 'delivery-001',
  DeliveryStatus status = DeliveryStatus.pending,
}) {
  return Delivery()
    ..deliveryId = deliveryId
    ..tripId = 'trip-001'
    ..listingId = 'listing-001'
    ..farmerId = 'farmer-001'
    ..farmerName = 'Abebe Kebede'
    ..truckerId = 'trucker-001'
    ..truckerName = 'Tadesse Worku'
    ..buyerId = 'buyer-001'
    ..buyerName = 'Mekonnen Tadesse'
    ..qrToken = 'test-token'
    ..status = status
    ..createdAt = DateTime(2026, 2, 20)
    ..updatedAt = DateTime(2026, 2, 20)
    ..version = 1;
}

void main() {
  late MockAuthBloc authBloc;
  late MockDeliveryBloc deliveryBloc;

  setUpAll(() {
    registerFallbackValue(FakeAuthEvent());
    registerFallbackValue(FakeAuthState());
    registerFallbackValue(FakeDeliveryEvent());
    registerFallbackValue(FakeDeliveryState());
  });

  setUp(() {
    authBloc = MockAuthBloc();
    deliveryBloc = MockDeliveryBloc();

    when(() => authBloc.state).thenReturn(
      const AuthAuthenticated(
        userId: 'farmer-001',
        tokens: _testTokens,
        role: UserRole.farmer,
      ),
    );
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    when(() => deliveryBloc.stream).thenAnswer((_) => const Stream.empty());
  });

  Widget buildTestWidget() {
    return MultiBlocProvider(
      providers: [
        BlocProvider<AuthBloc>.value(value: authBloc),
        BlocProvider<DeliveryBloc>.value(value: deliveryBloc),
      ],
      child: const MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        locale: Locale('en'),
        home: Scaffold(body: DeliveryListScreen()),
      ),
    );
  }

  group('DeliveryListScreen', () {
    testWidgets('shows loading indicator when DeliveryLoading state',
        (tester) async {
      when(() => deliveryBloc.state).thenReturn(const DeliveryLoading());

      await tester.pumpWidget(buildTestWidget());
      await tester.pump();

      expect(find.byType(CircularProgressIndicator), findsOneWidget);
    });

    testWidgets('shows error message with retry button on DeliveryError state',
        (tester) async {
      when(() => deliveryBloc.state).thenReturn(
        const DeliveryError(message: 'Network error'),
      );

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(find.text('Network error'), findsOneWidget);
      expect(find.byIcon(Icons.error_outline), findsOneWidget);
      expect(find.text('Try Again'), findsOneWidget);
    });

    testWidgets('shows empty state when no deliveries loaded', (tester) async {
      when(() => deliveryBloc.state).thenReturn(
        const DeliveriesLoaded(deliveries: []),
      );

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(find.text('No deliveries yet'), findsOneWidget);
      expect(find.text('Your deliveries will appear here'), findsOneWidget);
      expect(
          find.byIcon(Icons.local_shipping_outlined), findsAtLeastNWidgets(1));
    });

    testWidgets('shows delivery cards when deliveries are loaded',
        (tester) async {
      final deliveries = [
        _makeDelivery(deliveryId: 'delivery-001'),
        _makeDelivery(
          deliveryId: 'delivery-002',
          status: DeliveryStatus.inTransit,
        ),
      ];
      when(() => deliveryBloc.state).thenReturn(
        DeliveriesLoaded(deliveries: deliveries),
      );

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(find.byType(DeliveryCard), findsNWidgets(2));
    });

    testWidgets('status filter chips are rendered', (tester) async {
      when(() => deliveryBloc.state).thenReturn(
        const DeliveriesLoaded(deliveries: []),
      );

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(find.text('All'), findsOneWidget);
      expect(find.text('Pending'), findsOneWidget);
      expect(find.text('Picked Up'), findsOneWidget);
      expect(find.text('In Transit'), findsOneWidget);
      expect(find.text('Delivered'), findsOneWidget);
      expect(find.text('Disputed'), findsOneWidget);
      expect(find.text('Cancelled'), findsOneWidget);
    });

    testWidgets('tapping status filter chip dispatches FilterUserDeliveries',
        (tester) async {
      when(() => deliveryBloc.state).thenReturn(
        const DeliveriesLoaded(deliveries: []),
      );

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // Find the Pending filter chip and tap it
      final pendingChip = find.widgetWithText(FilterChip, 'Pending');
      expect(pendingChip, findsOneWidget);
      await tester.tap(pendingChip);
      await tester.pump();

      verify(
        () => deliveryBloc.add(
          const FilterUserDeliveries(
            userId: 'farmer-001',
            role: 'farmer',
            statusFilter: 'pending',
          ),
        ),
      ).called(1);
    });

    testWidgets('debug seed button visible in debug mode for empty list',
        (tester) async {
      when(() => deliveryBloc.state).thenReturn(
        const DeliveriesLoaded(deliveries: []),
      );

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      // In debug mode (tests run in debug), the seed button should be visible
      expect(find.byIcon(Icons.bug_report), findsOneWidget);
      expect(find.text('Seed Test Delivery'), findsOneWidget);
    });

    testWidgets('dispatches LoadUserDeliveries on init', (tester) async {
      when(() => deliveryBloc.state).thenReturn(const DeliveryInitial());

      await tester.pumpWidget(buildTestWidget());
      await tester.pump();

      verify(
        () => deliveryBloc.add(
          const LoadUserDeliveries(userId: 'farmer-001', role: 'farmer'),
        ),
      ).called(1);
    });

    testWidgets('shows My Deliveries title', (tester) async {
      when(() => deliveryBloc.state).thenReturn(
        const DeliveriesLoaded(deliveries: []),
      );

      await tester.pumpWidget(buildTestWidget());
      await tester.pumpAndSettle();

      expect(find.text('My Deliveries'), findsOneWidget);
    });
  });
}
