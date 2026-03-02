import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_bloc.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_event.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/bloc/delivery_state.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/screens/delivery_detail_screen.dart';
import 'package:wereda_connect/features/delivery_verification/presentation/widgets/delivery_card.dart';
import 'package:wereda_connect/screens/profile_screen.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';
import 'package:wereda_connect/shared/widgets/green_header.dart';

/// Deliveries tab screen — shows user's deliveries with status filter chips.
///
/// Uses GreenHeader for visual consistency with other tabs.
/// Reads from DeliveryBloc (provided at app root) via LoadUserDeliveries.
class DeliveriesTabScreen extends StatefulWidget {
  const DeliveriesTabScreen({super.key});

  @override
  State<DeliveriesTabScreen> createState() => _DeliveriesTabScreenState();
}

class _DeliveriesTabScreenState extends State<DeliveriesTabScreen> {
  String? _selectedStatus;
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
    if (authState is AuthAuthenticated) return authState.userId;
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

  void _applyFilter(String? status) {
    setState(() => _selectedStatus = status);
    final userId = _getUserId();
    if (userId == null) return;
    context.read<DeliveryBloc>().add(
          FilterUserDeliveries(
            userId: userId,
            role: _userRole,
            statusFilter: status,
          ),
        );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;

    return Column(
      children: [
        // ── Header ──────────────────────────────────────────────────
        GreenHeader(
          height: 120,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const SizedBox(height: 8),
              Row(
                children: [
                  Expanded(
                    child: Text(
                      l10n.myDeliveries,
                      style: const TextStyle(
                        fontSize: 22,
                        fontWeight: FontWeight.w700,
                        color: Colors.white,
                      ),
                    ),
                  ),
                  GestureDetector(
                    onTap: () => Navigator.push(
                      context,
                      MaterialPageRoute<void>(
                        builder: (_) => const ProfileScreen(),
                      ),
                    ),
                    child: const CircleAvatar(
                      radius: 18,
                      backgroundColor: Colors.white24,
                      child: Icon(
                        Icons.person,
                        size: 20,
                        color: Colors.white,
                      ),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 4),
              Text(
                l10n.trackDeliveries,
                style: TextStyle(
                  fontSize: 14,
                  color: Colors.white.withAlpha(180),
                ),
              ),
            ],
          ),
        ),

        // ── Status filter chips ─────────────────────────────────────
        SizedBox(
          height: 50,
          child: ListView(
            scrollDirection: Axis.horizontal,
            padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 8),
            children: [
              _buildChip(null, l10n.deliveryFilterAll),
              _buildChip('pending', l10n.deliveryFilterPending),
              _buildChip('pickedUp', l10n.deliveryFilterPickedUp),
              _buildChip('inTransit', l10n.deliveryFilterInTransit),
              _buildChip('delivered', l10n.deliveryFilterDelivered),
            ],
          ),
        ),

        // ── Delivery list ───────────────────────────────────────────
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
                      Text(state.message),
                      const SizedBox(height: 16),
                      FilledButton(
                        onPressed: _loadDeliveries,
                        child: Text(l10n.retry),
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
                        const Icon(
                          Icons.local_shipping_outlined,
                          size: 64,
                          color: AppTheme.textMuted,
                        ),
                        const SizedBox(height: 16),
                        Text(
                          l10n.noDeliveries,
                          style: const TextStyle(
                            fontSize: 16,
                            fontWeight: FontWeight.w500,
                            color: AppTheme.textMuted,
                          ),
                        ),
                        const SizedBox(height: 4),
                        Text(
                          l10n.deliveryNoDeliveriesHint,
                          style: const TextStyle(
                            fontSize: 13,
                            color: AppTheme.textMuted,
                          ),
                        ),
                      ],
                    ),
                  );
                }

                return RefreshIndicator(
                  onRefresh: () async => _loadDeliveries(),
                  child: ListView.separated(
                    padding: const EdgeInsets.symmetric(
                      horizontal: 20,
                      vertical: 12,
                    ),
                    itemCount: state.deliveries.length,
                    separatorBuilder: (_, __) => const SizedBox(height: 10),
                    itemBuilder: (context, index) {
                      final delivery = state.deliveries[index];
                      final userId = _getUserId() ?? '';
                      return DeliveryCard(
                        delivery: delivery,
                        currentUserId: userId,
                        onTap: () {
                          Navigator.push(
                            context,
                            MaterialPageRoute<void>(
                              builder: (_) => DeliveryDetailScreen(
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
                    const Icon(
                      Icons.local_shipping_outlined,
                      size: 64,
                      color: AppTheme.textMuted,
                    ),
                    const SizedBox(height: 16),
                    Text(
                      l10n.noDeliveries,
                      style: const TextStyle(
                        fontSize: 16,
                        color: AppTheme.textMuted,
                      ),
                    ),
                  ],
                ),
              );
            },
          ),
        ),
      ],
    );
  }

  Widget _buildChip(String? status, String label) {
    final isSelected = _selectedStatus == status;
    return Padding(
      padding: const EdgeInsets.only(right: 8),
      child: FilterChip(
        label: Text(label),
        selected: isSelected,
        labelStyle: TextStyle(
          color: isSelected ? Colors.white : AppTheme.textDark,
        ),
        onSelected: (_) => _applyFilter(status),
      ),
    );
  }
}
