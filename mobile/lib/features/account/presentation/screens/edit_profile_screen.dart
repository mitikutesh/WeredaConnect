import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:wereda_connect/core/auth/offline_auth_cache.dart'
    if (dart.library.html) 'package:wereda_connect/core/auth/offline_auth_cache_web.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/account/domain/profile_edit.dart';
import 'package:wereda_connect/features/auth/bloc/auth_bloc.dart';
import 'package:wereda_connect/features/auth/bloc/auth_event.dart';
import 'package:wereda_connect/features/auth/bloc/auth_state.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/widgets/fieldway.dart';
import 'package:wereda_connect/shared/widgets/join_chrome.dart';
import 'package:wereda_connect/shared/widgets/labeled_field.dart';
import 'package:wereda_connect/shared/widgets/place_selector.dart';

/// Writes [values] to the device's copy of [userId]'s profile.
typedef SaveLocalProfile = Future<void> Function(
  String userId,
  String phoneNumber,
  ProfileValues values,
);

/// Profile > "Edit profile" (SIM-56, user-entity.md 2.2.0), the same for
/// Farmer, Trucker and Buyer on mobile and web: the name and woreda, and a
/// company buyer's company name. Role, phone and verification are not here.
///
/// The form starts from the device's copy and, when online, takes the
/// server's newer one (`GET /user/profile`) unless an edit still waits on the
/// phone. Saving checks the server's rules first (FUP-52: no phone number),
/// then the phone queues the change (it works offline) and web sends it.
class EditProfileScreen extends StatefulWidget {
  /// Reads the device's copy; defaults to the offline cache.
  final Future<ProfileValues?> Function(AuthAuthenticated auth)? loadCurrent;

  /// Reads and sends the profile; defaults to the app's provider.
  final BaseProfileRepository? repository;

  /// Writes the device's copy; defaults to the offline cache.
  final SaveLocalProfile? saveLocal;

  const EditProfileScreen({
    this.loadCurrent,
    this.repository,
    this.saveLocal,
    super.key,
  });

  @override
  State<EditProfileScreen> createState() => _EditProfileScreenState();
}

class _EditProfileScreenState extends State<EditProfileScreen> {
  final _formKey = GlobalKey<FormState>();
  final _name = TextEditingController();
  final _woreda = TextEditingController();
  final _company = TextEditingController();

  AuthAuthenticated? _auth;
  BaseProfileRepository? _repository;
  ProfileValues? _current;
  bool _edited = false;
  bool _isSubmitting = false;

  /// Set once a save is sent: a server copy read before it is older than
  /// the save, so it is never applied after that.
  bool _saveStarted = false;

  /// Fields the server refused for a phone number (web), by wire name.
  Set<String> _refusedFields = const {};

  @override
  void initState() {
    super.initState();
    final auth = context.read<AuthBloc>().state;
    if (auth is AuthAuthenticated) {
      _auth = auth;
      _repository = widget.repository ?? _appRepository();
      _load(auth);
    }
  }

  @override
  void dispose() {
    _name.dispose();
    _woreda.dispose();
    _company.dispose();
    super.dispose();
  }

  BaseProfileRepository? _appRepository() {
    try {
      return context.read<BaseProfileRepository>();
    } on Object {
      return null;
    }
  }

  static Future<ProfileValues?> _cachedValues(AuthAuthenticated auth) async {
    final phone = auth.phoneNumber;
    if (phone == null || phone.isEmpty) return null;
    try {
      final user = await OfflineAuthCache().getCachedUser(phone);
      if (user == null || user.userId != auth.userId) return null;
      return ProfileValues(
        displayName: user.effectiveDisplayName,
        woreda: user.woreda,
        role: user.role.name,
        buyerType: user.buyerType,
        companyName: user.companyName,
      );
    } on Object {
      return null;
    }
  }

  static Future<void> _saveCached(
    String userId,
    String phoneNumber,
    ProfileValues values,
  ) =>
      OfflineAuthCache().updateProfile(
        userId: userId,
        phoneNumber: phoneNumber,
        displayName: values.displayName,
        woreda: values.woreda,
        buyerType: values.buyerType,
        companyName: values.companyName,
      );

  Future<void> _writeLocal(AuthAuthenticated auth, ProfileValues values) async {
    try {
      await (widget.saveLocal ?? _saveCached)(
        auth.userId,
        auth.phoneNumber ?? '',
        values,
      );
    } on Object catch (e) {
      // The server or the queue has it; the next sign-in rewrites the copy.
      debugPrint('[EditProfile] local profile not updated: $e');
    }
  }

  void _fill(ProfileValues values) {
    _name.text = values.displayName;
    _woreda.text = values.woreda;
    _company.text = values.companyName ?? '';
  }

  Future<void> _load(AuthAuthenticated auth) async {
    final cached = await (widget.loadCurrent ?? _cachedValues)(auth);
    if (!mounted) return;
    final current = cached ??
        ProfileValues(
          displayName: auth.userName,
          woreda: auth.woreda ?? '',
          role: auth.role.name,
        );
    setState(() {
      _current = current;
      _fill(current);
    });

    // The server's copy is newer than the device's unless an edit waits here
    // or a save started while it was read (checked after every wait).
    final repository = _repository;
    if (repository == null) return;
    final server = await repository.fetch(auth.userId);
    if (!mounted || _saveStarted || server == null || server == _current) {
      return;
    }
    if (await repository.hasUnsentEdit(auth.userId)) return;
    if (!mounted || _saveStarted) return;
    await _writeLocal(auth, server);
    if (!mounted || _saveStarted) return;
    context.read<AuthBloc>().add(
          AuthProfileChanged(
            userId: auth.userId,
            userName: server.displayName,
            woreda: server.woreda,
          ),
        );
    setState(() {
      _current = server;
      if (!_edited && !_isSubmitting) _fill(server);
    });
  }

  void _onEdited() {
    if (_edited && _refusedFields.isEmpty) return;
    setState(() {
      _edited = true;
      _refusedFields = const {};
    });
  }

  String? _fieldError(
    String? value,
    AppLocalizations l10n, {
    required String field,
    required String? current,
    bool isName = false,
    String? requiredText,
  }) {
    if (_refusedFields.contains(field)) return l10n.errorContactInText;
    // Like the server: an unchanged value is not sent, so not checked (a
    // value stored before a rule, or a row without a woreda, still saves
    // a change to another field).
    if ((value ?? '').trim() == (current ?? '').trim()) return null;
    return switch (profileFieldError(value, isName: isName)) {
      null => null,
      ProfileFieldError.required => requiredText ?? l10n.fieldRequired,
      ProfileFieldError.tooLong => l10n.profileEditTooLong,
      ProfileFieldError.contactInText => l10n.errorContactInText,
      ProfileFieldError.nameUnavailable => l10n.profileEditNameUnavailable,
    };
  }

  Future<void> _save() async {
    final auth = _auth;
    final current = _current;
    if (auth == null || current == null || _isSubmitting) return;
    if (_refusedFields.isNotEmpty) setState(() => _refusedFields = const {});
    if (!(_formKey.currentState?.validate() ?? false)) return;

    final l10n = AppLocalizations.of(context)!;
    final messenger = ScaffoldMessenger.of(context);
    final navigator = Navigator.of(context);
    final changes = ProfileChanges.between(
      current: current,
      displayName: _name.text,
      woreda: _woreda.text,
      companyName: current.isCompanyBuyer ? _company.text : null,
    );
    if (changes.isEmpty) {
      navigator.pop(false);
      return;
    }

    final repository = _repository;
    if (repository == null) {
      messenger.showSnackBar(SnackBar(content: Text(l10n.profileEditFailed)));
      return;
    }

    _saveStarted = true;
    setState(() => _isSubmitting = true);
    ProfileSaveOutcome outcome;
    try {
      outcome = await repository.save(userId: auth.userId, changes: changes);
    } on SyncRejectedException catch (e) {
      if (!mounted) return;
      final fields = contactInTextFields(e.message).toSet();
      setState(() {
        _isSubmitting = false;
        _refusedFields = fields;
      });
      if (fields.isNotEmpty) {
        _formKey.currentState?.validate();
      } else {
        // The server answered: not a connection problem.
        messenger
            .showSnackBar(SnackBar(content: Text(l10n.profileEditRefused)));
      }
      return;
    } on Object catch (e) {
      debugPrint('[EditProfile] save failed: $e');
      if (!mounted) return;
      setState(() => _isSubmitting = false);
      messenger.showSnackBar(SnackBar(content: Text(l10n.profileEditFailed)));
      return;
    }

    final saved = current.apply(changes);
    await _writeLocal(auth, saved);
    if (!mounted) return;
    context.read<AuthBloc>().add(
          AuthProfileChanged(
            userId: auth.userId,
            userName: saved.displayName,
            woreda: saved.woreda,
          ),
        );
    messenger.showSnackBar(
      SnackBar(
        content: Text(
          outcome == ProfileSaveOutcome.queued
              ? l10n.profileEditSavedQueued
              : l10n.profileEditSaved,
        ),
      ),
    );
    navigator.pop(true);
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final current = _current;

    return Scaffold(
      body: SafeArea(
        child: FwContentWidth(
          maxWidth: 600,
          child: SingleChildScrollView(
            padding: const EdgeInsets.fromLTRB(20, 16, 20, 28),
            child: Form(
              key: _formKey,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  const Align(
                    alignment: AlignmentDirectional.centerStart,
                    child: JoinBackButton(),
                  ),
                  const SizedBox(height: 12),
                  FwPageHeader(
                    padding: EdgeInsets.zero,
                    eyebrow: l10n.profile,
                    title: l10n.profileEditTitle,
                  ),
                  const SizedBox(height: 16),
                  JoinNote.info(text: l10n.profileEditNote),
                  const SizedBox(height: 20),
                  if (current == null)
                    const Center(child: CircularProgressIndicator())
                  else ...[
                    LabeledField(
                      label: l10n.displayName,
                      field: TextFormField(
                        key: const Key('profile_edit_name'),
                        controller: _name,
                        enabled: !_isSubmitting,
                        decoration: labeledFieldDecoration().copyWith(
                          helperText: l10n.displayNameHelper,
                          counterText: '',
                        ),
                        keyboardType: TextInputType.name,
                        textCapitalization: TextCapitalization.words,
                        maxLength: profileFieldMaxLength,
                        validator: (value) => _fieldError(
                          value,
                          l10n,
                          field: 'displayName',
                          current: current.displayName,
                          isName: true,
                        ),
                        onChanged: (_) => _onEdited(),
                      ),
                    ),
                    const SizedBox(height: 14),
                    LabeledField(
                      // The suggestions stay their own nodes (see LabeledField).
                      mergeSemantics: false,
                      label: l10n.wereda,
                      field: PlaceSelector(
                        key: const Key('profile_edit_woreda'),
                        controller: _woreda,
                        enabled: !_isSubmitting,
                        labelAbove: true,
                        showSearchIcon: false,
                        maxLength: profileFieldMaxLength,
                        validator: (value) => _fieldError(
                          value,
                          l10n,
                          field: 'woreda',
                          current: current.woreda,
                        ),
                        onChanged: (_) => _onEdited(),
                        onPlaceSelected: (_) => _onEdited(),
                      ),
                    ),
                    if (current.isCompanyBuyer) ...[
                      const SizedBox(height: 14),
                      LabeledField(
                        label: l10n.companyName,
                        field: TextFormField(
                          key: const Key('profile_edit_company'),
                          controller: _company,
                          enabled: !_isSubmitting,
                          decoration: labeledFieldDecoration()
                              .copyWith(counterText: ''),
                          textCapitalization: TextCapitalization.words,
                          maxLength: profileFieldMaxLength,
                          validator: (value) => _fieldError(
                            value,
                            l10n,
                            field: 'companyName',
                            current: current.companyName,
                            requiredText: l10n.companyNameRequired,
                          ),
                          onChanged: (_) => _onEdited(),
                        ),
                      ),
                    ],
                    const SizedBox(height: 24),
                    FilledButton(
                      key: const Key('profile_edit_save'),
                      onPressed: _isSubmitting ? null : _save,
                      child: _isSubmitting
                          ? const SizedBox(
                              width: 20,
                              height: 20,
                              child: CircularProgressIndicator(strokeWidth: 2),
                            )
                          : Text(l10n.save),
                    ),
                  ],
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}
