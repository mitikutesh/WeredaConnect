import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_bloc.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_event.dart';
import 'package:wereda_connect/features/rating/presentation/bloc/rating_state.dart';
import 'package:wereda_connect/features/rating/presentation/widgets/star_rating.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Dialog for a buyer to rate a counterparty (farmer or trucker) after a
/// completed order. Requires an ancestor [RatingBloc] provider.
///
/// Pops with `true` on a successful submit, `false`/null otherwise.
class RateCounterpartyDialog extends StatefulWidget {
  final String orderId;
  final String raterId;
  final String rateeId;

  /// `farmer` or `trucker`.
  final String rateeRole;
  final String rateeName;

  const RateCounterpartyDialog({
    required this.orderId,
    required this.raterId,
    required this.rateeId,
    required this.rateeRole,
    required this.rateeName,
    super.key,
  });

  @override
  State<RateCounterpartyDialog> createState() => _RateCounterpartyDialogState();
}

class _RateCounterpartyDialogState extends State<RateCounterpartyDialog> {
  int _stars = 5;
  final _commentController = TextEditingController();

  @override
  void dispose() {
    _commentController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);
    final title =
        widget.rateeRole == 'trucker' ? l10n.rateTrucker : l10n.rateFarmer;

    return BlocConsumer<RatingBloc, RatingState>(
      listenWhen: (previous, current) =>
          current is RatingSubmitted || current is RatingError,
      listener: (context, state) {
        if (state is RatingSubmitted) {
          Navigator.of(context).pop(true);
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(content: Text(l10n.ratingThankYou)),
          );
        } else if (state is RatingError) {
          ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(content: Text(localizeErrorCode(state.code, l10n))),
          );
        }
      },
      builder: (context, state) {
        final isSubmitting = state is RatingSubmitting;
        return AlertDialog(
          title: Text(title),
          content: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                widget.rateeName,
                style: theme.textTheme.titleMedium,
              ),
              const SizedBox(height: 16),
              Center(
                child: StarRating(
                  rating: _stars.toDouble(),
                  size: 40,
                  onRatingChanged: (value) => setState(() => _stars = value),
                ),
              ),
              const SizedBox(height: 16),
              TextField(
                controller: _commentController,
                maxLines: 3,
                maxLength: 500,
                textCapitalization: TextCapitalization.sentences,
                decoration: InputDecoration(
                  hintText: l10n.ratingCommentHint,
                  border: const OutlineInputBorder(),
                ),
              ),
            ],
          ),
          actions: [
            TextButton(
              onPressed:
                  isSubmitting ? null : () => Navigator.of(context).pop(false),
              child: Text(l10n.cancel),
            ),
            FilledButton(
              onPressed: isSubmitting
                  ? null
                  : () => context.read<RatingBloc>().add(
                        SubmitRating(
                          orderId: widget.orderId,
                          raterId: widget.raterId,
                          rateeId: widget.rateeId,
                          rateeRole: widget.rateeRole,
                          stars: _stars,
                          comment: _commentController.text,
                        ),
                      ),
              child: isSubmitting
                  ? const SizedBox(
                      height: 18,
                      width: 18,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : Text(l10n.submitRating),
            ),
          ],
        );
      },
    );
  }
}
