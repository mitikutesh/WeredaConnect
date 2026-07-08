import 'package:flutter/material.dart';

import 'package:wereda_connect/shared/theme/app_theme.dart';

/// A 1–5 star rating widget.
///
/// Interactive when [onRatingChanged] is provided (tap a star to set the
/// value); otherwise a read-only display that renders half stars for
/// fractional averages.
class StarRating extends StatelessWidget {
  final double rating;
  final int max;
  final double size;
  final ValueChanged<int>? onRatingChanged;
  final Color? color;

  const StarRating({
    required this.rating,
    this.max = 5,
    this.size = 24,
    this.onRatingChanged,
    this.color,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final starColor = color ?? AppTheme.accentGold;
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: List.generate(max, (index) {
        final position = index + 1;
        final IconData icon;
        if (rating >= position) {
          icon = Icons.star;
        } else if (rating >= position - 0.5) {
          icon = Icons.star_half;
        } else {
          icon = Icons.star_border;
        }
        final star = Icon(icon, size: size, color: starColor);
        if (onRatingChanged == null) return star;
        return IconButton(
          padding: EdgeInsets.zero,
          visualDensity: VisualDensity.compact,
          constraints: BoxConstraints.tightFor(
            width: size + 12,
            height: size + 12,
          ),
          iconSize: size,
          icon: star,
          onPressed: () => onRatingChanged!(position),
        );
      }),
    );
  }
}
