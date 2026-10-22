import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/widgets/security_prompt_listener.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';

class _MockAuthBloc extends Mock implements AuthBloc {}

const _signedIn = AuthAuthenticated(
  userId: 'farmer-1',
  tokens: AuthTokens(accessToken: 'a', refreshToken: 'r', idToken: 'i'),
  role: UserRole.farmer,
  userName: 'Almaz',
  phoneNumber: '+251900100001',
);

SyncOperationRejected _rejected({String action = 'update'}) =>
    SyncOperationRejected(
      entityType: 'order',
      entityId: 'order-1',
      action: action,
      message: 'ORDER_VALIDATION: Invalid status transition.',
    );

/// Pump enough frames for the async event handler and the snackbar.
Future<void> settle(WidgetTester tester) async {
  for (var i = 0; i < 10; i++) {
    await tester.pump(const Duration(milliseconds: 100));
  }
}

/// ESC-12: a queued order change the server refused for good is explained in
/// the user's language, even when it was refused before the shell mounted.
void main() {
  late _MockAuthBloc auth;
  late StreamController<AuthState> states;
  late SecurityEvents events;

  setUp(() {
    auth = _MockAuthBloc();
    states = StreamController<AuthState>.broadcast();
    events = SecurityEvents.forTesting();
    when(() => auth.state).thenReturn(_signedIn);
    when(() => auth.stream).thenAnswer((_) => states.stream);
  });

  tearDown(() {
    unawaited(states.close());
  });

  Widget host({Locale locale = const Locale('en')}) =>
      BlocProvider<AuthBloc>.value(
        value: auth,
        child: MaterialApp(
          locale: locale,
          // Same delegates as the app: Material has no Afan Oromo, so om
          // falls back like it does in main.dart.
          localizationsDelegates: const [
            AppLocalizations.delegate,
            FallbackMaterialLocalizationsDelegate(),
            GlobalWidgetsLocalizations.delegate,
            FallbackCupertinoLocalizationsDelegate(),
          ],
          supportedLocales: AppLocalizations.supportedLocales,
          home: Scaffold(
            body: SecurityPromptListener(
              events: events,
              child: const Text('app'),
            ),
          ),
        ),
      );

  testWidgets('a refused order change says it was not allowed', (tester) async {
    await tester.pumpWidget(host());

    events.emit(_rejected());
    await settle(tester);

    expect(
      find.text(lookupAppLocalizations(const Locale('en')).errorOrderRefused),
      findsOneWidget,
    );
  });

  testWidgets('a refused order create says the order was removed',
      (tester) async {
    await tester.pumpWidget(host());

    events.emit(_rejected(action: 'create'));
    await settle(tester);

    expect(
      find.text(lookupAppLocalizations(const Locale('en')).errorOrderNotPlaced),
      findsOneWidget,
    );
  });

  for (final code in ['am', 'om']) {
    testWidgets('the reason is localized ($code)', (tester) async {
      await tester.pumpWidget(host(locale: Locale(code)));

      events.emit(_rejected());
      await settle(tester);

      final text = lookupAppLocalizations(Locale(code)).errorOrderRefused;
      expect(
        text,
        isNot(lookupAppLocalizations(const Locale('en')).errorOrderRefused),
      );
      expect(find.text(text), findsOneWidget);
    });
  }

  testWidgets('a rejection raised before the shell mounted is still shown',
      (tester) async {
    events.emit(_rejected());

    await tester.pumpWidget(host());
    await settle(tester);

    expect(
      find.text(lookupAppLocalizations(const Locale('en')).errorOrderRefused),
      findsOneWidget,
    );
  });

  testWidgets('nothing is shown when signed out', (tester) async {
    when(() => auth.state).thenReturn(const AuthUnauthenticated());
    await tester.pumpWidget(host());

    events.emit(_rejected());
    await settle(tester);

    expect(find.byType(SnackBar), findsNothing);
  });

  testWidgets('a rejection does not wait for, or close, an open prompt',
      (tester) async {
    await tester.pumpWidget(host());
    // The fallback delegates load asynchronously; a prompt (unlike a
    // rejection) is not kept for a listener that has not mounted yet.
    await settle(tester);
    events.emit(const StepUpRequired());
    await settle(tester);
    expect(find.text("Confirm it's you"), findsOneWidget);

    events.emit(_rejected());
    await settle(tester);

    expect(
      find.text(lookupAppLocalizations(const Locale('en')).errorOrderRefused),
      findsOneWidget,
    );
    expect(find.text("Confirm it's you"), findsOneWidget);
  });

  final en = lookupAppLocalizations(const Locale('en'));

  testWidgets('a refused duplicate listing keeps the listing-form wording',
      (tester) async {
    await tester.pumpWidget(host());

    events.emit(
      const SyncOperationRejected(
        entityType: 'crop_listing',
        entityId: 'crop-1',
        action: 'create',
        message: 'FRAUD_DUPLICATE_LISTING: duplicate',
        userId: 'farmer-1',
      ),
    );
    await settle(tester);

    expect(find.text(en.fraudDuplicateListing), findsOneWidget);
  });

  testWidgets('any other refused change says it was not saved', (tester) async {
    await tester.pumpWidget(host());

    events.emit(
      const SyncOperationRejected(
        entityType: 'delivery',
        entityId: 'delivery-1',
        action: 'create',
        message: 'DELIVERY_ORDER_REQUIRED: missing orderId',
      ),
    );
    await settle(tester);

    expect(find.text(en.errorChangeNotSaved), findsOneWidget);
  });

  // ESC-08: the local cancel was undone because the goods were picked up.
  testWidgets('a delivery cancel refused after pickup says why',
      (tester) async {
    await tester.pumpWidget(host());

    events.emit(
      const SyncOperationRejected(
        entityType: 'delivery',
        entityId: 'delivery-1',
        action: 'update',
        message: 'DELIVERY_CANCEL_NOT_ALLOWED: only before pickup.',
        userId: 'farmer-1',
      ),
    );
    await settle(tester);

    expect(find.text(en.deliveryErrorCancelNotAllowed), findsOneWidget);
  });

  // ESC-07: the local dispute was undone because the payment could not be
  // frozen.
  testWidgets('a refused delivery dispute says why', (tester) async {
    await tester.pumpWidget(host());

    events.emit(
      const SyncOperationRejected(
        entityType: 'delivery',
        entityId: 'delivery-1',
        action: 'update',
        message: 'DELIVERY_DISPUTE_REFUSED: being settled.',
        userId: 'farmer-1',
      ),
    );
    await settle(tester);

    expect(find.text(en.deliveryErrorDisputeRefused), findsOneWidget);
  });

  testWidgets('another user\'s refusal is not shown (shared device)',
      (tester) async {
    // Kept before this user signed in, raised for someone else's op.
    events.emit(
      const SyncOperationRejected(
        entityType: 'order',
        entityId: 'order-9',
        action: 'update',
        message: 'ORDER_VALIDATION: no',
        userId: 'farmer-2',
      ),
    );
    await tester.pumpWidget(host());
    await settle(tester);

    expect(find.byType(SnackBar), findsNothing);
  });

  testWidgets('a refused batch reads as one message, not a queue of them',
      (tester) async {
    await tester.pumpWidget(host());

    for (var i = 0; i < 20; i++) {
      events.emit(
        SyncOperationRejected(
          entityType: 'order',
          entityId: 'order-$i',
          action: 'update',
          message: '',
          userId: 'farmer-1',
        ),
      );
    }
    await settle(tester);

    expect(find.text(en.errorOrderRefused), findsOneWidget);
    // Nothing else is waiting behind it.
    ScaffoldMessenger.of(tester.element(find.text('app')))
        .removeCurrentSnackBar();
    await settle(tester);
    expect(find.byType(SnackBar), findsNothing);
  });

  // ESC-12 part B (sync-protocol.md 1.17.0): every spec section 8 code says
  // what happened and what to do, for the farmer, the trucker and the buyer.
  group('part B codes (FUP-01)', () {
    for (final (entityType, action, message, text) in <(
      String,
      String,
      String,
      String Function(AppLocalizations),
    )>[
      // Farmer.
      (
        'crop_listing',
        'create',
        'REPUTATION_RESTRICTED: restricted',
        (l) => l.errorReputationRestricted,
      ),
      (
        'crop_listing',
        'delete',
        'CROP_HAS_ACTIVE_ORDERS: open orders',
        (l) => l.errorCropHasActiveOrders,
      ),
      (
        'order',
        'update',
        'REPUTATION_RESTRICTED: restricted',
        (l) => l.errorReputationRestricted,
      ),
      (
        'order',
        'update',
        'ORDER_DEADLINE_PASSED: too late',
        (l) => l.errorDeadlinePassed,
      ),
      // Trucker.
      (
        'trip',
        'create',
        'REPUTATION_RESTRICTED: restricted',
        (l) => l.errorReputationRestricted,
      ),
      (
        'trip',
        'delete',
        'TRIP_HAS_BOOKINGS: booked',
        (l) => l.errorTripHasBookings,
      ),
      // Buyer.
      (
        'order',
        'create',
        'REPUTATION_RESTRICTED: owner restricted',
        (l) => l.errorCounterpartyRestricted,
      ),
      (
        'order',
        'create',
        'TRIP_CAPACITY_EXCEEDED: full',
        (l) => l.errorTripCapacityExceeded,
      ),
      (
        'order',
        'create',
        'ORDER_TRIP_NOT_ELIGIBLE: route',
        (l) => l.errorTripNotEligible,
      ),
      (
        'order',
        'update',
        'ORDER_TRANSPORT_ATTEMPTS_EXCEEDED: 3 tried',
        (l) => l.errorTransportAttemptsExceeded,
      ),
      // Anyone on an old build.
      (
        'crop_listing',
        'create',
        'APP_UPDATE_REQUIRED: minimum 2.0.0',
        (l) => l.errorAppUpdateRequired,
      ),
      (
        'order',
        'create',
        'APP_UPDATE_REQUIRED: minimum 2.0.0',
        (l) => l.errorAppUpdateRequired,
      ),
    ]) {
      testWidgets('$entityType $action ${message.split(':').first}',
          (tester) async {
        await tester.pumpWidget(host());

        events.emit(
          SyncOperationRejected(
            entityType: entityType,
            entityId: '$entityType-1',
            action: action,
            message: message,
            userId: 'farmer-1',
          ),
        );
        await settle(tester);

        expect(find.text(text(en)), findsOneWidget);
        expect(find.text(en.errorChangeNotSaved), findsNothing);
        expect(find.text(en.errorOrderNotPlaced), findsNothing);
      });
    }

    for (final code in ['am', 'om']) {
      testWidgets('a refused trip change is localized ($code)', (tester) async {
        await tester.pumpWidget(host(locale: Locale(code)));

        events.emit(
          const SyncOperationRejected(
            entityType: 'trip',
            entityId: 'trip-1',
            action: 'delete',
            message: 'TRIP_HAS_BOOKINGS: booked',
          ),
        );
        await settle(tester);

        final text = lookupAppLocalizations(Locale(code)).errorTripHasBookings;
        expect(text, isNot(en.errorTripHasBookings));
        expect(find.text(text), findsOneWidget);
      });
    }
  });
}
