import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:wereda_connect/core/security/security_events.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/widgets/app_update_banner.dart';

const _message = 'Please update the app to place or change orders. '
    'Everything else still works.';
const _storeHint = 'If the store does not open or does not show the app, '
    'get the latest WeredaConnect from where you got it.';

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

  testWidgets('an accepted order hides it; the screen keeps its state',
      (tester) async {
    final events = SecurityEvents.forTesting()..emit(_tooOld);
    await _pump(tester, host(events));
    await tester.tap(find.text('taps 0'));
    await tester.pump();
    expect(find.text(_message), findsOneWidget);

    events.clearAppUpdateRequired();
    await tester.pumpAndSettle();

    expect(find.text(_message), findsNothing);
    expect(find.text('taps 1'), findsOneWidget);
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

  // FUP-242: the Play listing is not live, so a launch that "worked" may
  // land on a page without the app. The second line says what to do.
  testWidgets('phone: a second line says where else to get the app',
      (tester) async {
    await _pump(
      tester,
      host(
        SecurityEvents.forTesting()..emit(_tooOld),
        openStore: () async => false,
      ),
    );
    expect(find.text(_storeHint), findsOneWidget);

    await tester.tap(find.text('Update'));
    await tester.pumpAndSettle();

    expect(find.byType(SnackBar), findsNothing);
    expect(find.text(_storeHint), findsOneWidget);
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
    expect(find.text(_storeHint), findsNothing);
    await tester.tap(find.text('Reload page'));
    await tester.pumpAndSettle();

    expect(reloaded, 1);
    expect(opened, 0);
    expect(find.byType(SnackBar), findsNothing);
  });

  testWidgets(
      'web: the button waits for the reload; one that cannot happen says so',
      (tester) async {
    final reload = Completer<bool>();
    var reloads = 0;
    await _pump(
      tester,
      host(
        SecurityEvents.forTesting()..emit(_tooOld),
        isWeb: true,
        reloadWeb: () {
          reloads++;
          return reload.future;
        },
      ),
    );

    await tester.tap(find.text('Reload page'));
    await tester.pump();
    expect(find.byType(CircularProgressIndicator), findsOneWidget);
    // A second tap while it works does nothing.
    await tester.tap(find.text('Reload page'));
    await tester.pump();
    expect(reloads, 1);

    reload.complete(false);
    await tester.pumpAndSettle();

    expect(find.byType(CircularProgressIndicator), findsNothing);
    expect(
      find.text(
        'Could not get the latest version. Check your connection and try '
        'again.',
      ),
      findsOneWidget,
    );
    // Nothing changed: the banner and the button stay.
    expect(find.text(_message), findsOneWidget);
    expect(find.text('Reload page'), findsOneWidget);
  });

  // A 320 px phone browser or phone at 2x text, every language, both
  // buttons, and the web button while it works: nothing overflows, the
  // button sits under the text, the banner takes at most 40% and scrolls.
  for (final locale in const [Locale('en'), Locale('am'), Locale('om')]) {
    for (final (platform, isWeb, busy) in [
      ('phone', false, false),
      ('web', true, false),
      ('web, reloading', true, true),
    ]) {
      testWidgets(
          '320x480 at 2x text, ${locale.languageCode}, $platform: no '
          'overflow, at most 40%, the screen stays in view', (tester) async {
        tester.view.physicalSize = const Size(320, 480);
        tester.view.devicePixelRatio = 1;
        tester.platformDispatcher.textScaleFactorTestValue = 2;
        addTearDown(tester.view.reset);
        addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
        final reload = Completer<bool>();
        addTearDown(() {
          if (!reload.isCompleted) reload.complete(true);
        });

        await _pump(
          tester,
          host(
            SecurityEvents.forTesting()..emit(_tooOld),
            locale: locale,
            isWeb: isWeb,
            reloadWeb: () => reload.future,
          ),
        );
        if (busy) {
          // At 2x it may sit below the fold of the banner: scroll to it.
          final reloadButton = find.descendant(
            of: find.byType(MaterialBanner),
            matching: find.byType(TextButton),
          );
          await tester.ensureVisible(reloadButton);
          await tester.pumpAndSettle();
          await tester.tap(reloadButton);
          await tester.pump();
          expect(find.byType(CircularProgressIndicator), findsOneWidget);
        }

        expect(tester.takeException(), isNull);
        final area = tester.getRect(
          find.ancestor(
            of: find.byType(MaterialBanner),
            matching: find.byType(SingleChildScrollView),
          ),
        );
        expect(area.height, lessThanOrEqualTo(480 * 0.4));
        expect(find.text('taps 0'), findsOneWidget);
        expect(
          tester.getRect(find.text('taps 0')).top,
          greaterThan(area.bottom),
        );
        // The button is under the message, not beside it.
        final banner = find.byType(MaterialBanner);
        final message = find.descendant(
          of: banner,
          matching: find.byType(Text),
        );
        final button = find.descendant(
          of: banner,
          matching: find.byType(TextButton),
        );
        expect(
          tester.getRect(button).top,
          greaterThanOrEqualTo(tester.getRect(message.first).bottom),
        );
        if (busy) {
          reload.complete(true);
          await tester.pumpAndSettle();
        }
      });
    }
  }

  testWidgets('wide: the button sits beside the text', (tester) async {
    tester.view.physicalSize = const Size(1200, 800);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);

    await _pump(
      tester,
      host(SecurityEvents.forTesting()..emit(_tooOld), isWeb: true),
    );

    final banner = find.byType(MaterialBanner);
    final message = tester.getRect(
      find.descendant(of: banner, matching: find.text(_message)),
    );
    final button = tester.getRect(
      find.descendant(of: banner, matching: find.byType(TextButton)),
    );
    expect(button.left, greaterThanOrEqualTo(message.right));
    expect(tester.takeException(), isNull);
  });

  testWidgets('scrolled text stays clear of the status bar', (tester) async {
    tester.view.physicalSize = const Size(320, 480);
    tester.view.devicePixelRatio = 1;
    tester.view.padding = const FakeViewPadding(top: 24);
    tester.platformDispatcher.textScaleFactorTestValue = 2;
    addTearDown(tester.view.reset);
    addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);

    await _pump(
      tester,
      host(
        SecurityEvents.forTesting()..emit(_tooOld),
        locale: const Locale('om'),
      ),
    );
    final scroll = find.ancestor(
      of: find.byType(MaterialBanner),
      matching: find.byType(SingleChildScrollView),
    );
    expect(tester.getRect(scroll).top, greaterThanOrEqualTo(24));

    await tester.drag(scroll, const Offset(0, -200));
    await tester.pumpAndSettle();

    // The viewport starts under the inset, so nothing scrolls into it.
    expect(tester.getRect(scroll).top, greaterThanOrEqualTo(24));
    expect(tester.takeException(), isNull);
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
}
