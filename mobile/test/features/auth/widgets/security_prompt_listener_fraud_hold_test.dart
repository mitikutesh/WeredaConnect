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

const _held = 'FRAUD_ACCOUNT_HELD: Your account is under review. Some actions '
    'are paused. Contact support.';

AuthAuthenticated _signedIn(UserRole role) => AuthAuthenticated(
      userId: '${role.name}-1',
      tokens: const AuthTokens(
        accessToken: 'a',
        refreshToken: 'r',
        idToken: 'i',
      ),
      role: role,
      userName: 'User',
    );

/// Pump enough frames for the async event handler and the snackbar.
Future<void> _settle(WidgetTester tester) async {
  for (var i = 0; i < 10; i++) {
    await tester.pump(const Duration(milliseconds: 100));
  }
}

/// FRD-06: a queued change refused with FRAUD_ACCOUNT_HELD is explained as
/// "your account is under review", for every role, in all three languages.
void main() {
  late _MockAuthBloc auth;
  late StreamController<AuthState> states;
  late SecurityEvents events;

  setUp(() {
    auth = _MockAuthBloc();
    states = StreamController<AuthState>.broadcast();
    events = SecurityEvents.forTesting();
    when(() => auth.stream).thenAnswer((_) => states.stream);
  });

  tearDown(() {
    unawaited(states.close());
  });

  Widget host(Locale locale) => BlocProvider<AuthBloc>.value(
        value: auth,
        child: MaterialApp(
          locale: locale,
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

  for (final (role, entityType, action) in const [
    (UserRole.farmer, 'crop_listing', 'create'),
    (UserRole.trucker, 'trip', 'create'),
    (UserRole.buyer, 'order', 'create'),
    (UserRole.buyer, 'order', 'update'),
    (UserRole.buyer, 'payment_intent', 'initiate'),
  ]) {
    for (final locale in const [Locale('en'), Locale('am'), Locale('om')]) {
      testWidgets(
          '${role.name} $entityType $action says the account is under review '
          '(${locale.languageCode})', (tester) async {
        when(() => auth.state).thenReturn(_signedIn(role));
        await tester.pumpWidget(host(locale));

        events.emit(
          SyncOperationRejected(
            entityType: entityType,
            entityId: 'e-1',
            action: action,
            message: _held,
            userId: '${role.name}-1',
          ),
        );
        await _settle(tester);

        final l10n = lookupAppLocalizations(locale);
        expect(find.text(l10n.fraudAccountHeld), findsOneWidget);
        expect(find.textContaining('FRAUD_'), findsNothing);
        expect(find.text(l10n.errorOrderNotPlaced), findsNothing);
        expect(find.text(l10n.errorChangeNotSaved), findsNothing);
      });
    }
  }
}
