import 'package:flutter/material.dart';

import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Pieces the list screens without a board (My Trips, Orders, Payments)
/// borrow from the boards that have one: the Market's pill chips, the My
/// Crops load bar and info note, and the "person · place" header eyebrow.

/// One of the Market board's filter pills: white with a whisper border,
/// filled field green when selected.
class FwFilterPill extends StatelessWidget {
  final String label;
  final bool selected;
  final VoidCallback onTap;

  const FwFilterPill({
    required this.label,
    required this.selected,
    required this.onTap,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    return Semantics(
      button: true,
      selected: selected,
      child: Material(
        color: selected ? AppTheme.fieldGreen : AppTheme.surfaceWhite,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(999),
          side: BorderSide(
            color: selected ? AppTheme.fieldGreen : AppTheme.whisperBorder,
          ),
        ),
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          onTap: onTap,
          child: Container(
            height: 40,
            padding: const EdgeInsets.symmetric(horizontal: 14),
            alignment: Alignment.center,
            child: Text(
              label,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: TextStyle(
                fontSize: 13,
                fontWeight: selected ? FontWeight.w700 : FontWeight.w600,
                color: selected ? Colors.white : AppTheme.soilDark,
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// One option of a [FwFilterPillRow].
class FwFilterOption<T> {
  final T value;
  final String label;

  const FwFilterOption({required this.value, required this.label});
}

/// A row of [FwFilterPill]s that scrolls sideways when it does not fit,
/// like the crop chips on the Market board.
class FwFilterPillRow<T> extends StatelessWidget {
  final List<FwFilterOption<T>> options;
  final T selected;
  final ValueChanged<T> onSelected;
  final EdgeInsetsGeometry padding;

  const FwFilterPillRow({
    required this.options,
    required this.selected,
    required this.onSelected,
    this.padding = const EdgeInsets.symmetric(horizontal: 20),
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    return SingleChildScrollView(
      scrollDirection: Axis.horizontal,
      padding: padding,
      child: Row(
        children: [
          for (var i = 0; i < options.length; i++) ...[
            if (i > 0) const SizedBox(width: 8),
            FwFilterPill(
              label: options[i].label,
              selected: options[i].value == selected,
              onTap: () => onSelected(options[i].value),
            ),
          ],
        ],
      ),
    );
  }
}

/// The My Crops board's thin load bar with its caption ("32 of 60 quintals
/// ordered"). [fraction] is clamped to 0..1; an empty bar is a plain track.
class FwLoadBar extends StatelessWidget {
  final double fraction;
  final String caption;
  final Color color;

  const FwLoadBar({
    required this.fraction,
    required this.caption,
    this.color = AppTheme.fieldGreen,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final filled = fraction.isNaN ? 0.0 : fraction.clamp(0.0, 1.0);
    return Row(
      children: [
        Expanded(
          child: ClipRRect(
            borderRadius: BorderRadius.circular(999),
            child: SizedBox(
              height: 6,
              child: Stack(
                children: [
                  Container(color: AppTheme.segmentTrack),
                  if (filled > 0)
                    FractionallySizedBox(
                      widthFactor: filled,
                      child: Container(
                        decoration: BoxDecoration(
                          color: color,
                          borderRadius: BorderRadius.circular(999),
                        ),
                      ),
                    ),
                ],
              ),
            ),
          ),
        ),
        const SizedBox(width: 10),
        // The caption keeps its words; the bar gives way on a narrow card.
        ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 230),
          child: Text(
            caption,
            maxLines: 1,
            softWrap: false,
            overflow: TextOverflow.ellipsis,
            style: const TextStyle(
              fontSize: 12,
              fontWeight: FontWeight.w500,
              color: AppTheme.mutedSteel,
            ),
          ),
        ),
      ],
    );
  }
}

/// The boards' tinted note row ("Only you see your listings here"): an icon
/// and one line of 13/600 text on a soft ground, radius 16.
class FwInfoNote extends StatelessWidget {
  final IconData icon;
  final String text;
  final Color background;
  final Color foreground;

  const FwInfoNote({
    required this.icon,
    required this.text,
    this.background = AppTheme.dropoffBlueTint,
    this.foreground = AppTheme.dropoffBlue,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 12),
      decoration: BoxDecoration(
        color: background,
        borderRadius: BorderRadius.circular(16),
      ),
      child: Row(
        children: [
          Icon(icon, size: 18, color: foreground),
          const SizedBox(width: 10),
          Expanded(
            child: Text(
              text,
              style: TextStyle(
                fontSize: 13,
                fontWeight: FontWeight.w600,
                height: 1.35,
                color: foreground,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// The header eyebrow the boards put over a list title: the signed-in
/// person and their place ("Almaz Bekele · Meki"). Falls back to the name
/// alone, then to [fallback], when the session has no place or no name.
String fwPersonPlaceEyebrow(AuthState state, String fallback) {
  if (state is! AuthAuthenticated) return fallback;
  final name = state.userName.trim();
  final place = state.woreda?.trim() ?? '';
  if (name.isEmpty) return fallback;
  return place.isEmpty ? name : '$name · $place';
}
