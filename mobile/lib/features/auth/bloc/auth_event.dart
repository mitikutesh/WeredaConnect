import 'package:equatable/equatable.dart';

import 'package:wereda_connect/core/auth/models/auth_tokens.dart';

import 'package:wereda_connect/core/models/user.dart';

/// Base class for authentication events.
abstract class AuthEvent extends Equatable {
  const AuthEvent();

  @override
  List<Object?> get props => [];
}

/// Event to check if user is already authenticated.
class AuthCheckRequested extends AuthEvent {
  const AuthCheckRequested();
}

/// Event to register a new user, optionally with Fayda verification.
///
/// Fayda verification is optional during registration (two-tier model):
/// - Without Fayda: user can trade up to 10,000 ETB
/// - With Fayda: user can trade unlimited amounts
class AuthRegisterRequested extends AuthEvent {
  final String phoneNumber;
  final String password;
  final String name;
  final UserRole role;
  final String woreda;
  final String preferredLanguage;
  final String? faydaVerificationToken;

  // Optional fields from multi-step registration flow
  final String? drivingLicensePath;
  final String? buyerType;
  final String? companyName;

  const AuthRegisterRequested({
    required this.phoneNumber,
    required this.password,
    required this.name,
    required this.role,
    required this.woreda,
    this.faydaVerificationToken,
    this.preferredLanguage = 'am',
    this.drivingLicensePath,
    this.buyerType,
    this.companyName,
  });

  @override
  List<Object?> get props => [
        phoneNumber,
        password,
        name,
        role,
        woreda,
        preferredLanguage,
        faydaVerificationToken,
        drivingLicensePath,
        buyerType,
        companyName,
      ];
}

/// Event to login (auto-detects online/offline).
class AuthLoginRequested extends AuthEvent {
  final String phoneNumber;
  final String password;

  const AuthLoginRequested({
    required this.phoneNumber,
    required this.password,
  });

  @override
  List<Object?> get props => [phoneNumber, password];
}

/// Event to login online (forces online authentication).
class AuthLoginOnlineRequested extends AuthEvent {
  final String phoneNumber;
  final String password;

  const AuthLoginOnlineRequested({
    required this.phoneNumber,
    required this.password,
  });

  @override
  List<Object?> get props => [phoneNumber, password];
}

/// Event to login offline (uses cached credentials).
class AuthLoginOfflineRequested extends AuthEvent {
  final String phoneNumber;
  final String password;

  const AuthLoginOfflineRequested({
    required this.phoneNumber,
    required this.password,
  });

  @override
  List<Object?> get props => [phoneNumber, password];
}

/// Event to refresh authentication tokens.
class AuthRefreshTokenRequested extends AuthEvent {
  final String refreshToken;

  const AuthRefreshTokenRequested({
    required this.refreshToken,
  });

  @override
  List<Object?> get props => [refreshToken];
}

/// Event to cancel an in-flight auth operation (e.g. user navigated back).
class AuthCancelRequested extends AuthEvent {
  const AuthCancelRequested();
}

/// The OTP screen was closed (back, or success) without the code needed any
/// longer. Drops the password and registration details held for the verify
/// call so they do not outlive the screen.
class AuthOtpAbandoned extends AuthEvent {
  const AuthOtpAbandoned();
}

/// Event to logout user.
class AuthLogoutRequested extends AuthEvent {
  final bool clearCache;

  const AuthLogoutRequested({
    this.clearCache = true,
  });

  @override
  List<Object?> get props => [clearCache];
}

/// The server confirmed [userId]'s account is deleted (account-deletion.md):
/// remove what this device holds for it. Sends nothing to the server. When
/// that user is signed in, the session ends too; otherwise (it signed out,
/// or another account signed in, before the answer came) only its local data
/// goes and the current session is left alone.
class AuthAccountDeleted extends AuthEvent {
  final String userId;

  /// The account's phone, which keys its offline credential cache.
  final String? phoneNumber;

  const AuthAccountDeleted({required this.userId, this.phoneNumber});

  @override
  List<Object?> get props => [userId, phoneNumber];
}

/// A request was refused with `410 ACCOUNT_004`: the signed-in account is
/// being or has been deleted. For any [deletionState] but `in_progress` the
/// signed-in account is wiped as for [AuthAccountDeleted].
class AuthAccountGone extends AuthEvent {
  final String deletionState;

  const AuthAccountGone({required this.deletionState});

  @override
  List<Object?> get props => [deletionState];
}

/// Event to request password reset (sends verification code).
class AuthForgotPasswordRequested extends AuthEvent {
  final String phoneNumber;

  const AuthForgotPasswordRequested({
    required this.phoneNumber,
  });

  @override
  List<Object?> get props => [phoneNumber];
}

/// Event to confirm password reset with verification code.
class AuthResetPasswordRequested extends AuthEvent {
  final String phoneNumber;
  final String confirmationCode;
  final String newPassword;

  const AuthResetPasswordRequested({
    required this.phoneNumber,
    required this.confirmationCode,
    required this.newPassword,
  });

  @override
  List<Object?> get props => [phoneNumber, confirmationCode, newPassword];
}

/// Confirm the registration SMS OTP. [phoneNumber] defaults to the phone of
/// the pending registration / unconfirmed login.
class AuthVerifyOtpRequested extends AuthEvent {
  final String otpCode;
  final String? phoneNumber;

  const AuthVerifyOtpRequested({required this.otpCode, this.phoneNumber});

  @override
  List<Object?> get props => [otpCode, phoneNumber];
}

/// Ask the server for a fresh registration OTP.
class AuthResendOtpRequested extends AuthEvent {
  final String? phoneNumber;

  const AuthResendOtpRequested({this.phoneNumber});

  @override
  List<Object?> get props => [phoneNumber];
}

/// A step-up re-authentication succeeded: swap in [tokens] without clearing
/// any local data. The login call already refreshed the offline cache.
class AuthStepUpSucceeded extends AuthEvent {
  final AuthTokens tokens;

  const AuthStepUpSucceeded({required this.tokens});

  @override
  List<Object?> get props => [tokens];
}

/// The server rejected the refresh token (400/401): sign the user out
/// locally. Unsynced work stays queued for their next login.
class AuthSessionExpired extends AuthEvent {
  const AuthSessionExpired();
}
