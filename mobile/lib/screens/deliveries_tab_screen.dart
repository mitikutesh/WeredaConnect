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
import 'package:wereda_connect/shared/layout/breakpoints.dart';
import 'package:wereda_connect/shared/widgets/green_header.dart';
import 'package:wereda_connect/shared/widgets/offline_banner.dart';

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

    final isMobile = Breakpoints.isMobile(context);
    final isDesktop = Breakpoints.isDesktop(context);
    final hPad = isDesktop ? 32.0 : 24.0;
    final listHPad = isMobile ? 20.0 : 24.0;

    // ── Profile avatar (shared between mobile and desktop headers) ──
    final profileAvatar = GestureDetector(
      onTap: () => Navigator.push(
        context,
        MaterialPageRoute<void>(
          builder: (_) => const ProfileScreen(),
        ),
      ),
      child: CircleAvatar(
        radius: 18,
        backgroundColor:
            isMobile ? Colors.white24 : AppTheme.textMuted.withAlpha(40),
        child: Icon(
          Icons.person,
          size: 20,
          color: isMobile ? Colors.white : AppTheme.textDark,
        ),
      ),
    );

    // ── Filter chips row ──────────────────────────────────────────
    final Widget filterChips;
    if (isMobile) {
      filterChips = SizedBox(
        height: 50,
        child: ListView(
          scrollDirection: Axis.horizontal,
          padding: EdgeInsets.symmetric(horizontal: listHPad, vertical: 8),
          children: [
            _buildChip(null, l10n.deliveryFilterAll),
            _buildChip('pending', l10n.deliveryFilterPending),
            _buildChip('pickedUp', l10n.deliveryFilterPickedUp),
            _buildChip('inTransit', l10n.deliveryFilterInTransit),
            _buildChip('delivered', l10n.deliveryFilterDelivered),
          ],
        ),
      );
    } else {
      filterChips = Padding(
        padding: EdgeInsets.symmetric(horizontal: listHPad, vertical: 8),
        child: Row(
          children: [
            Expanded(
              child: _buildChip(null, l10n.deliveryFilterAll, expand: true),
            ),
            const SizedBox(width: 10),
            Expanded(
              child: _buildChip(
                'pending',
                l10n.deliveryFilterPending,
                expand: true,
              ),
            ),
            const SizedBox(width: 10),
            Expanded(
              child: _buildChip(
                'pickedUp',
                l10n.deliveryFilterPickedUp,
                expand: true,
              ),
            ),
            const SizedBox(width: 10),
            Expanded(
              child: _buildChip(
                'inTransit',
                l10n.deliveryFilterInTransit,
                expand: true,
              ),
            ),
            const SizedBox(width: 10),
            Expanded(
              child: _buildChip(
                'delivered',
                l10n.deliveryFilterDelivered,
                expand: true,
              ),
            ),
          ],
        ),
      );
    }

    // ── Delivery list ─────────────────────────────────────────────
    final deliveryList = Expanded(
      child: BlocBuilder<DeliveryBloc, DeliveryState>(
        builder: (context, state) {
          if (state is DeliveryLoading) {
            return const Center(child: CircularProgressIndicator());
          }

          if (state is DeliveryError) {
            return Center(
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 32),
                child: Column(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Icon(
                      Icons.local_shipping_outlined,
                      size: 56,
                      color: AppTheme.textMuted.withAlpha(150),
                    ),
                    const SizedBox(height: 16),
                    Text(
                      l10n.noDeliveries,
                      style: const TextStyle(
                        fontSize: 16,
                        fontWeight: FontWeight.w600,
                        color: AppTheme.textDark,
                      ),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      l10n.deliveryNoDeliveriesHint,
                      style: const TextStyle(
                        fontSize: 13,
                        color: AppTheme.textMuted,
                      ),
                      textAlign: TextAlign.center,
                    ),
                    const SizedBox(height: 20),
                    SizedBox(
                      width: 200,
                      child: FilledButton(
                        onPressed: _loadDeliveries,
                        child: Text(l10n.retry),
                      ),
                    ),
                  ],
                ),
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
                padding: EdgeInsets.symmetric(
                  horizontal: listHPad,
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
    );

    // ── Mobile layout (unchanged) ─────────────────────────────────
    if (isMobile) {
      return Column(
        children: [
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
                    profileAvatar,
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
          const OfflineBanner(),
          filterChips,
          deliveryList,
        ],
      );
    }

    // ── Desktop / Tablet layout ───────────────────────────────────
    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 900),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            // ── Flat page header ──────────────────────────────────
            Padding(
              padding: EdgeInsets.fromLTRB(hPad, 28, hPad, 20),
              child: Column(
                children: [
                  Text(
                    l10n.myDeliveries,
                    style: const TextStyle(
                      fontSize: 24,
                      fontWeight: FontWeight.w700,
                      color: AppTheme.textDark,
                    ),
                    textAlign: TextAlign.center,
                  ),
                  const SizedBox(height: 4),
                  Text(
                    l10n.trackDeliveries,
                    style: const TextStyle(
                      fontSize: 14,
                      color: AppTheme.textMuted,
                    ),
                    textAlign: TextAlign.center,
                  ),
                ],
              ),
            ),
            filterChips,
            deliveryList,
          ],
        ),
      ),
    );
  }

  Widget _buildChip(String? status, String label, {bool expand = false}) {
    final isSelected = _selectedStatus == status;
    final chip = FilterChip(
      label: expand
          ? SizedBox(
              width: double.infinity,
              child: Text(label, textAlign: TextAlign.center),
            )
          : Text(label),
      selected: isSelected,
      labelStyle: TextStyle(
        color: isSelected ? Colors.white : AppTheme.textDark,
      ),
      onSelected: (_) => _applyFilter(status),
    );
    if (expand) return chip;
    return Padding(
      padding: const EdgeInsets.only(right: 8),
      child: chip,
    );
  }
}
