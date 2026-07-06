import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/delivery_error_l10n.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/delivery_detail_screen.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/widgets/delivery_card.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';

/// Browse and filter deliveries for the current user.
///
/// Shows status-based filter chips with a list of delivery cards.
/// The user's role is automatically locked to their authenticated role.
class DeliveryListScreen extends StatefulWidget {
  const DeliveryListScreen({super.key});

  @override
  State<DeliveryListScreen> createState() => _DeliveryListScreenState();
}

class _DeliveryListScreenState extends State<DeliveryListScreen> {
  String? _selectedStatus;

  /// The user's role from auth — always locked to their actual role.
  String? _userRole;

  @override
  void initState() {
    super.initState();
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) {
      _userRole = authState.role.name;
    }
    _loadDeliveries();
  }

  String? _getUserId() {
    final authState = context.read<AuthBloc>().state;
    if (authState is AuthAuthenticated) {
      return authState.userId;
    }
    return null;
  }

  void _loadDeliveries() {
    final userId = _getUserId();
    if (userId != null) {
      context.read<DeliveryBloc>().add(
            LoadUserDeliveries(userId: userId, role: _userRole),
          );
    }
  }

  void _applyFilters() {
    final userId = _getUserId();
    if (userId == null) return;

    context.read<DeliveryBloc>().add(
          FilterUserDeliveries(
            userId: userId,
            role: _userRole,
            statusFilter: _selectedStatus,
          ),
        );
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final l10n = AppLocalizations.of(context)!;
    final userId = _getUserId() ?? '';

    return BlocListener<DeliveryBloc, DeliveryState>(
      listener: (context, state) {
        if (state is DeliveryCreated) {
          _loadDeliveries();
        }
      },
      child: Column(
        children: [
          const OfflineBanner(),
          // Title
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 8),
            child: Align(
              alignment: Alignment.centerLeft,
              child: Text(
                l10n.deliveryMyDeliveries,
                style: theme.textTheme.headlineSmall?.copyWith(
                  fontWeight: FontWeight.bold,
                ),
              ),
            ),
          ),
          // Status filter chips
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 16),
            child: SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              child: Row(
                children: [
                  _StatusFilterChip(
                    label: l10n.deliveryFilterAll,
                    selected: _selectedStatus == null,
                    onSelected: () {
                      setState(() => _selectedStatus = null);
                      _applyFilters();
                    },
                  ),
                  const SizedBox(width: 8),
                  ..._buildStatusChips(),
                ],
              ),
            ),
          ),
          const SizedBox(height: 8),
          // Delivery list
          Expanded(
            child: BlocBuilder<DeliveryBloc, DeliveryState>(
              builder: (context, state) {
                if (state is DeliveryLoading) {
                  return const Center(child: CircularProgressIndicator());
                }

                if (state is DeliveryError) {
                  return Center(
                    child: Column(
                      mainAxisAlignment: MainAxisAlignment.center,
                      children: [
                        const Icon(Icons.error_outline, size: 48),
                        const SizedBox(height: 16),
                        Text(localizeDeliveryError(l10n, state.code)),
                        const SizedBox(height: 16),
                        FilledButton(
                          onPressed: _loadDeliveries,
                          child: Text(l10n.deliveryTryAgain),
                        ),
                      ],
                    ),
                  );
                }

                if (state is DeliveriesLoaded) {
                  if (state.deliveries.isEmpty) {
                    return Center(
                      child: Column(
                        mainAxisAlignment: MainAxisAlignment.center,
                        children: [
                          Icon(
                            Icons.local_shipping_outlined,
                            size: 80,
                            color: theme.colorScheme.secondary,
                          ),
                          const SizedBox(height: 24),
                          Text(
                            l10n.deliveryNoDeliveries,
                            style: theme.textTheme.titleLarge,
                          ),
                          const SizedBox(height: 8),
                          Text(
                            l10n.deliveryNoDeliveriesHint,
                            style: theme.textTheme.bodyMedium?.copyWith(
                              color: theme.colorScheme.onSurface
                                  .withValues(alpha: 0.6),
                            ),
                            textAlign: TextAlign.center,
                          ),
                        ],
                      ),
                    );
                  }

                  return RefreshIndicator(
                    onRefresh: () async {
                      _loadDeliveries();
                    },
                    child: ListView.builder(
                      padding: const EdgeInsets.only(top: 8, bottom: 80),
                      itemCount: state.deliveries.length,
                      itemBuilder: (context, index) {
                        final delivery = state.deliveries[index];
                        return DeliveryCard(
                          delivery: delivery,
                          currentUserId: userId,
                          onTap: () {
                            Navigator.push(
                              context,
                              MaterialPageRoute<void>(
                                builder: (context) => DeliveryDetailScreen(
                                  delivery: delivery,
                                  currentUserId: userId,
                                ),
                              ),
                            );
                          },
                        );
                      },
                    ),
                  );
                }

                // Initial state
                return Center(
                  child: Column(
                    mainAxisAlignment: MainAxisAlignment.center,
                    children: [
                      Icon(
                        Icons.local_shipping_outlined,
                        size: 80,
                        color: theme.colorScheme.secondary,
                      ),
                      const SizedBox(height: 24),
                      Text(
                        l10n.deliveryNoDeliveries,
                        style: theme.textTheme.titleLarge,
                      ),
                    ],
                  ),
                );
              },
            ),
          ),
        ],
      ),
    );
  }

  List<Widget> _buildStatusChips() {
    final l10n = AppLocalizations.of(context)!;
    final statuses = [
      ('pending', l10n.deliveryFilterPending),
      ('pickedUp', l10n.deliveryFilterPickedUp),
      ('inTransit', l10n.deliveryFilterInTransit),
      ('delivered', l10n.deliveryFilterDelivered),
      ('disputed', l10n.deliveryStatusDisputed),
      ('cancelled', l10n.deliveryStatusCancelled),
    ];

    final chips = <Widget>[];
    for (final (value, label) in statuses) {
      chips.add(
        _StatusFilterChip(
          label: label,
          selected: _selectedStatus == value,
          onSelected: () {
            setState(() => _selectedStatus = value);
            _applyFilters();
          },
        ),
      );
      chips.add(const SizedBox(width: 8));
    }
    return chips;
  }
}

class _StatusFilterChip extends StatelessWidget {
  final String label;
  final bool selected;
  final VoidCallback onSelected;

  const _StatusFilterChip({
    required this.label,
    required this.selected,
    required this.onSelected,
  });

  @override
  Widget build(BuildContext context) {
    return FilterChip(
      label: Text(label),
      selected: selected,
      onSelected: (_) => onSelected(),
    );
  }
}
