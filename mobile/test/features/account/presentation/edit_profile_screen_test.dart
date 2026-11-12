import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mocktail/mocktail.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/account/domain/profile_edit.dart';
import 'package:wereda_connect/features/account/presentation/screens/edit_profile_screen.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

class MockAuthBloc extends Mock implements AuthBloc {}

class _FakeRepository implements BaseProfileRepository {
  ProfileValues? server;
  bool unsent = false;
  Object? saveError;
  ProfileSaveOutcome outcome = ProfileSaveOutcome.queued;
  final saved = <ProfileChanges>[];
  int fetches = 0;

  /// When set, the read waits for it (a slow network).
  Completer<void>? fetchGate;

  /// When set, the save waits for it (a slow web save).
  Completer<void>? saveGate;

  @override
  Future<ProfileValues?> fetch(String userId) async {
    fetches++;
    await fetchGate?.future;
    return server;
  }

  @override
  Future<bool> hasUnsentEdit(String userId) async => unsent;

  @override
  Future<ProfileSaveOutcome> save({
    required String userId,
    required ProfileChanges changes,
  }) async {
    await saveGate?.future;
    final error = saveError;
    if (error != null) throw error;
    saved.add(changes);
    return outcome;
  }
}

AuthAuthenticated _signedIn(UserRole role) => AuthAuthenticated(
      userId: '${role.name}-1',
      tokens: const AuthTokens(
        accessToken: 'a',
        refreshToken: 'r',
        idToken: 'i',
      ),
      role: role,
      userName: 'Abebe',
      phoneNumber: '+251911234567',
      woreda: 'Adama',
    );

/// SIM-56: Profile > Edit profile, the same for every role on mobile and web.
void main() {
  late MockAuthBloc bloc;
  late StreamController<AuthState> states;
  late _FakeRepository repository;
  late List<ProfileValues> written;

  setUpAll(() {
    registerFallbackValue(const AuthCheckRequested());
  });

  setUp(() {
    bloc = MockAuthBloc();
    states = StreamController<AuthState>.broadcast();
    when(() => bloc.stream).thenAnswer((_) => states.stream);
    repository = _FakeRepository();
    written = [];
  });

  tearDown(() {
    unawaited(states.close());
  });

  /// Pushes the screen from a home that records what it popped.
  Future<List<Object?>> open(
    WidgetTester tester, {
    required UserRole role,
    ProfileValues current = const ProfileValues(
      displayName: 'Abebe',
      woreda: 'Adama',
    ),
  }) async {
    when(() => bloc.state).thenReturn(_signedIn(role));
    final popped = <Object?>[];
    tester.view.physicalSize = const Size(800, 2000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      BlocProvider<AuthBloc>.value(
        value: bloc,
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          locale: const Locale('en'),
          home: Builder(
            builder: (context) => Scaffold(
              body: TextButton(
                onPressed: () async {
                  popped.add(
                    await Navigator.of(context).push<bool>(
                      MaterialPageRoute<bool>(
                        builder: (_) => EditProfileScreen(
                          loadCurrent: (_) async => current,
                          repository: repository,
                          saveLocal: (userId, phone, values) async =>
                              written.add(values),
                        ),
                      ),
                    ),
                  );
                },
                child: const Text('open'),
              ),
            ),
          ),
        ),
      ),
    );
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    return popped;
  }

  final name = find.byKey(const Key('profile_edit_name'));
  final company = find.byKey(const Key('profile_edit_company'));
  final save = find.byKey(const Key('profile_edit_save'));

  String fieldText(WidgetTester tester, Finder field) => tester
      .widget<EditableText>(
        find.descendant(of: field, matching: find.byType(EditableText)),
      )
      .controller
      .text;

  const companyBuyer = ProfileValues(
    displayName: 'Almaz',
    woreda: 'Meki',
    role: 'buyer',
    buyerType: 'company',
    companyName: 'Meki Traders',
  );

  for (final role in UserRole.values) {
    testWidgets('${role.name}: the form starts from the saved profile',
        (tester) async {
      await open(tester, role: role);

      expect(find.text('Edit profile'), findsOneWidget);
      expect(fieldText(tester, name), 'Abebe');
      expect(
        fieldText(tester, find.byKey(const Key('profile_edit_woreda'))),
        'Adama',
      );
      // Only a company buyer has a company name.
      expect(company, findsNothing);
      expect(find.textContaining('keep the old details'), findsOneWidget);
    });

    testWidgets('${role.name}: saving queues only what changed',
        (tester) async {
      final popped = await open(tester, role: role);

      await tester.enterText(name, '  Abebe K. ');
      await tester.tap(save);
      await tester.pumpAndSettle();

      expect(repository.saved, [const ProfileChanges(displayName: 'Abebe K.')]);
      expect(
        written.single,
        const ProfileValues(displayName: 'Abebe K.', woreda: 'Adama'),
      );
      verify(
        () => bloc.add(
          AuthProfileChanged(
            userId: '${role.name}-1',
            userName: 'Abebe K.',
            woreda: 'Adama',
          ),
        ),
      ).called(1);
      expect(popped, [true]);
      expect(
        find.text('Profile saved on this phone. It is sent when you are '
            'online.'),
        findsOneWidget,
      );
    });
  }

  testWidgets('a company buyer edits the company name too', (tester) async {
    repository.outcome = ProfileSaveOutcome.saved;
    await open(tester, role: UserRole.buyer, current: companyBuyer);

    expect(fieldText(tester, company), 'Meki Traders');
    await tester.enterText(company, 'Meki Grain Traders');
    await tester.tap(save);
    await tester.pumpAndSettle();

    expect(
      repository.saved,
      [const ProfileChanges(companyName: 'Meki Grain Traders')],
    );
    expect(written.single.companyName, 'Meki Grain Traders');
    expect(find.text('Profile saved.'), findsOneWidget);
  });

  testWidgets('a phone number in the name is refused before anything is sent',
      (tester) async {
    await open(tester, role: UserRole.farmer);

    await tester.enterText(name, 'Abebe 0911 22 33 44');
    await tester.tap(save);
    await tester.pumpAndSettle();

    expect(
      find.textContaining('Phone numbers are not allowed here'),
      findsOneWidget,
    );
    expect(repository.saved, isEmpty);
    expect(written, isEmpty);
  });

  testWidgets('an empty name and the deleted-account name are refused',
      (tester) async {
    await open(tester, role: UserRole.trucker);

    await tester.enterText(name, '   ');
    await tester.tap(save);
    await tester.pumpAndSettle();
    expect(find.text('This field is required'), findsOneWidget);

    await tester.enterText(name, 'Deleted user');
    await tester.tap(save);
    await tester.pumpAndSettle();
    expect(
      find.text('This name is not available. Choose another.'),
      findsOneWidget,
    );
    expect(repository.saved, isEmpty);
  });

  testWidgets('nothing changed closes the screen without sending',
      (tester) async {
    final popped = await open(tester, role: UserRole.farmer);

    await tester.tap(save);
    await tester.pumpAndSettle();

    expect(repository.saved, isEmpty);
    expect(popped, [false]);
  });

  testWidgets('a server refusal for a phone number marks that field (web)',
      (tester) async {
    repository.saveError = const SyncRejectedException(
      'AUTH_027: Remove the phone number from woreda. Contact opens after '
      'payment is held in escrow.',
    );
    final popped = await open(tester, role: UserRole.buyer);

    await tester.enterText(name, 'Almaz');
    await tester.tap(save);
    await tester.pumpAndSettle();

    expect(
      find.textContaining('Phone numbers are not allowed here'),
      findsOneWidget,
    );
    expect(written, isEmpty);
    expect(popped, isEmpty);
  });

  testWidgets('nothing saved says so and keeps the form', (tester) async {
    repository.saveError = const NetworkException('offline');
    final popped = await open(tester, role: UserRole.farmer);

    await tester.enterText(name, 'Abebe K.');
    await tester.tap(save);
    await tester.pumpAndSettle();

    expect(
      find.text('Your profile could not be saved. Check your connection and '
          'try again.'),
      findsOneWidget,
    );
    expect(written, isEmpty);
    expect(popped, isEmpty);
    verifyNever(() => bloc.add(any(that: isA<AuthProfileChanged>())));
  });

  testWidgets("the server's newer copy fills the form and the device",
      (tester) async {
    repository.server = companyBuyer;
    await open(tester, role: UserRole.buyer);

    expect(repository.fetches, 1);
    expect(fieldText(tester, name), 'Almaz');
    expect(fieldText(tester, company), 'Meki Traders');
    expect(written.single, companyBuyer);
    verify(
      () => bloc.add(
        const AuthProfileChanged(
          userId: 'buyer-1',
          userName: 'Almaz',
          woreda: 'Meki',
        ),
      ),
    ).called(1);
  });

  testWidgets('an edit still waiting on the phone keeps the phone copy',
      (tester) async {
    repository
      ..server = companyBuyer
      ..unsent = true;
    await open(tester, role: UserRole.buyer);

    expect(fieldText(tester, name), 'Abebe');
    expect(written, isEmpty);
    verifyNever(() => bloc.add(any(that: isA<AuthProfileChanged>())));
  });

  testWidgets('an account an admin moved away from buyer has no company field',
      (tester) async {
    await open(
      tester,
      role: UserRole.farmer,
      current: const ProfileValues(
        displayName: 'Almaz',
        woreda: 'Meki',
        role: 'farmer',
        buyerType: 'company',
        companyName: 'Meki Traders',
      ),
    );

    expect(company, findsNothing);
    await tester.enterText(name, 'Almaz T.');
    await tester.tap(save);
    await tester.pumpAndSettle();

    expect(repository.saved, [const ProfileChanges(displayName: 'Almaz T.')]);
  });

  testWidgets('a server read that ends after a save started is not applied',
      (tester) async {
    repository
      ..server = const ProfileValues(displayName: 'Old name', woreda: 'Adama')
      ..fetchGate = Completer<void>()
      ..saveGate = Completer<void>()
      ..outcome = ProfileSaveOutcome.saved;
    await open(tester, role: UserRole.trucker);

    // The save is in flight when the old read comes back.
    await tester.enterText(name, 'New name');
    await tester.tap(save);
    await tester.pump();
    repository.fetchGate!.complete();
    await tester.pump();
    repository.saveGate!.complete();
    await tester.pumpAndSettle();

    // Only the save reached the device's copy.
    expect(
      written,
      [const ProfileValues(displayName: 'New name', woreda: 'Adama')],
    );
    verifyNever(
      () => bloc.add(
        const AuthProfileChanged(
          userId: 'trucker-1',
          userName: 'Old name',
          woreda: 'Adama',
        ),
      ),
    );
  });

  testWidgets('an unchanged field is not checked, like on the server',
      (tester) async {
    // A woreda stored before FUP-52 still lets the name change.
    await open(
      tester,
      role: UserRole.farmer,
      current: const ProfileValues(
        displayName: 'Abebe',
        woreda: 'Adama 0911223344',
      ),
    );

    await tester.enterText(name, 'Abebe K.');
    await tester.tap(save);
    await tester.pumpAndSettle();

    expect(repository.saved, [const ProfileChanges(displayName: 'Abebe K.')]);
  });

  testWidgets('a refusal naming no field says the details were not accepted',
      (tester) async {
    repository.saveError = const SyncRejectedException(
      'USER_PROFILE_VALIDATION: companyName can only be changed by a company '
      'buyer.',
    );
    await open(tester, role: UserRole.buyer);

    await tester.enterText(name, 'Almaz');
    await tester.tap(save);
    await tester.pumpAndSettle();

    expect(
      find.text(
        'These details were not accepted. Check each field and try again.',
      ),
      findsOneWidget,
    );
    expect(find.textContaining('Check your connection'), findsNothing);
    expect(written, isEmpty);
  });
}
