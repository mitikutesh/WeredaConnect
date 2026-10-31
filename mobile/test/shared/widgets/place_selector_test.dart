import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';
import 'package:wereda_connect/shared/services/place_repository.dart';
import 'package:wereda_connect/shared/widgets/place_selector.dart';

PlaceRepository _loadRepo() {
  final json = File('test/fixtures/woredas_sample.json').readAsStringSync();
  return PlaceRepository()..loadFromStringForTest(json);
}

Widget _harness({
  required TextEditingController controller,
  required PlaceRepository repo,
  ValueChanged<Place?>? onSelected,
  ValueChanged<String>? onChanged,
  Locale locale = const Locale('en'),
  bool pickerLook = false,
}) {
  return MaterialApp(
    // The app's delegates: Afan Oromo has no built-in Material strings.
    localizationsDelegates: const [
      AppLocalizations.delegate,
      FallbackMaterialLocalizationsDelegate(),
      GlobalWidgetsLocalizations.delegate,
      FallbackCupertinoLocalizationsDelegate(),
    ],
    supportedLocales: AppLocalizations.supportedLocales,
    locale: locale,
    home: Scaffold(
      body: PlaceSelector(
        controller: controller,
        repository: repo,
        // Tight debounce keeps the tests fast and deterministic.
        debounce: const Duration(milliseconds: 10),
        onPlaceSelected: onSelected,
        onChanged: onChanged,
        pickerLook: pickerLook,
      ),
    ),
  );
}

Future<void> _type(WidgetTester tester, String text) async {
  await tester.pumpAndSettle(); // the fallback delegates load asynchronously
  await tester.enterText(find.byType(TextField), text);
  await tester.pump(const Duration(milliseconds: 30)); // let debounce fire
  await tester.pump(); // build the options overlay
}

void main() {
  testWidgets('shows matching options with Amharic name beneath',
      (tester) async {
    await tester.pumpWidget(
      _harness(
        controller: TextEditingController(),
        repo: _loadRepo(),
      ),
    );

    await _type(tester, 'Adama');

    // The am subtitle only appears in the option tile, not the input field.
    expect(find.text('አዳማ'), findsOneWidget);
  });

  testWidgets('selecting an option stores the canonical English name',
      (tester) async {
    final controller = TextEditingController();
    Place? selected;
    await tester.pumpWidget(
      _harness(
        controller: controller,
        repo: _loadRepo(),
        onSelected: (p) => selected = p,
      ),
    );

    await _type(tester, 'Adam');
    await tester.tap(find.text('አዳማ'));
    await tester.pumpAndSettle();

    expect(controller.text, 'Adama'); // canonical en, not the id
    expect(selected?.id, 'gn-adama');
    expect(find.text('አዳማ'), findsNothing); // overlay dismissed
  });

  // Register personal board: "Meki · Oromia" with a chevron, no glass.
  testWidgets('the picker look shows the region and a chevron',
      (tester) async {
    final controller = TextEditingController();
    Place? selected;
    await tester.pumpWidget(
      _harness(
        controller: controller,
        repo: _loadRepo(),
        onSelected: (p) => selected = p,
        pickerLook: true,
      ),
    );

    await _type(tester, 'Adam');
    await tester.tap(find.text('አዳማ'));
    await tester.pumpAndSettle();

    expect(controller.text, 'Adama · ${selected!.region}');
    expect(selected?.en, 'Adama');
    expect(find.byIcon(Icons.keyboard_arrow_down), findsOneWidget);
    expect(find.byIcon(Icons.search), findsNothing);
  });

  testWidgets('reports raw text via onChanged while typing', (tester) async {
    final changes = <String>[];
    await tester.pumpWidget(
      _harness(
        controller: TextEditingController(),
        repo: _loadRepo(),
        onChanged: changes.add,
      ),
    );

    await _type(tester, 'Gamb');

    expect(changes.last, 'Gamb');
  });

  testWidgets('escape dismisses the options', (tester) async {
    await tester.pumpWidget(
      _harness(
        controller: TextEditingController(),
        repo: _loadRepo(),
      ),
    );

    await _type(tester, 'Adama');
    expect(find.text('አዳማ'), findsOneWidget);

    await tester.sendKeyEvent(LogicalKeyboardKey.escape);
    await tester.pumpAndSettle();

    expect(find.text('አዳማ'), findsNothing);
  });

  testWidgets('keyboard down + enter selects the highlighted option',
      (tester) async {
    final controller = TextEditingController();
    Place? selected;
    await tester.pumpWidget(
      _harness(
        controller: controller,
        repo: _loadRepo(),
        onSelected: (p) => selected = p,
      ),
    );

    await _type(tester, 'adama'); // single match
    await tester.sendKeyEvent(LogicalKeyboardKey.arrowDown);
    await tester.pump();
    // Submitting the field selects the highlighted option (RawAutocomplete).
    await tester.testTextInput.receiveAction(TextInputAction.done);
    await tester.pumpAndSettle();

    expect(selected?.id, 'gn-adama');
    expect(controller.text, 'Adama');
  });

  testWidgets('free text with no match is kept and shows the hint',
      (tester) async {
    final controller = TextEditingController();
    var selectedCalls = 0;
    await tester.pumpWidget(
      _harness(
        controller: controller,
        repo: _loadRepo(),
        onSelected: (_) => selectedCalls++,
      ),
    );

    await _type(tester, 'Nowhereville');

    expect(controller.text, 'Nowhereville'); // free text preserved
    expect(find.text('No match — you can use what you typed'), findsOneWidget);
    // Nothing was picked, so no selection callback fires (placeId stays null).
    expect(selectedCalls, 0);
  });

  testWidgets('renders an option that has no Amharic/Oromo name',
      (tester) async {
    await tester.pumpWidget(
      _harness(
        controller: TextEditingController(),
        repo: _loadRepo(),
      ),
    );

    await _type(tester, 'Mertule');

    // en shown; no crash despite empty am/om.
    expect(find.text('Mertule Maryam'), findsOneWidget);
  });

  testWidgets('shows the display name without marks; a pick stores en',
      (tester) async {
    final controller = TextEditingController();
    Place? selected;
    await tester.pumpWidget(
      _harness(
        controller: controller,
        repo: _loadRepo(),
        onSelected: (p) => selected = p,
      ),
    );

    await _type(tester, 'meki');

    expect(find.text('Meki'), findsOneWidget); // tile title
    expect(find.text('Mek’ī'), findsNothing);
    expect(find.text('መቂ'), findsOneWidget); // other names beneath

    await tester.tap(find.text('Meki'));
    await tester.pumpAndSettle();

    expect(selected?.id, 'gn-meki');
    expect(controller.text, 'Mek’ī'); // canonical en for legacy fields
  });

  testWidgets('a renamed place shows its current name and the old one',
      (tester) async {
    await tester.pumpWidget(
      _harness(controller: TextEditingController(), repo: _loadRepo()),
    );

    await _type(tester, 'Ziway');

    expect(find.text('Batu'), findsOneWidget);
    expect(find.text('ባቱ  ·  Baatuu  ·  Ziway'), findsOneWidget);
  });

  testWidgets('shows the Amharic name first in am', (tester) async {
    await tester.pumpWidget(
      _harness(
        controller: TextEditingController(),
        repo: _loadRepo(),
        locale: const Locale('am'),
      ),
    );

    await _type(tester, 'Batu');

    expect(find.text('ባቱ'), findsOneWidget);
    expect(find.text('Batu  ·  Baatuu  ·  Ziway'), findsOneWidget);
  });

  testWidgets('shows the Oromo name first in om, else the English one',
      (tester) async {
    await tester.pumpWidget(
      _harness(
        controller: TextEditingController(),
        repo: _loadRepo(),
        locale: const Locale('om'),
      ),
    );

    await _type(tester, 'Batu');
    expect(find.text('Baatuu'), findsOneWidget);
    expect(find.text('Batu  ·  ባቱ  ·  Ziway'), findsOneWidget);

    await _type(tester, 'Meki');
    expect(find.text('Meki'), findsWidgets); // field text + tile title
    expect(find.text('መቂ'), findsOneWidget);
  });
}
