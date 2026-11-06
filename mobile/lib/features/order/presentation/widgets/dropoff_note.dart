import 'package:flutter/material.dart';

import 'package:wereda_connect/core/models/order.dart';
import 'package:wereda_connect/core/models/order_dropoff_note.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/labeled_field.dart';

// LT-12 (live-trip.md section 10): the buyer's drop-off landmark. The field
// on Place order and in the edit sheet, and the card on the order detail.
// Same widgets on mobile and web.

/// The drop-off landmark field: one line of at most [dropoffNoteMaxLength]
/// characters (the counter counts what the server counts), an example as the
/// hint, who sees it under it, and the phone number refusal in place of that
/// line (the server refuses it with `ORDER_CONTACT_IN_TEXT`).
class DropoffNoteField extends StatelessWidget {
  const DropoffNoteField({
    required this.controller,
    this.onChanged,
    this.autofocus = false,
    super.key,
  });

  final TextEditingController controller;
  final ValueChanged<String>? onChanged;
  final bool autofocus;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final phoneNumber =
        dropoffNoteProblem(controller.text) == DropoffNoteProblem.phoneNumber;
    return LabeledField(
      label: l10n.dropoffNoteLabel,
      field: TextField(
        key: const Key('dropoff_note_field'),
        controller: controller,
        autofocus: autofocus,
        maxLength: dropoffNoteMaxLength,
        textCapitalization: TextCapitalization.sentences,
        textInputAction: TextInputAction.done,
        onChanged: onChanged,
        decoration:
            labeledFieldDecoration(hintText: l10n.dropoffNoteHint).copyWith(
          helperText: l10n.dropoffNoteHelper,
          helperMaxLines: 4,
          errorText: phoneNumber ? l10n.errorContactInText : null,
        ),
      ),
    );
  }
}

/// Asks the buyer for the drop-off landmark, starting from [initial].
/// Returns the text to save (blank clears it), or null when the sheet was
/// dismissed. Save waits until the text holds no phone number.
Future<String?> showDropoffNoteSheet(
  BuildContext context, {
  String? initial,
}) =>
    showModalBottomSheet<String>(
      context: context,
      isScrollControlled: true,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(20)),
      ),
      builder: (_) => _DropoffNoteSheet(initial: initial ?? ''),
    );

class _DropoffNoteSheet extends StatefulWidget {
  const _DropoffNoteSheet({required this.initial});

  final String initial;

  @override
  State<_DropoffNoteSheet> createState() => _DropoffNoteSheetState();
}

class _DropoffNoteSheetState extends State<_DropoffNoteSheet> {
  late final TextEditingController _controller =
      TextEditingController(text: widget.initial);

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final canSave = dropoffNoteProblem(_controller.text) == null;
    return SafeArea(
      child: SingleChildScrollView(
        padding: EdgeInsets.fromLTRB(
          24,
          20,
          24,
          16 + MediaQuery.viewInsetsOf(context).bottom,
        ),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Semantics(
              header: true,
              child: Text(
                l10n.dropoffNoteTitle,
                style: Theme.of(context)
                    .textTheme
                    .titleMedium
                    ?.copyWith(fontWeight: FontWeight.bold),
              ),
            ),
            const SizedBox(height: 16),
            DropoffNoteField(
              controller: _controller,
              autofocus: true,
              onChanged: (_) => setState(() {}),
            ),
            const SizedBox(height: 16),
            FilledButton(
              key: const Key('dropoff_note_save'),
              onPressed: canSave
                  ? () => Navigator.of(context).pop(_controller.text)
                  : null,
              style: FilledButton.styleFrom(
                minimumSize: const Size.fromHeight(48),
              ),
              child: Text(l10n.save),
            ),
            const SizedBox(height: 4),
            TextButton(
              onPressed: () => Navigator.of(context).pop(),
              child: Text(l10n.cancel),
            ),
          ],
        ),
      ),
    );
  }
}

/// The drop-off landmark on the order detail: the note for every party the
/// server sent it to (the buyer, the farmer, the committed trucker), and for
/// the buyer before pickup Add or Edit ([onEdit]). Nothing when there is no
/// note and the viewer cannot add one.
class DropoffNoteCard extends StatelessWidget {
  const DropoffNoteCard({
    required this.order,
    required this.userId,
    this.onEdit,
    super.key,
  });

  final Order order;
  final String userId;

  /// Opens the edit sheet; null hides the button.
  final VoidCallback? onEdit;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final note = normalizeDropoffNote(order.dropoffNote);
    final edit = canEditDropoffNote(order, userId) ? onEdit : null;
    if (note == null && edit == null) return const SizedBox.shrink();

    return FwCard(
      key: const Key('order_dropoff_note'),
      padding: const EdgeInsets.fromLTRB(16, 14, 8, 14),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Padding(
            padding: EdgeInsets.only(top: 2),
            child: Icon(
              Icons.place_outlined,
              size: 22,
              color: AppTheme.dropoffBlue,
            ),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(l10n.dropoffNoteTitle, style: LabeledField.labelStyle),
                const SizedBox(height: 2),
                Text(
                  note ?? l10n.dropoffNoteEmpty,
                  style: note == null
                      ? const TextStyle(
                          fontSize: 14,
                          height: 1.4,
                          color: AppTheme.mutedSteel,
                        )
                      : const TextStyle(
                          fontSize: 15,
                          height: 1.4,
                          fontWeight: FontWeight.w700,
                          color: AppTheme.soilDark,
                        ),
                ),
              ],
            ),
          ),
          if (edit != null) ...[
            const SizedBox(width: 4),
            TextButton(
              key: const Key('order_dropoff_note_edit'),
              onPressed: edit,
              child: Text(note == null ? l10n.dropoffNoteAdd : l10n.edit),
            ),
          ],
        ],
      ),
    );
  }
}
