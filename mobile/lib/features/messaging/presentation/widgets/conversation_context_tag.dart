import 'package:flutter/material.dart';

import 'package:wereda_connect/features/crop_listing/domain/models/crop_type.dart';
import 'package:wereda_connect/features/messaging/domain/conversation_context_resolver.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// The human-readable label for what a thread is about: the linked crop
/// ("Teff • 100 kg"), trip route ("Adama → Bishoftu"), or the crop behind an
/// order/delivery ("Order • Teff • 100 kg"), falling back to a localized
/// generic type name when the entity can't be resolved.
String conversationContextLabel(
  AppLocalizations l10n,
  String contextType,
  ConversationContextInfo? info,
) {
  switch (contextType) {
    case 'crop':
      return _cropLabel(l10n, info) ?? l10n.messageContextCrop;
    case 'trip':
      final from = info?.fromWoreda;
      final to = info?.toWoreda;
      if (from != null && from.isNotEmpty && to != null && to.isNotEmpty) {
        return '$from → $to';
      }
      return l10n.messageContextTrip;
    case 'order':
      final crop = _cropLabel(l10n, info);
      return crop == null
          ? l10n.messageContextOrder
          : '${l10n.messageContextOrder} • $crop';
    case 'delivery':
      final crop = _cropLabel(l10n, info);
      return crop == null
          ? l10n.messageContextDelivery
          : '${l10n.messageContextDelivery} • $crop';
    default:
      return contextType;
  }
}

String? _cropLabel(AppLocalizations l10n, ConversationContextInfo? info) {
  final key = info?.cropTypeKey;
  if (key == null || key.isEmpty) return null;
  final name = CropType.fromKey(key).displayName(l10n);
  final qty = info?.quantityKg;
  return qty != null && qty > 0 ? '$name • ${_formatKg(qty)} kg' : name;
}

/// Icon for the thread's context (crop-specific glyph when resolved).
IconData conversationContextIcon(
  String contextType,
  ConversationContextInfo? info,
) {
  switch (contextType) {
    case 'crop':
      final key = info?.cropTypeKey;
      return key != null && key.isNotEmpty
          ? CropType.fromKey(key).icon
          : Icons.eco_outlined;
    case 'trip':
      return Icons.route_outlined;
    case 'order':
      return Icons.receipt_long_outlined;
    case 'delivery':
      return Icons.local_shipping_outlined;
    default:
      return Icons.tag;
  }
}

String _formatKg(double qty) =>
    qty % 1 == 0 ? qty.toInt().toString() : qty.toStringAsFixed(1);

/// Compact "icon + label" row naming the crop/trip a thread is linked to.
/// Resolves best-effort through [resolver]; renders the generic type label
/// until (or unless) the entity resolves.
class ConversationContextTag extends StatelessWidget {
  final String contextType;
  final String contextId;
  final ConversationContextResolver? resolver;
  final Color? color;
  final double fontSize;

  /// When set, the tag becomes tappable (opens the linked entity) and shows
  /// a chevron as the affordance.
  final VoidCallback? onTap;

  const ConversationContextTag({
    required this.contextType,
    required this.contextId,
    this.resolver,
    this.color,
    this.fontSize = 12,
    this.onTap,
    super.key,
  });

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final effectiveColor =
        color ?? Theme.of(context).colorScheme.onSurface.withValues(alpha: 0.6);

    final tag = FutureBuilder<ConversationContextInfo?>(
      future: resolver?.resolve(contextType, contextId),
      initialData: resolver?.peek(contextType, contextId),
      builder: (context, snapshot) {
        final info = snapshot.data;
        return Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              conversationContextIcon(contextType, info),
              size: fontSize + 2,
              color: effectiveColor,
            ),
            const SizedBox(width: 4),
            Flexible(
              child: Text(
                conversationContextLabel(l10n, contextType, info),
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: TextStyle(
                  color: effectiveColor,
                  fontSize: fontSize,
                  fontWeight: FontWeight.w500,
                ),
              ),
            ),
            if (onTap != null)
              Icon(
                Icons.chevron_right,
                size: fontSize + 2,
                color: effectiveColor,
              ),
          ],
        );
      },
    );

    if (onTap == null) return tag;
    return Semantics(
      button: true,
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(6),
        child: tag,
      ),
    );
  }
}
