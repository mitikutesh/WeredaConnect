import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/config/app_update.dart';
import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/widgets/app_update_banner.dart';

const _message = 'Please update the app to place or change orders. '
    'Everything else still works.';

// Not the running build's number, so the tests hold after a bump.
const oldBuild = 7;
const newBuild = 8;

const _tooOld = SyncOperationRejected(
  entityType: 'order',
  entityId: 'o-1',
  action: 'update',
  message: 'APP_UPDATE_REQUIRED: too old',
);

/// Counts its own taps, so a test can tell whether its state survived.
class _Counter extends StatefulWidget {
  const _Counter();

  @override
  State<_Counter> createState() => _CounterState();
}

class _CounterState extends State<_Counter> {
  int _taps = 0;

  @override
  Widget build(BuildContext context) => TextButton(
        onPressed: () => setState(() => _taps++),
        child: Text('taps $_taps'),
      );
}

/// Mounts [widget] and lets the localizations load.
Future<void> _pump(WidgetTester tester, Widget widget) async {
  await tester.pumpWidget(widget);
  await tester.pumpAndSettle();
}

/// FUP-75: the persistent "update the app" banner over the shell.
void main() {
  setUp(() => SharedPreferences.setMockInitialValues({}));

  Widget host(
    SecurityEvents events, {
    Widget child = const _Counter(),
    bool isWeb = false,
    AppUpdateAction? openStore,
    AppUpdateAction? reloadWeb,
    Locale locale = const Locale('en'),
  }) =>
      MaterialApp(
        locale: locale,
        localizationsDelegates: const [
          AppLocalizations.delegate,
          FallbackMaterialLocalizationsDelegate(),
          GlobalWidgetsLocalizations.delegate,
          FallbackCupertinoLocalizationsDelegate(),
        ],
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: AppUpdateBanner(
            events: events,
            isWeb: isWeb,
            openStore: openStore ?? () async => true,
            reloadWeb: reloadWeb ?? () async => true,
            child: child,
          ),
        ),
      );

  testWidgets('hidden while the build is accepted', (tester) async {
    await _pump(tester, host(SecurityEvents.forTesting()));
    await tester.pumpAndSettle();

    expect(find.text(_message), findsNothing);
    expect(find.text('taps 0'), findsOneWidget);
  });

  testWidgets('a refusal while mounted shows it; the screen keeps its state',
      (tester) async {
    final events = SecurityEvents.forTesting();
    await _pump(tester, host(events));
    await tester.tap(find.text('taps 0'));
    await tester.pump();

    events.emit(_tooOld);
    await tester.pumpAndSettle();

    expect(find.text(_message), findsOneWidget);
    expect(find.text('Update'), findsOneWidget);
    // Not rebuilt from scratch, and still usable under the banner.
    expect(find.text('taps 1'), findsOneWidget);
    await tester.tap(find.text('taps 1'));
    await tester.pump();
    expect(find.text('taps 2'), findsOneWidget);
  });

  testWidgets('a refusal before the shell mounted shows it at once',
      (tester) async {
    final events = SecurityEvents.forTesting()..emit(_tooOld);

    await _pump(tester, host(events));

    expect(find.text(_message), findsOneWidget);
  });

  testWidgets('a web refusal shows it too', (tester) async {
    final events = SecurityEvents.forTesting();
    await _pump(tester, host(events, isWeb: true));

    events.reportAppUpdateRequired();
    await tester.pumpAndSettle();

    expect(find.text(_message), findsOneWidget);
    expect(find.text('Reload page'), findsOneWidget);
  });

  testWidgets('comes back after a restart on the same build', (tester) async {
    const store = AppUpdateFlagStore();
    // The run that was refused.
    SecurityEvents.forTesting(appUpdateStore: store, appBuild: oldBuild)
        .emit(_tooOld);
    await tester.pumpAndSettle();
    expect(
      (await SharedPreferences.getInstance()).getInt(AppUpdateFlagStore.key),
      oldBuild,
    );

    // The next run, same build: nothing refused yet.
    final restarted = SecurityEvents.forTesting(
      appUpdateStore: store,
      appBuild: oldBuild,
    );
    await _pump(tester, host(restarted));
    await tester.pumpAndSettle();

    expect(restarted.appUpdateRequired, isTrue);
    expect(find.text(_message), findsOneWidget);
  });

  testWidgets('gone after the update (a new build), and forgotten',
      (tester) async {
    SharedPreferences.setMockInitialValues({AppUpdateFlagStore.key: oldBuild});
    final updated = SecurityEvents.forTesting(
      appUpdateStore: const AppUpdateFlagStore(),
      appBuild: newBuild,
    );

    await _pump(tester, host(updated));
    await tester.pumpAndSettle();

    expect(find.text(_message), findsNothing);
    expect(
      (await SharedPreferences.getInstance()).getInt(AppUpdateFlagStore.key),
      isNull,
    );
  });

  testWidgets('phone: the button opens the store page', (tester) async {
    var opened = 0;
    var reloaded = 0;
    await _pump(
      tester,
      host(
        SecurityEvents.forTesting()..emit(_tooOld),
        openStore: () async {
          opened++;
          return true;
        },
        reloadWeb: () async {
          reloaded++;
          return true;
        },
      ),
    );

    await tester.tap(find.text('Update'));
    await tester.pumpAndSettle();

    expect(opened, 1);
    expect(reloaded, 0);
    expect(find.byType(SnackBar), findsNothing);
    // The banner stays until the app is updated.
    expect(find.text(_message), findsOneWidget);
  });

  testWidgets('phone: a store that does not open is said', (tester) async {
    await _pump(
      tester,
      host(
        SecurityEvents.forTesting()..emit(_tooOld),
        openStore: () async => false,
      ),
    );

    await tester.tap(find.text('Update'));
    await tester.pumpAndSettle();

    expect(
      find.text(
        'Could not open the store. Please install the latest WeredaConnect '
        'from where you got the app.',
      ),
      findsOneWidget,
    );
  });

  testWidgets('web: the button reloads the latest app', (tester) async {
    var opened = 0;
    var reloaded = 0;
    await _pump(
      tester,
      host(
        SecurityEvents.forTesting()..emit(_tooOld),
        isWeb: true,
        openStore: () async {
          opened++;
          return true;
        },
        reloadWeb: () async {
          reloaded++;
          return true;
        },
      ),
    );

    expect(find.text('Update'), findsNothing);
    await tester.tap(find.text('Reload page'));
    await tester.pumpAndSettle();

    expect(reloaded, 1);
    expect(opened, 0);
  });

  testWidgets('the screen under it does not pad for the status bar again',
      (tester) async {
    final events = SecurityEvents.forTesting();
    double? top;
    await _pump(
      tester,
      MediaQuery(
        data: const MediaQueryData(padding: EdgeInsets.only(top: 24)),
        child: host(
          events,
          child: Builder(
            builder: (context) {
              top = MediaQuery.paddingOf(context).top;
              return const SizedBox.expand();
            },
          ),
        ),
      ),
    );
    expect(top, 24);

    events.emit(_tooOld);
    await tester.pumpAndSettle();

    expect(top, 0);
  });

  for (final (locale, text) in [
    (
      const Locale('am'),
      'ትዕዛዝ ለማስገባት ወይም ለመቀየር እባክዎ መተግበሪያውን ያዘምኑ። '
          'ሌላው ሁሉ እንደተለመደው ይሰራል።',
    ),
    (
      const Locale('om'),
      'Ajaja galmeessuuf ykn jijjiiruuf maaloo appii haaromsaa. '
          'Wantoonni biroo hundi akkuma duraatti hojjetu.',
    ),
  ]) {
    testWidgets('reads in ${locale.languageCode}', (tester) async {
      await _pump(
        tester,
        host(SecurityEvents.forTesting()..emit(_tooOld), locale: locale),
      );
      await tester.pumpAndSettle();

      expect(find.text(text), findsOneWidget);
    });
  }

  test('the store page is the Android app id on Play', () {
    expect(
      AppStore.playStorePage.toString(),
      'https://play.google.com/store/apps/details?id=et.wereda.connect',
    );
  });

  test('off the web there is no page to reload', () async {
    expect(await reloadLatestWebApp(), isFalse);
  });
}
