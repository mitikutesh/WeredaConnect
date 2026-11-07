import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/errors/app_error.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/core/sync/listing_report_rules.dart';
import 'package:wereda_connect/features/listing_report/domain/base_listing_report_repository.dart';
import 'package:wereda_connect/features/live_trip/presentation/widgets/pause_sheet.dart'
    show SheetHandle;
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// FUP-67: the low-key "Report this listing" button at the end of a crop or
/// trip detail screen. Shown to buyers only: buyers see every listing, a
/// farmer or a trucker sees only their own, so there is nothing for them to
/// report.
class ReportListingButton extends StatelessWidget {
  const ReportListingButton({
    required this.listingType,
    required this.listingId,
    required this.reporterId,
    super.key,
  });

  final ReportedListingType listingType;
  final String listingId;

  /// The signed-in buyer.
  final String reporterId;

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return Align(
      alignment: AlignmentDirectional.centerStart,
      child: TextButton.icon(
        key: const Key('report_listing_button'),
        onPressed: () => showReportListingSheet(
          context,
          listingType: listingType,
          listingId: listingId,
          reporterId: reporterId,
        ),
        icon: const Icon(Icons.flag_outlined, size: 18),
        label: Text(l10n.reportListing),
        style: TextButton.styleFrom(
          foregroundColor: AppTheme.mutedSteel,
          minimumSize: const Size(48, 48),
          textStyle: const TextStyle(
            fontFamily: AppTheme.fontFamily,
            fontFamilyFallback: AppTheme.ethiopicFontFallback,
            fontSize: 13,
            fontWeight: FontWeight.w600,
          ),
        ),
      ),
    );
  }
}

/// Opens the report sheet; after a report it tells the buyer what happened.
/// The repository is read from [context] (`BaseListingReportRepository`).
Future<void> showReportListingSheet(
  BuildContext context, {
  required ReportedListingType listingType,
  required String listingId,
  required String reporterId,
}) async {
  final repository = context.read<BaseListingReportRepository>();
  final messenger = ScaffoldMessenger.maybeOf(context);
  final l10n = AppLocalizations.of(context)!;
  final outcome = await showModalBottomSheet<ListingReportOutcome>(
    context: context,
    isScrollControlled: true,
    backgroundColor: AppTheme.surfaceWhite,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(28)),
    ),
    builder: (_) => ReportListingSheet(
      repository: repository,
      listingType: listingType,
      listingId: listingId,
      reporterId: reporterId,
    ),
  );
  if (outcome == null) return;
  messenger?.showSnackBar(
    SnackBar(
      content: Text(
        outcome == ListingReportOutcome.alreadyReported
            ? l10n.reportListingAlreadySent
            : l10n.reportListingSent,
      ),
    ),
  );
}

/// The sheet: the question, the privacy note, one row per reason and Send
/// report. Send stays off until a reason is picked; a failure is shown in the
/// sheet so the buyer can try again without picking again. Closes with the
/// outcome once the report is sent (web) or queued (mobile).
class ReportListingSheet extends StatefulWidget {
  const ReportListingSheet({
    required this.repository,
    required this.listingType,
    required this.listingId,
    required this.reporterId,
    super.key,
  });

  final BaseListingReportRepository repository;
  final ReportedListingType listingType;
  final String listingId;
  final String reporterId;

  @override
  State<ReportListingSheet> createState() => _ReportListingSheetState();
}

class _ReportListingSheetState extends State<ReportListingSheet> {
  ListingReportReason? _reason;
  bool _sending = false;
  String? _error;

  static String _label(ListingReportReason reason, AppLocalizations l10n) =>
      switch (reason) {
        ListingReportReason.contactInfo => l10n.reportReasonContactInfo,
        ListingReportReason.misleading => l10n.reportReasonMisleading,
        ListingReportReason.scam => l10n.reportReasonScam,
        ListingReportReason.offensive => l10n.reportReasonOffensive,
        ListingReportReason.other => l10n.reportReasonOther,
      };

  Future<void> _send() async {
    final reason = _reason;
    if (reason == null || _sending) return;
    final l10n = AppLocalizations.of(context)!;
    setState(() {
      _sending = true;
      _error = null;
    });
    try {
      final outcome = await widget.repository.report(
        reporterId: widget.reporterId,
        listingType: widget.listingType,
        listingId: widget.listingId,
        reason: reason,
      );
      if (mounted) Navigator.of(context).pop(outcome);
    } on Object catch (e) {
      if (!mounted) return;
      setState(() {
        _sending = false;
        _error = localizeErrorCode(_errorCode(e), l10n);
      });
    }
  }

  /// A /sync refusal (web) reads as the queue's refusal of a report would
  /// (the daily limit, a held account, else "not sent"); anything else, such
  /// as no connection or a full queue, as usual.
  static AppErrorCode _errorCode(Object error) => error is SyncRejectedException
      ? syncRejectionErrorCode(
          entityType: listingReportEntityType,
          action: 'create',
          message: error.message,
        )
      : mapErrorToCode(error);

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return SafeArea(
      child: SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(22, 12, 22, 24),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            const SheetHandle(),
            const SizedBox(height: 16),
            Text(
              l10n.reportListingTitle,
              style: const TextStyle(
                fontSize: 22,
                fontWeight: FontWeight.w800,
                letterSpacing: -0.4,
                color: AppTheme.soilDark,
              ),
            ),
            const SizedBox(height: 6),
            Text(
              l10n.reportListingPrivacyNote,
              style: const TextStyle(
                fontSize: 14,
                height: 1.5,
                color: AppTheme.unselectedNav,
              ),
            ),
            const SizedBox(height: 16),
            RadioGroup<ListingReportReason>(
              groupValue: _reason,
              onChanged: (value) {
                if (value != null && !_sending) {
                  setState(() => _reason = value);
                }
              },
              child: Column(
                children: [
                  for (final (i, reason) in ListingReportReason.values.indexed)
                    Padding(
                      padding: EdgeInsets.only(top: i == 0 ? 0 : 8),
                      child: _ReasonRow(
                        key: Key('report_reason_${reason.wire}'),
                        reason: reason,
                        label: _label(reason, l10n),
                        selected: reason == _reason,
                        onTap: _sending
                            ? null
                            : () => setState(() => _reason = reason),
                      ),
                    ),
                ],
              ),
            ),
            if (_error != null) ...[
              const SizedBox(height: 12),
              Text(
                _error!,
                key: const Key('report_listing_error'),
                style: const TextStyle(
                  fontSize: 13,
                  color: AppTheme.statusError,
                ),
              ),
            ],
            const SizedBox(height: 16),
            FilledButton(
              key: const Key('report_listing_send'),
              onPressed: _reason == null || _sending ? null : _send,
              style: FilledButton.styleFrom(
                backgroundColor: AppTheme.soilDark,
                foregroundColor: Colors.white,
                minimumSize: const Size.fromHeight(56),
                textStyle: const TextStyle(
                  fontFamily: AppTheme.fontFamily,
                  fontFamilyFallback: AppTheme.ethiopicFontFallback,
                  fontSize: 16,
                  fontWeight: FontWeight.w800,
                ),
                shape: RoundedRectangleBorder(
                  borderRadius: BorderRadius.circular(16),
                ),
              ),
              child: _sending
                  ? const SizedBox(
                      width: 22,
                      height: 22,
                      child: CircularProgressIndicator(
                        strokeWidth: 2.5,
                        color: Colors.white,
                      ),
                    )
                  : Text(l10n.reportListingSend),
            ),
            const SizedBox(height: 6),
            TextButton(
              key: const Key('report_listing_cancel'),
              onPressed: _sending ? null : () => Navigator.of(context).pop(),
              style: TextButton.styleFrom(
                foregroundColor: AppTheme.unselectedNav,
                minimumSize: const Size.fromHeight(44),
              ),
              child: Text(l10n.cancel),
            ),
          ],
        ),
      ),
    );
  }
}

/// One reason: a radio and its label in a bordered row (the pause sheet's
/// row).
class _ReasonRow extends StatelessWidget {
  const _ReasonRow({
    required this.reason,
    required this.label,
    required this.selected,
    required this.onTap,
    super.key,
  });

  final ListingReportReason reason;
  final String label;
  final bool selected;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    return Material(
      color: AppTheme.surfaceWhite,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(14),
        side: BorderSide(
          color: selected ? AppTheme.soilDark : AppTheme.whisperBorder,
          width: selected ? 2 : 1,
        ),
      ),
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        child: Container(
          constraints: const BoxConstraints(minHeight: 52),
          padding: const EdgeInsets.symmetric(horizontal: 14),
          child: Row(
            children: [
              SizedBox(
                width: 20,
                height: 20,
                child: Radio<ListingReportReason>(
                  value: reason,
                  activeColor: AppTheme.soilDark,
                  materialTapTargetSize: MaterialTapTargetSize.shrinkWrap,
                  visualDensity: VisualDensity.compact,
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Text(
                  label,
                  style: TextStyle(
                    fontSize: 15,
                    fontWeight: selected ? FontWeight.w700 : FontWeight.w600,
                    color: AppTheme.soilDark,
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
