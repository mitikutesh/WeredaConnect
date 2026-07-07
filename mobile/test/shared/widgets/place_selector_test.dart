import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/place.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
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
}) {
  return MaterialApp(
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    locale: const Locale('en'),
    home: Scaffold(
      body: PlaceSelector(
        controller: controller,
        repository: repo,
        // Tight debounce keeps the tests fast and deterministic.
        debounce: const Duration(milliseconds: 10),
        onPlaceSelected: onSelected,
        onChanged: onChanged,
      ),
    ),
  );
}

Future<void> _type(WidgetTester tester, String text) async {
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
}
