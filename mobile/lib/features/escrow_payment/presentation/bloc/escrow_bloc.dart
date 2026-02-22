import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/escrow_payment/data/repositories/escrow_repository.dart'
    as repo;
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_event.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_state.dart';

/// BLoC for managing escrow payment state.
///
/// Handles payment initiation (online/offline), status viewing, history,
/// dispute raising, and intent cancellation. Wires UI events to
/// [EscrowRepository] for data operations.
class EscrowBloc extends Bloc<EscrowEvent, EscrowState> {
  final repo.EscrowRepository _repository;

  /// Exposed for debug mock seeding (matches DeliveryBloc pattern).
  repo.EscrowRepository get repository => _repository;

  EscrowBloc({required repo.EscrowRepository repository})
      : _repository = repository,
        super(const EscrowInitial()) {
    on<InitiatePayment>(_onInitiatePayment);
    on<LoadEscrowDetail>(_onLoadEscrowDetail);
    on<LoadEscrowByDelivery>(_onLoadEscrowByDelivery);
    on<LoadPaymentHistory>(_onLoadPaymentHistory);
    on<RefreshEscrow>(_onRefreshEscrow);
    on<RefreshPaymentHistory>(_onRefreshPaymentHistory);
    on<RaisePaymentDispute>(_onRaiseDispute);
    on<CancelPaymentIntent>(_onCancelIntent);
    on<FilterPaymentHistory>(_onFilterHistory);
    on<LoadPendingIntents>(_onLoadPendingIntents);
  }

  Future<void> _onInitiatePayment(
    InitiatePayment event,
    Emitter<EscrowState> emit,
  ) async {
    emit(const EscrowLoading());
    try {
      final result = await _repository.initiatePayment(
        deliveryId: event.deliveryId,
        buyerId: event.buyerId,
        farmerId: event.farmerId,
        amount: event.amount,
      );
      if (result is repo.EscrowSuccess) {
        emit(PaymentInitiated(payment: result.payment));
      } else if (result is repo.EscrowQueued) {
        emit(PaymentQueued(intent: result.intent));
      } else if (result is repo.EscrowError) {
        emit(EscrowError(code: result.code, message: result.message));
      }
    } on Exception catch (e) {
      emit(EscrowError(code: 'ESCROW_CLIENT', message: e.toString()));
    }
  }

  Future<void> _onLoadEscrowDetail(
    LoadEscrowDetail event,
    Emitter<EscrowState> emit,
  ) async {
    emit(const EscrowLoading());
    try {
      final payment = await _repository.getEscrow(event.escrowId);
      if (payment != null) {
        emit(EscrowDetailLoaded(payment: payment));
      } else {
        emit(
          const EscrowError(
            code: 'ESCROW_016',
            message: 'Payment not found.',
          ),
        );
      }
    } on Exception catch (e) {
      emit(EscrowError(code: 'ESCROW_CLIENT', message: e.toString()));
    }
  }

  Future<void> _onLoadEscrowByDelivery(
    LoadEscrowByDelivery event,
    Emitter<EscrowState> emit,
  ) async {
    emit(const EscrowLoading());
    try {
      final payment = await _repository.getEscrowByDelivery(event.deliveryId);
      if (payment != null) {
        emit(EscrowDetailLoaded(payment: payment));
      } else {
        emit(
          const EscrowError(
            code: 'ESCROW_016',
            message: 'Payment not found for this delivery.',
          ),
        );
      }
    } on Exception catch (e) {
      emit(EscrowError(code: 'ESCROW_CLIENT', message: e.toString()));
    }
  }

  Future<void> _onLoadPaymentHistory(
    LoadPaymentHistory event,
    Emitter<EscrowState> emit,
  ) async {
    emit(const EscrowLoading());
    try {
      final payments = await _repository.getHistory(
        userId: event.userId,
        statusFilter: event.statusFilter,
        roleFilter: event.roleFilter,
      );
      final pendingIntents = await _repository.getPendingIntents();
      emit(
        PaymentHistoryLoaded(
          payments: payments,
          pendingIntents: pendingIntents,
          statusFilter: event.statusFilter,
          roleFilter: event.roleFilter,
        ),
      );
    } on Exception catch (e) {
      emit(EscrowError(code: 'ESCROW_CLIENT', message: e.toString()));
    }
  }

  Future<void> _onRefreshEscrow(
    RefreshEscrow event,
    Emitter<EscrowState> emit,
  ) async {
    // Silent refresh — no loading state
    try {
      final payment = await _repository.refreshEscrow(event.escrowId);
      if (payment != null) {
        emit(EscrowDetailLoaded(payment: payment));
      }
    } on Exception catch (e) {
      emit(EscrowError(code: 'ESCROW_CLIENT', message: e.toString()));
    }
  }

  Future<void> _onRefreshPaymentHistory(
    RefreshPaymentHistory event,
    Emitter<EscrowState> emit,
  ) async {
    // Silent refresh — no loading state
    try {
      final payments = await _repository.refreshHistory(
        userId: event.userId,
        statusFilter: event.statusFilter,
        roleFilter: event.roleFilter,
      );
      final pendingIntents = await _repository.getPendingIntents();
      emit(
        PaymentHistoryLoaded(
          payments: payments,
          pendingIntents: pendingIntents,
          statusFilter: event.statusFilter,
          roleFilter: event.roleFilter,
        ),
      );
    } on Exception catch (e) {
      emit(EscrowError(code: 'ESCROW_CLIENT', message: e.toString()));
    }
  }

  Future<void> _onRaiseDispute(
    RaisePaymentDispute event,
    Emitter<EscrowState> emit,
  ) async {
    emit(const EscrowLoading());
    try {
      final result = await _repository.raiseDispute(
        escrowId: event.escrowId,
        disputedBy: event.disputedBy,
        reason: event.reason,
        description: event.description,
        evidenceRefs: event.evidenceRefs,
      );
      if (result is repo.EscrowSuccess) {
        emit(DisputeRaisedSuccess(payment: result.payment));
      } else if (result is repo.EscrowError) {
        emit(EscrowError(code: result.code, message: result.message));
      }
    } on Exception catch (e) {
      emit(EscrowError(code: 'ESCROW_CLIENT', message: e.toString()));
    }
  }

  Future<void> _onCancelIntent(
    CancelPaymentIntent event,
    Emitter<EscrowState> emit,
  ) async {
    try {
      await _repository.cancelIntent(event.idempotencyKey);
      emit(const IntentCancelled());
    } on Exception catch (e) {
      emit(EscrowError(code: 'ESCROW_CLIENT', message: e.toString()));
    }
  }

  Future<void> _onFilterHistory(
    FilterPaymentHistory event,
    Emitter<EscrowState> emit,
  ) async {
    emit(const EscrowLoading());
    try {
      final payments = await _repository.getHistory(
        userId: event.userId,
        statusFilter: event.statusFilter,
        roleFilter: event.roleFilter,
      );
      final pendingIntents = await _repository.getPendingIntents();
      emit(
        PaymentHistoryLoaded(
          payments: payments,
          pendingIntents: pendingIntents,
          statusFilter: event.statusFilter,
          roleFilter: event.roleFilter,
        ),
      );
    } on Exception catch (e) {
      emit(EscrowError(code: 'ESCROW_CLIENT', message: e.toString()));
    }
  }

  Future<void> _onLoadPendingIntents(
    LoadPendingIntents event,
    Emitter<EscrowState> emit,
  ) async {
    try {
      final pendingIntents = await _repository.getPendingIntents();
      // If currently showing history, update the pending intents section
      final currentState = state;
      if (currentState is PaymentHistoryLoaded) {
        emit(
          PaymentHistoryLoaded(
            payments: currentState.payments,
            pendingIntents: pendingIntents,
            statusFilter: currentState.statusFilter,
            roleFilter: currentState.roleFilter,
          ),
        );
      }
    } on Exception catch (e) {
      emit(EscrowError(code: 'ESCROW_CLIENT', message: e.toString()));
    }
  }
}
