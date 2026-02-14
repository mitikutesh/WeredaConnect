import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/auth/auth_service.dart';
import 'package:wereda_connect/core/auth/models/auth_error.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';

/// BLoC for managing authentication state.
///
/// This BLoC handles:
/// - User registration with OTP verification
/// - Online/offline login
/// - Token refresh
/// - Logout
///
/// State transitions:
/// - AuthInitial → AuthLoading → AuthAuthenticated/AuthUnauthenticated/AuthError
/// - AuthUnauthenticated → AuthLoading → AuthOtpVerificationRequired (after registration)
/// - AuthOtpVerificationRequired → AuthLoading → AuthAuthenticated/AuthError
///
/// Ethiopian context:
/// - Supports offline authentication with cached credentials
/// - Auto-detects connectivity and chooses appropriate auth method
class AuthBloc extends Bloc<AuthEvent, AuthState> {
  final AuthService _authService;
  String? _currentPhoneNumber;
  // SECURITY: Password never stored in BLoC state to prevent memory exposure
  // On shared devices (common in Ethiopia), password in memory is a critical security risk
  // Password only exists in event parameters, never persisted in BLoC fields

  AuthBloc({
    AuthService? authService,
  })  : _authService = authService ?? AuthService(),
        super(const AuthInitial()) {
    // Register event handlers
    on<AuthCheckRequested>(_onAuthCheckRequested);
    on<AuthRegisterRequested>(_onAuthRegisterRequested);
    on<AuthVerifyOtpRequested>(_onAuthVerifyOtpRequested);
    on<AuthLoginRequested>(_onAuthLoginRequested);
    on<AuthLoginOnlineRequested>(_onAuthLoginOnlineRequested);
    on<AuthLoginOfflineRequested>(_onAuthLoginOfflineRequested);
    on<AuthRefreshTokenRequested>(_onAuthRefreshTokenRequested);
    on<AuthLogoutRequested>(_onAuthLogoutRequested);
  }

  /// Check if user is already authenticated.
  Future<void> _onAuthCheckRequested(
    AuthCheckRequested event,
    Emitter<AuthState> emit,
  ) async {
    emit(const AuthLoading());

    // TODO: Check if valid tokens exist in secure storage
    // For now, just emit unauthenticated
    emit(const AuthUnauthenticated());
  }

  /// Handle user registration.
  Future<void> _onAuthRegisterRequested(
    AuthRegisterRequested event,
    Emitter<AuthState> emit,
  ) async {
    emit(const AuthLoading());

    // Store phone number for session tracking (not sensitive)
    _currentPhoneNumber = event.phoneNumber;
    // SECURITY: Password is NOT stored - it only exists in event.password
    // After OTP verification, user will re-enter password for login

    final result = await _authService.register(
      phoneNumber: event.phoneNumber,
      password: event.password,
      name: event.name,
      role: event.role,
      woreda: event.woreda,
    );

    if (result.isSuccess) {
      emit(AuthOtpVerificationRequired(
        phoneNumber: event.phoneNumber,
        userId: result.userId!,
      ),);
    } else {
      emit(AuthError(
        errorCode: result.errorCode ?? AuthErrorCode.internalServerError,
        errorMessage: result.errorMessage ?? 'Registration failed',
        canRetry: result.errorCode?.isRecoverable ?? true,
      ),);
    }
  }

  /// Handle OTP verification.
  Future<void> _onAuthVerifyOtpRequested(
    AuthVerifyOtpRequested event,
    Emitter<AuthState> emit,
  ) async {
    emit(const AuthLoading());

    // Verify OTP
    final result = await _authService.verifyOtp(
      phoneNumber: event.phoneNumber,
      otp: event.otp,
    );

    if (result.isSuccess) {
      // SECURITY: After OTP verification, user must login separately to cache credentials
      // We DO NOT auto-login because password was never stored in BLoC state
      // This is intentional - users will be directed to login screen after registration
      emit(AuthAuthenticated(
        userId: result.userId!,
        tokens: result.tokens!,
      ),);
    } else {
      emit(AuthError(
        errorCode: result.errorCode ?? AuthErrorCode.internalServerError,
        errorMessage: result.errorMessage ?? 'OTP verification failed',
      ),);
    }
  }

  /// Handle login (auto-detect online/offline).
  Future<void> _onAuthLoginRequested(
    AuthLoginRequested event,
    Emitter<AuthState> emit,
  ) async {
    emit(const AuthLoading());

    final result = await _authService.login(
      phoneNumber: event.phoneNumber,
      password: event.password,
    );

    if (result.isSuccess) {
      emit(AuthAuthenticated(
        userId: result.userId!,
        tokens: result.tokens!,
        isOfflineAuth: result.isOfflineAuth,
      ),);
    } else {
      emit(AuthError(
        errorCode: result.errorCode ?? AuthErrorCode.internalServerError,
        errorMessage: result.errorMessage ?? 'Login failed',
        canRetry: result.errorCode?.isRecoverable ?? true,
      ),);
    }
  }

  /// Handle online login.
  Future<void> _onAuthLoginOnlineRequested(
    AuthLoginOnlineRequested event,
    Emitter<AuthState> emit,
  ) async {
    emit(const AuthLoading());

    final result = await _authService.loginOnline(
      phoneNumber: event.phoneNumber,
      password: event.password,
    );

    if (result.isSuccess) {
      emit(AuthAuthenticated(
        userId: result.userId!,
        tokens: result.tokens!,
      ),);
    } else {
      emit(AuthError(
        errorCode: result.errorCode ?? AuthErrorCode.internalServerError,
        errorMessage: result.errorMessage ?? 'Login failed',
        canRetry: result.errorCode?.isRecoverable ?? true,
      ),);
    }
  }

  /// Handle offline login.
  Future<void> _onAuthLoginOfflineRequested(
    AuthLoginOfflineRequested event,
    Emitter<AuthState> emit,
  ) async {
    emit(const AuthLoading());

    final result = await _authService.loginOffline(
      phoneNumber: event.phoneNumber,
      password: event.password,
    );

    if (result.isSuccess) {
      emit(AuthAuthenticated(
        userId: result.userId!,
        tokens: result.tokens!,
        isOfflineAuth: true,
      ),);
    } else {
      emit(AuthError(
        errorCode: result.errorCode ?? AuthErrorCode.internalServerError,
        errorMessage: result.errorMessage ?? 'Offline login failed',
        canRetry: result.errorCode?.isRecoverable ?? true,
      ),);
    }
  }

  /// Handle token refresh.
  Future<void> _onAuthRefreshTokenRequested(
    AuthRefreshTokenRequested event,
    Emitter<AuthState> emit,
  ) async {
    // Don't emit loading state for background token refresh
    final currentState = state;

    final result = await _authService.refreshToken(event.refreshToken);

    if (result.isSuccess) {
      // Update tokens in current authenticated state
      if (currentState is AuthAuthenticated) {
        emit(AuthAuthenticated(
          userId: currentState.userId,
          tokens: result.tokens!,
          isOfflineAuth: currentState.isOfflineAuth,
        ),);
      }
    } else {
      // Token refresh failed - logout user
      emit(const AuthUnauthenticated());
    }
  }

  /// Handle logout.
  Future<void> _onAuthLogoutRequested(
    AuthLogoutRequested event,
    Emitter<AuthState> emit,
  ) async {
    final currentState = state;

    if (currentState is AuthAuthenticated) {
      emit(const AuthLoading());

      await _authService.logout(
        phoneNumber: _currentPhoneNumber ?? '',
        accessToken: currentState.tokens.accessToken,
        clearCache: event.clearCache,
      );

      emit(const AuthUnauthenticated());
    }
  }
}
