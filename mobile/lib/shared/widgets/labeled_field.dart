import 'dart:math' as math;

import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/theme/app_theme.dart';

// Form fields as the Fieldway boards draw them: the label above the field
// (it wraps instead of being cut, which Afaan Oromoo labels need), and two
// fields side by side when they fit (Create trip, Create listing).

/// The look of a grid field: its label sits above it, not inside.
InputDecoration labeledFieldDecoration({
  String? suffixText,
  String? hintText,
}) =>
    InputDecoration(
      suffixText: suffixText,
      hintText: hintText,
      errorMaxLines: 4,
      border: OutlineInputBorder(borderRadius: BorderRadius.circular(14)),
      contentPadding: const EdgeInsets.symmetric(horizontal: 14, vertical: 14),
    );

/// A label above its field. Screen readers hear the label as the field's
/// own (one merged node), as they would a floating label.
class LabeledField extends StatelessWidget {
  const LabeledField({
    required this.label,
    required this.field,
    this.labelHeight,
    super.key,
  });

  final String label;
  final Widget field;

  /// The height the label takes, so two fields side by side line up when
  /// only one label wraps; null lets the label size itself.
  final double? labelHeight;

  static const labelStyle = TextStyle(
    fontSize: 13,
    fontWeight: FontWeight.w700,
    color: AppTheme.unselectedNav,
  );

  LabeledField withLabelHeight(double height) =>
      LabeledField(label: label, field: field, labelHeight: height);

  @override
  Widget build(BuildContext context) {
    final text = Text(label, style: labelStyle);
    return MergeSemantics(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        children: [
          if (labelHeight == null)
            text
          else
            SizedBox(
              height: labelHeight,
              child: Align(
                alignment: AlignmentDirectional.bottomStart,
                child: text,
              ),
            ),
          const SizedBox(height: 6),
          field,
        ],
      ),
    );
  }
}

/// Two grid fields side by side, top aligned; on a very narrow screen (or
/// with large text) they stack.
class LabeledFieldPair extends StatelessWidget {
  const LabeledFieldPair({
    required this.first,
    required this.second,
    super.key,
  });

  final LabeledField first;
  final LabeledField second;

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(
      builder: (context, constraints) {
        final scaled = MediaQuery.textScalerOf(context).scale(1) > 1.3;
        if (constraints.maxWidth < 300 || scaled) {
          return Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [first, const SizedBox(height: 16), second],
          );
        }
        const gap = 10.0;
        final columnWidth = (constraints.maxWidth - gap) / 2;
        double heightOf(String label) => (TextPainter(
              text: TextSpan(text: label, style: LabeledField.labelStyle),
              textDirection: Directionality.of(context),
              textScaler: MediaQuery.textScalerOf(context),
            )..layout(maxWidth: columnWidth))
                .height;
        final labelHeight =
            math.max(heightOf(first.label), heightOf(second.label));
        return Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Expanded(child: first.withLabelHeight(labelHeight)),
            const SizedBox(width: gap),
            Expanded(child: second.withLabelHeight(labelHeight)),
          ],
        );
      },
    );
  }
}
