import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/sync/connectivity_monitor.dart';
import 'package:wereda_connect/features/account/data/repositories/account_deletion_repository.dart';
import 'package:wereda_connect/features/account/presentation/bloc/account_deletion_bloc.dart';
import 'package:wereda_connect/features/account/presentation/bloc/account_deletion_event.dart';
import 'package:wereda_connect/features/account/presentation/bloc/account_deletion_state.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/auth/widgets/step_up_dialog.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Profile > "Delete account" (specs/contracts/account-deletion.md), the
/// same for Farmer, Trucker and Buyer on mobile and web.
///
/// Explains what is deleted and what is kept, asks once to confirm, then
/// sends the request (online only). Blocking items are listed by type; the
/// password prompt is the AUTH_040 step-up used for payments. On success the
/// AuthBloc wipes the device and the app returns to the login screen.
class DeleteAccountScreen extends StatelessWidget {
  /// Sends the request; defaults to the app's API client (tests inject one).
  final AccountDeletionRepository? repository;

  /// Whether the device is online; defaults to the platform check.
  final Future<bool> Function()? isOnline;

  /// Asks for the password again (AUTH_040). Defaults to [showStepUpDialog].
  final Future<bool> Function(BuildContext context)? confirmPassword;

  const DeleteAccountScreen({
    this.repository,
    this.isOnline,
    this.confirmPassword,
    super.key,
  });

  AccountDeletionBloc _createBloc(BuildContext context) {
    final auth = context.read<AuthBloc>();
    final session = auth.state;
    // The account this screen deletes. Every send is checked against it,
    // and only its data is wiped.
    final userId = session is AuthAuthenticated ? session.userId : '';
    final phone = session is AuthAuthenticated ? session.phoneNumber : null;
    return AccountDeletionBloc(
      repository: repository ?? AccountDeletionRepository(),
      userId: userId,
      isOnline:
          isOnline ?? ConnectivityMonitor(dio: ApiClient().dio).isConnected,
      isSameAccount: () => auth.isSignedInAs(userId),
      onDeleted: () =>
          auth.add(AuthAccountDeleted(userId: userId, phoneNumber: phone)),
      onAccountMissing: () => auth.add(const AuthLogoutRequested()),
    );
  }

  @override
  Widget build(BuildContext context) {
    return BlocProvider<AccountDeletionBloc>(
      create: _createBloc,
      child: _DeleteAccountView(
        confirmPassword: confirmPassword ?? showStepUpDialog,
      ),
    );
  }
}

class _DeleteAccountView extends StatelessWidget {
  final Future<bool> Function(BuildContext context) confirmPassword;

  const _DeleteAccountView({required this.confirmPassword});

  Future<void> _onState(
    BuildContext context,
    AccountDeletionState state,
  ) async {
    final bloc = context.read<AccountDeletionBloc>();
    if (state is AccountDeletionStepUpRequired) {
      final confirmed = await confirmPassword(context);
      if (bloc.isClosed) return;
      bloc.add(
        confirmed
            ? const AccountDeletionRequested()
            : const AccountDeletionStepUpCancelled(),
      );
    } else if (state is AccountDeletionSucceeded) {
      // The root messenger keeps it on screen after the app returns to login.
      ScaffoldMessenger.maybeOf(context)?.showSnackBar(
        SnackBar(
          content: Text(AppLocalizations.of(context)!.deleteAccountDone),
        ),
      );
    }
  }

  Future<void> _confirmAndDelete(BuildContext context) async {
    final l10n = AppLocalizations.of(context)!;
    final bloc = context.read<AccountDeletionBloc>();
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text(l10n.deleteAccountConfirmTitle),
        content: Text(l10n.deleteAccountConfirmBody),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(false),
            child: Text(l10n.cancel),
          ),
          TextButton(
            key: const Key('delete_account_confirm'),
            onPressed: () => Navigator.of(dialogContext).pop(true),
            style: TextButton.styleFrom(foregroundColor: AppTheme.statusError),
            child: Text(l10n.delete),
          ),
        ],
      ),
    );
    // Escape or a barrier tap returns null, which is not a yes.
    if (confirmed == true && !bloc.isClosed) {
      bloc.add(const AccountDeletionRequested());
    }
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    return BlocConsumer<AccountDeletionBloc, AccountDeletionState>(
      listener: _onState,
      builder: (context, state) {
        final busy = state is AccountDeletionSubmitting ||
            state is AccountDeletionStepUpRequired ||
            state is AccountDeletionSucceeded;
        final failed = state is AccountDeletionFailed ? state : null;
        final retry = failed != null && failed.canRetry;

        return Scaffold(
          appBar: AppBar(title: Text(l10n.deleteAccount)),
          body: SafeArea(
            child: SingleChildScrollView(
              padding: const EdgeInsets.all(20),
              child: Center(
                child: ConstrainedBox(
                  constraints: const BoxConstraints(maxWidth: 600),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      Row(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          const Icon(
                            Icons.warning_amber_rounded,
                            color: AppTheme.statusError,
                          ),
                          const SizedBox(width: 12),
                          Expanded(
                            child: Text(
                              l10n.deleteAccountWarning,
                              style: const TextStyle(
                                fontSize: 15,
                                fontWeight: FontWeight.w600,
                                color: AppTheme.textDark,
                              ),
                            ),
                          ),
                        ],
                      ),
                      const SizedBox(height: 20),
                      _Heading(l10n.deleteAccountDeletedHeading),
                      _Bullet(
                        Icons.remove_circle_outline,
                        l10n.deleteAccountDeletedProfile,
                      ),
                      _Bullet(
                        Icons.remove_circle_outline,
                        l10n.deleteAccountDeletedFayda,
                      ),
                      _Bullet(
                        Icons.remove_circle_outline,
                        l10n.deleteAccountDeletedListings,
                      ),
                      _Bullet(
                        Icons.remove_circle_outline,
                        l10n.deleteAccountDeletedMessages,
                      ),
                      _Bullet(
                        Icons.remove_circle_outline,
                        l10n.deleteAccountDeletedNotifications,
                      ),
                      const SizedBox(height: 16),
                      _Heading(l10n.deleteAccountKeptHeading),
                      _Bullet(
                        Icons.inventory_2_outlined,
                        l10n.deleteAccountKeptRecords,
                      ),
                      Padding(
                        padding: const EdgeInsets.only(left: 32, top: 4),
                        child: Text(
                          l10n.deleteAccountKeptReason,
                          style: const TextStyle(
                            fontSize: 13,
                            color: AppTheme.textMuted,
                          ),
                        ),
                      ),
                      const SizedBox(height: 16),
                      _Bullet(Icons.wifi, l10n.deleteAccountNeedsInternet),
                      const SizedBox(height: 20),
                      if (state is AccountDeletionSubmitting)
                        _Progress(
                          state.finishing
                              ? l10n.deleteAccountFinishing
                              : l10n.deleteAccountDeleting,
                        ),
                      if (state is AccountDeletionBlocked)
                        _BlockersCard(state: state),
                      if (failed != null)
                        _Notice(
                          key: const Key('delete_account_error'),
                          text: _failureText(failed.reason, l10n),
                        ),
                      const SizedBox(height: 20),
                      FilledButton.icon(
                        key: const Key('delete_account_button'),
                        onPressed: busy ||
                                failed?.reason ==
                                    AccountDeletionFailure.suspended
                            ? null
                            : retry
                                ? () => context
                                    .read<AccountDeletionBloc>()
                                    .add(const AccountDeletionRequested())
                                : () => _confirmAndDelete(context),
                        icon:
                            Icon(retry ? Icons.refresh : Icons.delete_forever),
                        label: Text(
                          retry ? l10n.retry : l10n.deleteAccountButton,
                        ),
                        style: FilledButton.styleFrom(
                          backgroundColor: AppTheme.statusError,
                          foregroundColor: Colors.white,
                          padding: const EdgeInsets.symmetric(vertical: 14),
                        ),
                      ),
                    ],
                  ),
                ),
              ),
            ),
          ),
        );
      },
    );
  }

  static String _failureText(
    AccountDeletionFailure reason,
    AppLocalizations l10n,
  ) =>
      switch (reason) {
        AccountDeletionFailure.offline => l10n.deleteAccountOffline,
        AccountDeletionFailure.network => l10n.deleteAccountNetworkError,
        AccountDeletionFailure.incomplete => l10n.deleteAccountIncomplete,
        AccountDeletionFailure.suspended => l10n.deleteAccountSuspended,
        AccountDeletionFailure.accountNotFound ||
        AccountDeletionFailure.unexpected =>
          l10n.deleteAccountFailed,
      };
}

/// The blocking items, one line per type with its count (`409 ACCOUNT_002`).
class _BlockersCard extends StatelessWidget {
  final AccountDeletionBlocked state;

  const _BlockersCard({required this.state});

  /// Known blocker types, in display order. Any other type is counted under
  /// one generic line: the contract keeps the set open.
  static const _knownTypes = [
    'order',
    'escrow',
    'delivery',
    'dispute',
    'payment_review',
  ];

  static String _line(String type, int count, AppLocalizations l10n) =>
      switch (type) {
        'order' => l10n.deleteAccountBlockerOrder(count),
        'escrow' => l10n.deleteAccountBlockerEscrow(count),
        'delivery' => l10n.deleteAccountBlockerDelivery(count),
        'dispute' => l10n.deleteAccountBlockerDispute(count),
        'payment_review' => l10n.deleteAccountBlockerPaymentReview(count),
        _ => l10n.deleteAccountBlockerOther(count),
      };

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final counts = <String, int>{};
    for (final blocker in state.blockers) {
      final type = _knownTypes.contains(blocker.type) ? blocker.type : 'other';
      counts[type] = (counts[type] ?? 0) + 1;
    }
    final more = state.blockerCount - state.blockers.length;

    return Container(
      key: const Key('delete_account_blockers'),
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: AppTheme.statusErrorContainer,
        borderRadius: BorderRadius.circular(12),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            l10n.deleteAccountBlockedTitle,
            style: const TextStyle(
              fontSize: 15,
              fontWeight: FontWeight.w700,
              color: AppTheme.onStatusErrorContainer,
            ),
          ),
          const SizedBox(height: 6),
          Text(
            l10n.deleteAccountBlockedIntro,
            style: const TextStyle(color: AppTheme.onStatusErrorContainer),
          ),
          for (final type in [..._knownTypes, 'other'])
            if (counts[type] != null)
              Padding(
                padding: const EdgeInsets.only(top: 6),
                child: Text(
                  _line(type, counts[type]!, l10n),
                  key: Key('delete_account_blocker_$type'),
                  style: const TextStyle(
                    color: AppTheme.onStatusErrorContainer,
                  ),
                ),
              ),
          if (more > 0)
            Padding(
              padding: const EdgeInsets.only(top: 6),
              child: Text(
                l10n.deleteAccountBlockersMore(more),
                style: const TextStyle(color: AppTheme.onStatusErrorContainer),
              ),
            ),
        ],
      ),
    );
  }
}

class _Heading extends StatelessWidget {
  final String text;

  const _Heading(this.text);

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Text(
        text,
        style: const TextStyle(
          fontSize: 14,
          fontWeight: FontWeight.w700,
          color: AppTheme.textDark,
        ),
      ),
    );
  }
}

class _Bullet extends StatelessWidget {
  final IconData icon;
  final String text;

  const _Bullet(this.icon, this.text);

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(icon, size: 20, color: AppTheme.textMuted),
          const SizedBox(width: 12),
          Expanded(
            child: Text(
              text,
              style: const TextStyle(fontSize: 14, color: AppTheme.textDark),
            ),
          ),
        ],
      ),
    );
  }
}

class _Progress extends StatelessWidget {
  final String text;

  const _Progress(this.text);

  @override
  Widget build(BuildContext context) {
    return Row(
      children: [
        const SizedBox(
          width: 20,
          height: 20,
          child: CircularProgressIndicator(strokeWidth: 2),
        ),
        const SizedBox(width: 12),
        Expanded(child: Text(text)),
      ],
    );
  }
}

class _Notice extends StatelessWidget {
  final String text;

  const _Notice({required this.text, super.key});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: AppTheme.statusErrorContainer,
        borderRadius: BorderRadius.circular(12),
      ),
      child: Text(
        text,
        style: const TextStyle(color: AppTheme.onStatusErrorContainer),
      ),
    );
  }
}
