import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';

/// The detail and form headers on page M: a 44 px round back button on
/// paper and a 32 px title.
void main() {
  Widget host(Widget child, {double width = 390, double textScale = 1}) =>
      MaterialApp(
        theme: AppTheme.light(),
        home: MediaQuery(
          data: MediaQueryData(
            size: Size(width, 800),
            textScaler: TextScaler.linear(textScale),
          ),
          child: Scaffold(
            body: Center(
              child: SizedBox(
                width: width,
                child: SingleChildScrollView(child: child),
              ),
            ),
          ),
        ),
      );

  testWidgets('FwDetailHeader: round paper back button, 32 px title',
      (tester) async {
    await tester.pumpWidget(
      host(const FwDetailHeader(title: 'Place Order', subtitle: 'Wheat')),
    );

    final title = tester.widget<Text>(find.text('Place Order'));
    expect(title.style?.fontSize, 32);
    expect(title.style, FwText.detailTitle);

    final button = tester.widget<FwSquareButton>(find.byType(FwSquareButton));
    expect(button.background, AppTheme.paper);
    final material = tester.widget<Material>(
      find
          .descendant(
            of: find.byType(FwSquareButton),
            matching: find.byType(Material),
          )
          .first,
    );
    expect(material.shape, const CircleBorder());
    expect(
      tester.getSize(
        find.descendant(
          of: find.byType(FwSquareButton),
          matching: find.byType(InkWell),
        ),
      ),
      const Size(44, 44),
    );
    // The hit area is at least 48 by 48.
    final hit = tester.getSize(find.byType(FwSquareButton));
    expect(hit.width, greaterThanOrEqualTo(48));
    expect(hit.height, greaterThanOrEqualTo(48));
  });

  testWidgets('the dark back button is round too', (tester) async {
    await tester.pumpWidget(host(const FwBackButton(dark: true)));
    final material = tester.widget<Material>(
      find
          .descendant(
            of: find.byType(FwBackButton),
            matching: find.byType(Material),
          )
          .first,
    );
    expect(material.shape, const CircleBorder());
    expect(material.color, AppTheme.inkCard);
  });

  testWidgets('FwBottomActions: a hairline over white only when asked',
      (tester) async {
    Future<void> pump({required bool hairline}) => tester.pumpWidget(
          MaterialApp(
            theme: AppTheme.light(),
            home: Scaffold(
              bottomNavigationBar: FwBottomActions(
                hairline: hairline,
                children: [
                  FilledButton(onPressed: () {}, child: const Text('Save')),
                ],
              ),
            ),
          ),
        );
    Finder box() => find.descendant(
          of: find.byType(FwBottomActions),
          matching: find.byType(DecoratedBox),
        );

    await pump(hairline: false);
    expect(box(), findsNothing);
    final plainTop = tester.getTopLeft(find.byType(FilledButton)).dy -
        tester.getTopLeft(find.byType(FwBottomActions)).dy;
    expect(plainTop, 8);

    await pump(hairline: true);
    final decoration =
        tester.widget<DecoratedBox>(box()).decoration as BoxDecoration;
    expect(decoration.color, AppTheme.ground);
    expect(
      decoration.border,
      const Border(top: BorderSide(color: AppTheme.line)),
    );
    // The board's 12 px over the button (the line is drawn inside them).
    final top = tester.getTopLeft(find.byType(FilledButton)).dy -
        tester.getTopLeft(find.byType(FwBottomActions)).dy;
    expect(top, 12);
  });

  testWidgets('FwStackedHeader: back on its own row, eyebrow, 32 px title',
      (tester) async {
    await tester.pumpWidget(
      host(
        const FwStackedHeader(
          eyebrow: 'Settings',
          title: 'Data Usage',
          subtitle: 'What the app sent and received',
          trailing: Icon(Icons.refresh),
        ),
      ),
    );

    final back = tester.getRect(find.byType(FwBackButton));
    final eyebrow = tester.getRect(find.text('Settings'));
    final title = tester.getRect(find.text('Data Usage'));
    final subtitle =
        tester.getRect(find.text('What the app sent and received'));
    expect(eyebrow.top, greaterThanOrEqualTo(back.bottom));
    expect(title.top, greaterThanOrEqualTo(eyebrow.bottom));
    expect(subtitle.top, greaterThanOrEqualTo(title.bottom));
    expect(
      tester.widget<Text>(find.text('Data Usage')).style?.fontSize,
      32,
    );
    // The trailing widget ends the back button's row.
    final trailing = tester.getRect(find.byIcon(Icons.refresh));
    expect(trailing.left, greaterThan(back.right));
    expect(trailing.center.dy, closeTo(back.center.dy, 1));
  });

  testWidgets('FwStackedHeader without Back: the title alone', (tester) async {
    await tester.pumpWidget(
      host(const FwStackedHeader(title: 'My reputation', showBack: false)),
    );
    expect(find.byType(FwBackButton), findsNothing);
    expect(find.text('My reputation'), findsOneWidget);
  });

  testWidgets('a long Afaan Oromo title at 320 px and 2x text wraps',
      (tester) async {
    await tester.pumpWidget(
      host(
        const FwStackedHeader(
          eyebrow: 'Qindaa\'ina',
          title: 'Itti Fayyadama Deetaa',
        ),
        width: 320,
        textScale: 2,
      ),
    );
    expect(tester.takeException(), isNull);
    expect(
      tester.getSize(find.text('Itti Fayyadama Deetaa')).width,
      lessThanOrEqualTo(320),
    );
  });
}
