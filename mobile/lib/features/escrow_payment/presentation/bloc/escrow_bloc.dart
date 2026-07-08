import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/escrow_payment/data/repositories/escrow_result.dart'
    as repo;
import 'package:wereda_connect/features/escrow_payment/domain/repositories/base_escrow_repository.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_event.dart';
import 'package:wereda_connect/features/escrow_payment/presentation/bloc/escrow_state.dart';

/// BLoC for managing escrow payment state.
///
/// Handles payment initiation (online/offline), status viewing, history,
/// dispute raising, and intent cancellation. Wires UI events to
/// BaseEscrowRepository for data operations.
class EscrowBloc extends Bloc<EscrowEvent, EscrowState> {
  final BaseEscrowRepository _repository;

  /// Exposed for debug mock seeding (matches DeliveryBloc pattern).
  BaseEscrowRepository get repository => _repository;

  EscrowBloc({required BaseEscrowRepository repository})
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
        emit(
          PaymentInitiated(
            payment: result.payment,
            toPayUrl: result.toPayUrl,
          ),
        );
      } else if (result is repo.EscrowQueued) {
        emit(PaymentQueued(intent: result.intent));
      } else if (result is repo.EscrowError) {
        emit(EscrowError(code: result.code, message: result.message));
      }
    } on Object catch (e) {
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
        // null == the escrow does not exist (a genuine 404). Real request
        // failures arrive as ApiException below.
        emit(
          const EscrowError(
            code: 'ESCROW_016',
            message: 'Payment not found.',
          ),
        );
      }
    } on ApiException catch (e) {
      // A request failure (auth/forbidden/server/rate-limit/network) must
      // surface as a distinct, retryable error — never as "payment not found".
      emit(_mapReadException(e));
    } on Object catch (e) {
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
        // null == the server confirmed there is no escrow for this delivery
        // (a genuine 404). Real request failures arrive as ApiException below.
        emit(
          const EscrowError(
            code: 'ESCROW_016_DELIVERY',
            message: 'Payment not found for this delivery.',
          ),
        );
      }
    } on ApiException catch (e) {
      // A request failure (auth/forbidden/server/rate-limit/network) must
      // surface as a distinct, retryable error — never as "payment not found".
      emit(_mapReadException(e));
    } on Object catch (e) {
      emit(EscrowError(code: 'ESCROW_CLIENT', message: e.toString()));
    }
  }

  /// Map an [ApiException] raised by an escrow read into a distinct,
  /// localizable [EscrowError] state.
  ///
  /// A genuine "no escrow" result is a 404, which the repository converts to a
  /// null return value (never an exception). Everything reaching here is a real
  /// failure, so it must NOT be reported as "payment not found". Codes mirror
  /// [EscrowRepository] error mapping and are all understood by
  /// localizeEscrowError, so the UI shows a localized, actionable message.
  EscrowError _mapReadException(ApiException e) {
    return switch (e) {
      AuthException(:final statusCode) when statusCode == 403 =>
        const EscrowError(
          code: 'ESCROW_022',
          message: 'You are not authorized to view this payment.',
        ),
      AuthException() => const EscrowError(
          code: 'ESCROW_005',
          message: 'Please log in to continue.',
        ),
      NetworkException() || TimeoutException() => const EscrowError(
          code: 'ESCROW_OFFLINE',
          message: 'Internet connection required for this action.',
        ),
      RateLimitException() => const EscrowError(
          code: 'ESCROW_011',
          message: 'Too many requests. Please wait and try again.',
        ),
      ServerException(:final statusCode) when statusCode == 502 =>
        const EscrowError(
          code: 'ESCROW_013',
          message: 'Telebirr service error. Please try again.',
        ),
      ServerException(:final statusCode) when statusCode == 503 =>
        const EscrowError(
          code: 'ESCROW_014',
          message: 'Telebirr is currently unavailable. Please try later.',
        ),
      _ => const EscrowError(
          code: 'ESCROW_012',
          message: 'Something went wrong. Please try again.',
        ),
    };
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
    } on Object catch (e) {
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
    } on Object catch (e) {
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
    } on Object catch (e) {
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
    } on Object catch (e) {
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
    } on Object catch (e) {
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
    } on Object catch (e) {
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
    } on Object catch (e) {
      emit(EscrowError(code: 'ESCROW_CLIENT', message: e.toString()));
    }
  }
}
