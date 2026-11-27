import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/features/order/presentation/widgets/dropoff_note.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/l10n/fallback_material_localizations.dart';

/// LT-12: the drop-off landmark card, field and sheet fit a 360 dp phone at
/// twice the text size in English, Amharic and Afaan Oromoo (an overflow
/// fails the test), and their buttons are 48 dp and labelled.
void main() {
  Order order({String? note, OrderStatus status = OrderStatus.accepted}) =>
      Order()
        ..orderId = 'order-001'
        ..buyerId = 'buyer-001'
        ..farmerId = 'farmer-001'
        ..tripId = 'trip-001'
        ..truckerId = 'trucker-001'
        ..status = status
        ..dropoffNote = note;

  Future<void> pump(
    WidgetTester tester,
    Widget child, {
    required String locale,
  }) async {
    tester.view.physicalSize = const Size(360, 1400);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      MaterialApp(
        locale: Locale(locale),
        localizationsDelegates: const [
          AppLocalizations.delegate,
          FallbackMaterialLocalizationsDelegate(),
          GlobalWidgetsLocalizations.delegate,
          FallbackCupertinoLocalizationsDelegate(),
        ],
        supportedLocales: AppLocalizations.supportedLocales,
        builder: (context, child) => MediaQuery(
          data: MediaQuery.of(context).copyWith(
            textScaler: const TextScaler.linear(2),
          ),
          child: child!,
        ),
        home: Scaffold(
          body: SingleChildScrollView(
            padding: const EdgeInsets.all(20),
            child: child,
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  for (final locale in ['en', 'am', 'om']) {
    group(locale, () {
      testWidgets('the buyer card with a long note, and Edit', (tester) async {
        final handle = tester.ensureSemantics();
        await pump(
          tester,
          DropoffNoteCard(
            order: order(
              note: 'መርካቶ፣ አንዋር መስጊድ አጠገብ፣ ከአውቶቡስ ጣቢያው ጀርባ ያለው ሰማያዊ በር',
            ),
            userId: 'buyer-001',
            onEdit: () {},
          ),
          locale: locale,
        );

        expect(
          find.byKey(const Key('order_dropoff_note_edit')),
          findsOneWidget,
        );
        await expectLater(tester, meetsGuideline(androidTapTargetGuideline));
        await expectLater(tester, meetsGuideline(labeledTapTargetGuideline));
        handle.dispose();
      });

      testWidgets('the buyer card without a note, and Add', (tester) async {
        await pump(
          tester,
          DropoffNoteCard(
            order: order(),
            userId: 'buyer-001',
            onEdit: () {},
          ),
          locale: locale,
        );

        expect(
          find.byKey(const Key('order_dropoff_note_edit')),
          findsOneWidget,
        );
      });

      testWidgets('the field with its helper and the phone error',
          (tester) async {
        final controller = TextEditingController(text: 'call 0911234567');
        addTearDown(controller.dispose);
        await pump(
          tester,
          DropoffNoteField(controller: controller),
          locale: locale,
        );

        final l10n = lookupAppLocalizations(Locale(locale));
        expect(find.text(l10n.dropoffNoteLabel), findsOneWidget);
        expect(find.text(l10n.errorContactInText), findsOneWidget);
      });

      testWidgets('the edit sheet', (tester) async {
        final handle = tester.ensureSemantics();
        await pump(
          tester,
          Builder(
            builder: (context) => TextButton(
              onPressed: () => showDropoffNoteSheet(context, initial: 'Kality'),
              child: const Text('open'),
            ),
          ),
          locale: locale,
        );
        await tester.tap(find.text('open'));
        await tester.pumpAndSettle();

        final l10n = lookupAppLocalizations(Locale(locale));
        expect(find.text(l10n.dropoffNoteTitle), findsOneWidget);
        expect(find.byKey(const Key('dropoff_note_save')), findsOneWidget);
        await expectLater(tester, meetsGuideline(androidTapTargetGuideline));
        handle.dispose();
      });
    });
  }

  testWidgets('the sheet returns the text, or null when cancelled',
      (tester) async {
    final results = <String?>[];
    await tester.pumpWidget(
      MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: Builder(
            builder: (context) => TextButton(
              onPressed: () async =>
                  results.add(await showDropoffNoteSheet(context)),
              child: const Text('open'),
            ),
          ),
        ),
      ),
    );

    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    await tester.enterText(
      find.byKey(const Key('dropoff_note_field')),
      'Saris',
    );
    await tester.tap(find.byKey(const Key('dropoff_note_save')));
    await tester.pumpAndSettle();

    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Cancel'));
    await tester.pumpAndSettle();

    expect(results, ['Saris', null]);
  });

  testWidgets('nothing for a viewer with no note who cannot add one',
      (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: DropoffNoteCard(
            order: order(),
            userId: 'farmer-001',
            onEdit: () {},
          ),
        ),
      ),
    );

    expect(find.byKey(const Key('order_dropoff_note')), findsNothing);
  });
}
