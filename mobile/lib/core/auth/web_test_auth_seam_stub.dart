import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';

/// No-op implementation for non-web builds (mobile / Dart VM). The real seam
/// lives in `web_test_auth_seam_web.dart` and is only compiled for the web
/// target via the conditional import in `web_test_auth_seam.dart`. F-37.
void registerWebTestAuthSeamImpl(AuthBloc authBloc) {}
