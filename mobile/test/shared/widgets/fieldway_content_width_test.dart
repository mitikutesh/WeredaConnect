import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/shared/widgets/fieldway.dart';

/// FwContentWidth: the phone column centred on a wide window, never taller
/// than its child (a pinned bottom bar must leave the page its body).
void main() {
  Future<void> pump(WidgetTester tester, double width) async {
    tester.view.physicalSize = Size(width, 800);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      const MaterialApp(
        home: Scaffold(
          body: FwContentWidth(
            child: SizedBox.expand(key: Key('body')),
          ),
          bottomNavigationBar: FwContentWidth(
            child: SizedBox(
              height: 56,
              width: double.infinity,
              key: Key('bar'),
            ),
          ),
        ),
      ),
    );
  }

  testWidgets('wide: centred at 720 and the bar keeps its own height',
      (tester) async {
    await pump(tester, 1280);

    final bar = tester.getRect(find.byKey(const Key('bar')));
    expect(bar.height, 56);
    expect(bar.width, FwContentWidth.defaultMaxWidth);
    expect(bar.center.dx, 640);
    final body = tester.getRect(find.byKey(const Key('body')));
    expect(body.height, 800 - 56);
    expect(body.width, FwContentWidth.defaultMaxWidth);
  });

  testWidgets('phone: full width', (tester) async {
    await pump(tester, 390);

    expect(tester.getRect(find.byKey(const Key('bar'))).width, 390);
    expect(tester.getRect(find.byKey(const Key('body'))).width, 390);
  });
}
