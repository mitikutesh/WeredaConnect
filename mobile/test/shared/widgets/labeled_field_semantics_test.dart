import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/shared/widgets/labeled_field.dart';

/// A label above its field is one merged node, except for a field with a
/// suggestion list: on Flutter 3.38 the list's overlay counts as the field's
/// semantic child, and a merge folded every suggestion into the field.
void main() {
  Widget app(Widget child) => MaterialApp(home: Scaffold(body: child));

  testWidgets('merges the label into the field by default', (tester) async {
    await tester.pumpWidget(
      app(const LabeledField(label: 'Phone Number', field: TextField())),
    );
    expect(find.byType(MergeSemantics), findsOneWidget);
  });

  testWidgets('a place search keeps its own nodes', (tester) async {
    await tester.pumpWidget(
      app(
        const LabeledField(
          label: 'From (Origin)',
          mergeSemantics: false,
          field: TextField(),
        ),
      ),
    );
    expect(find.byType(MergeSemantics), findsNothing);
    expect(find.text('From (Origin)'), findsOneWidget);
  });

  test('the side-by-side copy keeps the choice', () {
    const field = LabeledField(
      label: 'To (Destination)',
      mergeSemantics: false,
      field: SizedBox(),
    );
    expect(field.withLabelHeight(40).mergeSemantics, isFalse);
  });
}
