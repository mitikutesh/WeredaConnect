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

Delivery _delivery() => Delivery()
  ..deliveryId = 'del-12345678'
  ..tripId = 't1'
  ..listingId = 'l1'
  ..farmerId = 'f1'
  ..farmerName = 'Abebe'
  ..truckerId = 'tr1'
  ..truckerName = 'Kebede'
  ..buyerId = 'b1'
  ..buyerName = 'Almaz'
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
