import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/theme/app_theme.dart';

// Marks the Home boards' cards repeat (design canvas page M): the orange
// "ACTION NEEDED" pill and the "Open" mark at a card's end.

/// The boards' "ACTION NEEDED" pill: orange with a 1.5 px black edge, the
/// one orange mark on the card.
class HomeActionPill extends StatelessWidget {
  final String label;

  const HomeActionPill(this.label, {super.key});

  @override
  Widget build(BuildContext context) {
    return Container(
      constraints: const BoxConstraints(minHeight: 26),
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 3),
      decoration: const ShapeDecoration(
        color: AppTheme.orange,
        shape: StadiumBorder(
          side: BorderSide(color: AppTheme.ink, width: 1.5),
        ),
      ),
      child: Align(
        widthFactor: 1,
        heightFactor: 1,
        child: Text(
          label.toUpperCase(),
          style: const TextStyle(
            fontSize: 12,
            fontWeight: FontWeight.w800,
            letterSpacing: 0.7,
            color: AppTheme.ink,
          ),
        ),
      ),
    );
  }
}

/// "Open" with a chevron at the end of a Home card: a black pill on the
/// raised card ([pill]), plain black words on the flat one. Decoration of
/// the card's own tap: the whole card is the button.
class HomeOpenMark extends StatelessWidget {
  final String label;
  final bool pill;

  const HomeOpenMark({required this.label, this.pill = true, super.key});

  @override
  Widget build(BuildContext context) {
    final ink = pill ? Colors.white : AppTheme.ink;
    final row = Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Text(
          label,
          style: TextStyle(
            fontSize: 13,
            fontWeight: pill ? FontWeight.w800 : FontWeight.w700,
            color: ink,
          ),
        ),
        SizedBox(width: pill ? 4 : 2),
        Icon(Icons.chevron_right, size: pill ? 15 : 18, color: ink),
      ],
    );
    if (!pill) return row;
    return Container(
      constraints: const BoxConstraints(minHeight: 34),
      padding: const EdgeInsetsDirectional.only(start: 14, end: 10),
      decoration: const ShapeDecoration(
        color: AppTheme.ink,
        shape: StadiumBorder(),
      ),
      child: row,
    );
  }
}
