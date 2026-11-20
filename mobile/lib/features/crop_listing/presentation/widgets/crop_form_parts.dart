import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'package:wereda_connect/core/models/trip_capacity.dart';
import 'package:wereda_connect/core/models/user.dart';
import 'package:wereda_connect/core/utils/etb_format.dart';
import 'package:wereda_connect/core/utils/kg_entry.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/fw_detail_header.dart';
import 'package:wereda_connect/shared/widgets/labeled_field.dart';
import 'package:wereda_connect/shared/widgets/role_sketch.dart';

// What Create listing and Edit listing share (the Farmer · Create crop
// listing board, design canvas page M): the header with the farmer sketch,
// the uppercase section label with its glyph, and the quantity typed in
// kilograms.

/// The form's header: the round Back, the title at 30 px and the farmer
/// sketch at the end when the title leaves it room (one sketch per screen,
/// never under the title).
class CropFormHeader extends StatelessWidget {
  final String title;
  final String? subtitle;

  const CropFormHeader({required this.title, this.subtitle, super.key});

  static const TextStyle titleStyle = TextStyle(
    fontSize: 30,
    fontWeight: FontWeight.w800,
    letterSpacing: -1.05,
    height: 1.05,
    color: AppTheme.ink,
  );

  static const double _sketchWidth = 66;
  static const double _sketchMinWidth = 48;
  static const double _sketchGap = 12;

  @override
  Widget build(BuildContext context) {
    final subtitle = this.subtitle;
    return Padding(
      padding: const EdgeInsets.fromLTRB(20, 16, 20, 8),
      child: Row(
        children: [
          const FwBackButton(),
          const SizedBox(width: 14),
          Expanded(
            child: LayoutBuilder(
              builder: (context, constraints) {
                final text = Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Semantics(
                      header: true,
                      child: Text(title, style: titleStyle),
                    ),
                    if (subtitle != null && subtitle.isNotEmpty)
                      Text(
                        subtitle,
                        style: const TextStyle(
                          fontSize: 13,
                          fontWeight: FontWeight.w500,
                          color: AppTheme.slate,
                        ),
                      ),
                  ],
                );
                if (!RoleSketch.sketchesShown(context)) return text;
                final painter = TextPainter(
                  text: TextSpan(
                    text: title,
                    style: DefaultTextStyle.of(context).style.merge(titleStyle),
                  ),
                  textDirection: Directionality.of(context),
                  textScaler: MediaQuery.textScalerOf(context),
                  maxLines: 1,
                )..layout();
                final room = constraints.maxWidth - painter.width - _sketchGap;
                painter.dispose();
                if (room < _sketchMinWidth) return text;
                return Row(
                  children: [
                    Expanded(child: text),
                    const SizedBox(width: _sketchGap),
                    RoleSketch(
                      role: UserRole.farmer,
                      width: room < _sketchWidth ? room : _sketchWidth,
                    ),
                  ],
                );
              },
            ),
          ),
        ],
      ),
    );
  }
}

/// A form section's label as the board writes it: a small glyph, then the
/// words in capitals with wide tracking.
class CropFormSectionLabel extends StatelessWidget {
  final IconData icon;
  final String text;

  const CropFormSectionLabel({
    required this.icon,
    required this.text,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        Icon(icon, size: 16, color: AppTheme.slate),
        const SizedBox(width: 8),
        Expanded(child: FwSectionLabel(text)),
      ],
    );
  }
}

/// The listing's quantity, typed in kilograms ("Quantity (kg)", the unit
/// and the same amount in quintals beside the number: "kg (40 q)").
///
/// The listing itself stays in quintals, as the server and the sync
/// contract have it (crop-listing-api.md): the form converts what is typed
/// with [quintalsFromKg] when it saves. The field takes what the server
/// takes: 1 kg (0.01 quintals) to 1,000,000 kg (10,000 quintals), two
/// decimals at most.
class KgQuantityField extends StatelessWidget {
  final TextEditingController controller;
  final Key? fieldKey;

  /// What the field showed when the form opened (Edit listing), null on a
  /// new listing. While the field still shows it, it is not checked: an
  /// untouched quantity is not sent, so stock an order left under 1 kg
  /// must not keep the farmer from saving a new price.
  final String? untouchedText;

  const KgQuantityField({
    required this.controller,
    this.fieldKey,
    this.untouchedText,
    super.key,
  });

  /// Up to seven digits and two decimals (1,000,000.00 at most); a
  /// grouping comma is taken out, never read as a decimal mark
  /// ([KgEntryFormatter]).
  static const List<TextInputFormatter> formatters = [KgEntryFormatter()];

  /// The words for what is wrong with [value], or null when it can be saved.
  static String? validate(String? value, AppLocalizations l10n) =>
      switch (kgEntryProblem(value)) {
        null => null,
        KgEntryProblem.missing => l10n.quantityRequired,
        KgEntryProblem.tooSmall => l10n.quantityKgTooSmall,
        KgEntryProblem.tooLarge => l10n.quantityKgTooLarge,
      };

  /// A [LabeledField] (so it sits in a [LabeledFieldPair] beside the price).
  static LabeledField labeled({
    required TextEditingController controller,
    required AppLocalizations l10n,
    Key? fieldKey,
    String? untouchedText,
  }) =>
      LabeledField(
        label: l10n.quantityInKg,
        field: KgQuantityField(
          controller: controller,
          fieldKey: fieldKey,
          untouchedText: untouchedText,
        ),
      );

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return TextFormField(
      key: fieldKey,
      controller: controller,
      decoration: labeledFieldDecoration().copyWith(
        // The quintals follow what is typed.
        suffix: ListenableBuilder(
          listenable: controller,
          builder: (context, _) {
            final kg = parseKgEntry(controller.text);
            return Text.rich(
              TextSpan(
                text: l10n.kgUnit,
                children: [
                  if (kg != null && kg > 0)
                    TextSpan(
                      text: ' ${l10n.quintalsShortInParens(
                        kgNumberText(kg / kgPerQuintal),
                      )}',
                      style: const TextStyle(color: AppTheme.slate),
                    ),
                ],
              ),
              key: const Key('listing_quantity_unit'),
              style: const TextStyle(
                fontSize: 13,
                fontWeight: FontWeight.w500,
                color: AppTheme.ink,
              ),
            );
          },
        ),
      ),
      keyboardType: const TextInputType.numberWithOptions(decimal: true),
      inputFormatters: formatters,
      validator: (value) => untouchedText != null && value == untouchedText
          ? null
          : validate(value, l10n),
    );
  }
}

/// "Total: 168,000 ETB (4,200 ETB per quintal)" for [kgText] kilograms at
/// [priceText] ETB per kg, once both are numbers above zero; null else.
String? cropFormTotalLine(
  String kgText,
  String priceText,
  AppLocalizations l10n,
) {
  final kg = parseKgEntry(kgText);
  final pricePerKg = double.tryParse(priceText);
  if (kg == null || kg <= 0) return null;
  if (pricePerKg == null || pricePerKg <= 0) return null;
  return l10n.cropFormTotalLine(
    formatEtbAmount(kg * pricePerKg),
    formatEtbAmount(pricePerKg * kgPerQuintal),
  );
}
