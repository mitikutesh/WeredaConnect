import 'package:wereda_connect/core/config/environment.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';

// Stub on mobile/VM; the real js-interop seam only on web. Keeps dart:js_interop
// out of the mobile build while this facade stays VM-analyzable/testable.
import 'package:wereda_connect/core/auth/web_test_auth_seam_stub.dart'
    if (dart.library.html) 'package:wereda_connect/core/auth/web_test_auth_seam_web.dart'
    as impl;

/// Build-time environment, as a compile-time constant so the gate below folds
/// (and is tree-shaken) per build.
const String _kEnv =
    String.fromEnvironment('ENVIRONMENT', defaultValue: 'development');

/// Default-DENY allowlist gate (F-37). The test auth seam is enabled ONLY for
/// known non-production environments. Any other value — including `prod` (the
/// literal CI passes, which `Environment.current` silently misclassifies as
/// `development`), `production`, empty, or a typo — yields false, so the seam can
/// never be compiled into / active in a production build. This is deliberately
/// an allowlist, not `!isProduction`, to fail closed against that env-name trap.
const bool kWebTestAuthSeamEnabled =
    _kEnv == 'dev' || _kEnv == 'development' || _kEnv == 'staging';

/// Pure form of the allowlist gate — for unit-testing the policy on the VM.
bool seamEnabledForEnv(String env) =>
    env == 'dev' || env == 'development' || env == 'staging';

/// Registers a production-gated, web-only test auth seam (`window.__seedAuth`)
/// used by the Playwright webapp e2e suite to authenticate deterministically.
///
/// No-op on mobile and in any production build:
/// - primary gate: compile-time const allowlist ([kWebTestAuthSeamEnabled]);
/// - secondary gate: runtime [Environment.config.isProduction] (belt-and-braces).
void registerWebTestAuthSeam(AuthBloc authBloc) {
  if (!kWebTestAuthSeamEnabled || Environment.config.isProduction) {
    return;
  }
  impl.registerWebTestAuthSeamImpl(authBloc);
}
