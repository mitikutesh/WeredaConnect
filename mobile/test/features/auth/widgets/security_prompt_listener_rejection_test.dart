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

  testWidgets('a rejection with nothing to explain shows nothing',
      (tester) async {
    await tester.pumpWidget(host());

    events.emit(
      const SyncOperationRejected(
        entityType: 'crop_listing',
        entityId: 'crop-1',
        action: 'create',
        message: 'FRAUD_DUPLICATE_LISTING: duplicate',
      ),
    );
    await settle(tester);

    expect(find.byType(SnackBar), findsNothing);
  });
}
