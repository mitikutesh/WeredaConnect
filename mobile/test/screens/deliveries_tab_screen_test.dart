import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/database/schemas/user.dart';
import 'package:wereda_connect/core/models/delivery.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/screens/deliveries_tab_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/layout/breakpoints.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class MockDeliveryBloc extends Mock implements DeliveryBloc {}

Delivery _delivery({
  String id = 'del-12345678',
  String farmerName = 'Abebe',
  String truckerName = 'Kebede',
  String buyerName = 'Almaz',
}) =>
    Delivery()
      ..deliveryId = id
      ..tripId = 't1'
      ..listingId = 'l1'
      ..farmerId = 'f1'
      ..farmerName = farmerName
      ..truckerId = 'tr1'
      ..truckerName = truckerName
      ..buyerId = 'b1'
      ..buyerName = buyerName
      ..qrToken = 'qr'
      ..status = DeliveryStatus.inTransit
      ..createdAt = DateTime(2026)
      ..updatedAt = DateTime(2026)
      ..version = 1;

/// LT-01: the deliveries list (a tab for all three roles) ends clear of the
/// floating create button.
void main() {
  late MockAuthBloc authBloc;
  late MockDeliveryBloc deliveryBloc;

  setUp(() {
    authBloc = MockAuthBloc();
    deliveryBloc = MockDeliveryBloc();
    when(() => authBloc.stream).thenAnswer((_) => const Stream.empty());
    final state = DeliveriesLoaded(deliveries: [_delivery()]);
    when(() => deliveryBloc.state).thenReturn(state);
    when(() => deliveryBloc.stream).thenAnswer((_) => const Stream.empty());
  });

  void signIn(UserRole role) {
    when(() => authBloc.state).thenReturn(
      AuthAuthenticated(
        userId: '${role.name}-1',
        tokens: const AuthTokens(
          accessToken: 'a',
          refreshToken: 'r',
          idToken: 'i',
        ),
        role: role,
      ),
    );
  }

  Future<void> pumpWide(WidgetTester tester) async {
    tester.view.physicalSize = const Size(1440, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      MultiBlocProvider(
        providers: [
          BlocProvider<AuthBloc>.value(value: authBloc),
          BlocProvider<DeliveryBloc>.value(value: deliveryBloc),
        ],
        child: const MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: Scaffold(body: DeliveriesTabScreen()),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  for (final role in UserRole.values) {
    testWidgets(
        '${role.name}: a wide screen with no deliveries shows the '
        'empty state, never sample deliveries', (tester) async {
      signIn(role);
      when(() => deliveryBloc.state)
          .thenReturn(const DeliveriesLoaded(deliveries: []));

      await pumpWide(tester);

      expect(find.text('No deliveries yet'), findsOneWidget);
      expect(find.textContaining('Selam Traders'), findsNothing);
      expect(find.textContaining('Almaz Bekele'), findsNothing);
    });
  }

  testWidgets('wide screen search narrows the list by name', (tester) async {
    signIn(UserRole.trucker);
    when(() => deliveryBloc.state).thenReturn(
      DeliveriesLoaded(
        deliveries: [
          _delivery(),
          _delivery(
            id: 'del-87654321',
            farmerName: 'Tolosa',
            truckerName: 'Chala',
            buyerName: 'Hiwot',
          ),
        ],
      ),
    );

    await pumpWide(tester);
    expect(find.textContaining('Almaz'), findsWidgets);
    expect(find.textContaining('Hiwot'), findsWidgets);

    await tester.enterText(find.byKey(const Key('deliveries_search')), 'hiw');
    await tester.pumpAndSettle();
    expect(find.textContaining('Almaz'), findsNothing);
    expect(find.textContaining('Hiwot'), findsWidgets);

    await tester.enterText(find.byKey(const Key('deliveries_search')), '');
    await tester.pumpAndSettle();
    expect(find.textContaining('Almaz'), findsWidgets);
  });

  for (final role in UserRole.values) {
    testWidgets('${role.name}: the list ends clear of the FAB', (tester) async {
      when(() => authBloc.state).thenReturn(
        AuthAuthenticated(
          userId: '${role.name}-1',
          tokens: const AuthTokens(
            accessToken: 'a',
            refreshToken: 'r',
            idToken: 'i',
          ),
          role: role,
        ),
      );

      await tester.pumpWidget(
        MultiBlocProvider(
          providers: [
            BlocProvider<AuthBloc>.value(value: authBloc),
            BlocProvider<DeliveryBloc>.value(value: deliveryBloc),
          ],
          child: const MaterialApp(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            home: Scaffold(body: DeliveriesTabScreen()),
          ),
        ),
      );
      await tester.pumpAndSettle();

      expect(find.textContaining('Almaz'), findsWidgets);
      final list = tester.widget<ListView>(
        find.descendant(
          of: find.byType(RefreshIndicator),
          matching: find.byType(ListView),
        ),
      );
      expect(
        list.padding!.resolve(TextDirection.ltr).bottom,
        Breakpoints.fabClearance,
      );
    });
  }
}
