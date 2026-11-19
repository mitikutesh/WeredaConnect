import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/core/sync/sync_executor.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/widgets/notification_bell.dart';

/// BUG-34: a sync run that sent nothing (the listing's photo upload failed,
/// so its create was held back: 0 of 1 ok, 0 failed) read "Sync Complete"
/// in the sync sheet while the change still waited.
void main() {
  Future<AppLocalizations> l10nFor(String locale) =>
      AppLocalizations.delegate.load(Locale(locale));

  /// The run of the finding: one operation held back, none sent or failed.
  const heldBack = SyncBatchResult(total: 1, deferred: 1);

  test('a run that left changes waiting says so, in every language', () async {
    for (final locale in ['en', 'am', 'om']) {
      final l10n = await l10nFor(locale);

      final message = syncResultMessage(l10n, heldBack, pendingCount: 1);

      expect(message, l10n.syncStillWaiting(1), reason: locale);
      expect(message, isNot(l10n.syncComplete), reason: locale);
    }
  });

  test('the English text says they wait and will be sent again', () async {
    final l10n = await l10nFor('en');

    expect(
      syncResultMessage(l10n, heldBack, pendingCount: 1),
      '1 change is still waiting. It will be sent again by itself.',
    );
    expect(
      syncResultMessage(l10n, heldBack, pendingCount: 3),
      '3 changes are still waiting. They will be sent again by themselves.',
    );
    // An empty batch after one that held changes back.
    expect(
      syncResultMessage(l10n, const SyncBatchResult.empty(), pendingCount: 2),
      '2 changes are still waiting. They will be sent again by themselves.',
    );
  });

  test('a run that emptied the queue still says the sync is complete',
      () async {
    final l10n = await l10nFor('en');

    expect(
      syncResultMessage(
        l10n,
        const SyncBatchResult(total: 1, succeeded: 1),
        pendingCount: 0,
      ),
      l10n.syncComplete,
    );
    expect(
      syncResultMessage(l10n, const SyncBatchResult.empty(), pendingCount: 0),
      l10n.syncComplete,
    );
  });

  test('the other outcomes keep their own line', () async {
    final l10n = await l10nFor('en');

    expect(
      syncResultMessage(
        l10n,
        const SyncBatchResult.noInternet(),
        pendingCount: 1,
      ),
      l10n.offlineModeIndicator,
    );
    expect(
      syncResultMessage(l10n, const SyncBatchResult.skipped(), pendingCount: 1),
      isNull,
    );
  });
}
