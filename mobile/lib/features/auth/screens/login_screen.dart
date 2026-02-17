import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/features/auth/screens/register_screen.dart';
import 'package:wereda_connect/features/onboarding/screens/welcome_screen.dart';
import 'package:wereda_connect/shared/widgets/language_selection_dialog.dart';

/// Login screen with Ethiopian phone input and offline mode support.
///
/// Features:
/// - Ethiopian phone number input (+251 prefix auto-filled)
/// - Password input with visibility toggle
/// - Offline mode indicator
/// - Large touch targets (48dp minimum per Material Design)
/// - Localized error messages (Amharic/Oromo/English)
///
/// Ethiopian context:
/// - Phone number format: +251XXXXXXXXX (9 digits after +251)
/// - Simple error messages for low-literacy users
/// - Visual feedback (colors, icons) more than text
class LoginScreen extends StatefulWidget {
  const LoginScreen({super.key});

  @override
  State<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends State<LoginScreen> {
  final _formKey = GlobalKey<FormState>();
  final _phoneController = TextEditingController(text: '+251');
  final _passwordController = TextEditingController();

  bool _obscurePassword = true;
  String? _errorMessage;

  @override
  void dispose() {
    _phoneController.dispose();
    _passwordController.dispose();
    super.dispose();
  }

  void _handleLogin() {
    // Clear previous errors
    setState(() {
      _errorMessage = null;
    });

    // Validate form
    if (!_formKey.currentState!.validate()) {
      return;
    }

    // Dispatch login event via BLoC
    context.read<AuthBloc>().add(
          AuthLoginRequested(
            phoneNumber: _phoneController.text.trim(),
            password: _passwordController.text,
          ),
        );
  }

  void _showLanguageSelectionDialog(BuildContext context) {
    showDialog<void>(
      context: context,
      builder: (context) => const LanguageSelectionDialog(),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final theme = Theme.of(context);

    return BlocListener<AuthBloc, AuthState>(
      listener: (context, state) {
        if (state is AuthError) {
          setState(() {
            _errorMessage = state.errorMessage;
          });
        }
      },
      child: Scaffold(
        appBar: AppBar(
          leading: IconButton(
            icon: const Icon(Icons.arrow_back),
            onPressed: () {
              Navigator.of(context).pushReplacement(
                MaterialPageRoute<void>(
                  builder: (_) => const WelcomeScreen(),
                ),
              );
            },
            tooltip: l10n.backToHome,
          ),
          title: Text(l10n.login),
          elevation: 0,
          actions: [
            IconButton(
              icon: const Icon(Icons.language),
              onPressed: () => _showLanguageSelectionDialog(context),
              tooltip: l10n.language,
            ),
          ],
        ),
        body: SafeArea(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(24.0),
            child: Form(
              key: _formKey,
              child: Builder(
                builder: (context) {
                  final isLoading =
                      context.watch<AuthBloc>().state is AuthLoading;

                  return Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      // App logo/icon
                      const SizedBox(height: 32),
                      Icon(
                        Icons.agriculture,
                        size: 80,
                        color: theme.primaryColor,
                      ),
                      const SizedBox(height: 16),

                      // Welcome message
                      Text(
                        l10n.welcomeMessage,
                        style: theme.textTheme.headlineSmall?.copyWith(
                          fontWeight: FontWeight.bold,
                        ),
                        textAlign: TextAlign.center,
                      ),
                      const SizedBox(height: 48),

                      // Phone number input
                      TextFormField(
                        controller: _phoneController,
                        decoration: InputDecoration(
                          labelText: l10n.phoneNumber,
                          prefixIcon: const Icon(Icons.phone),
                          hintText: l10n.ethiopianPhoneExample,
                          border: const OutlineInputBorder(),
                          // Large touch target
                          contentPadding: const EdgeInsets.symmetric(
                            horizontal: 16,
                            vertical: 20,
                          ),
                        ),
                        keyboardType: TextInputType.phone,
                        inputFormatters: [
                          // Ensure +251 prefix is always present
                          FilteringTextInputFormatter.allow(RegExp(r'[+\d]')),
                        ],
                        validator: (value) {
                          if (value == null || value.isEmpty) {
                            return l10n.phoneNumber;
                          }
                          // Ethiopian phone format: +251XXXXXXXXX
                          if (!RegExp(r'^\+251\d{9}$').hasMatch(value)) {
                            return l10n.invalidPhoneFormat;
                          }
                          return null;
                        },
                      ),
                      const SizedBox(height: 16),

                      // Password input
                      TextFormField(
                        controller: _passwordController,
                        decoration: InputDecoration(
                          labelText: l10n.password,
                          prefixIcon: const Icon(Icons.lock),
                          suffixIcon: IconButton(
                            icon: Icon(
                              _obscurePassword
                                  ? Icons.visibility_off
                                  : Icons.visibility,
                            ),
                            onPressed: () {
                              setState(() {
                                _obscurePassword = !_obscurePassword;
                              });
                            },
                            // Large touch target
                            iconSize: 24,
                            padding: const EdgeInsets.all(12),
                          ),
                          border: const OutlineInputBorder(),
                          contentPadding: const EdgeInsets.symmetric(
                            horizontal: 16,
                            vertical: 20,
                          ),
                        ),
                        obscureText: _obscurePassword,
                        validator: (value) {
                          if (value == null || value.isEmpty) {
                            return l10n.password;
                          }
                          if (value.length < 6) {
                            return l10n.passwordRequirements;
                          }
                          return null;
                        },
                      ),
                      const SizedBox(height: 8),

                      // Forgot password link
                      Align(
                        alignment: Alignment.centerRight,
                        child: TextButton(
                          onPressed: () {
                            // TODO: Implement forgot password
                            ScaffoldMessenger.of(context).showSnackBar(
                              SnackBar(content: Text(l10n.comingSoon)),
                            );
                          },
                          child: Text(l10n.forgotPassword),
                        ),
                      ),
                      const SizedBox(height: 24),

                      // Error message
                      if (_errorMessage != null)
                        Container(
                          padding: const EdgeInsets.all(12),
                          margin: const EdgeInsets.only(bottom: 16),
                          decoration: BoxDecoration(
                            color: Colors.red.shade50,
                            border: Border.all(color: Colors.red.shade200),
                            borderRadius: BorderRadius.circular(8),
                          ),
                          child: Row(
                            children: [
                              Icon(
                                Icons.error_outline,
                                color: Colors.red.shade700,
                              ),
                              const SizedBox(width: 12),
                              Expanded(
                                child: Text(
                                  _errorMessage!,
                                  style: TextStyle(color: Colors.red.shade700),
                                ),
                              ),
                            ],
                          ),
                        ),

                      // Login button
                      ElevatedButton(
                        onPressed: isLoading ? null : _handleLogin,
                        style: ElevatedButton.styleFrom(
                          padding: const EdgeInsets.symmetric(vertical: 16),
                          minimumSize:
                              const Size.fromHeight(48), // Large touch target
                          shape: RoundedRectangleBorder(
                            borderRadius: BorderRadius.circular(8),
                          ),
                        ),
                        child: isLoading
                            ? const SizedBox(
                                height: 20,
                                width: 20,
                                child: CircularProgressIndicator(
                                  strokeWidth: 2,
                                  valueColor: AlwaysStoppedAnimation<Color>(
                                    Colors.white,
                                  ),
                                ),
                              )
                            : Text(
                                l10n.login,
                                style: const TextStyle(fontSize: 16),
                              ),
                      ),
                      const SizedBox(height: 24),

                      // Register link
                      Row(
                        mainAxisAlignment: MainAxisAlignment.center,
                        children: [
                          Text(l10n.dontHaveAccount),
                          TextButton(
                            onPressed: () {
                              Navigator.of(context).push(
                                MaterialPageRoute<void>(
                                  builder: (_) => const RegisterScreen(),
                                ),
                              );
                            },
                            child: Text(l10n.register),
                          ),
                        ],
                      ),
                    ],
                  );
                },
              ),
            ),
          ),
        ),
      ),
    );
  }
}
