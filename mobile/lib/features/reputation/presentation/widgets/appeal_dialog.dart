import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:image_picker/image_picker.dart';

import 'package:wereda_connect/features/reputation/data/reputation_repository.dart';
import 'package:wereda_connect/features/reputation/domain/models/reputation_event.dart';
import 'package:wereda_connect/shared/l10n/app_localizations.dart';
import 'package:wereda_connect/shared/theme/app_theme.dart';

/// Picks the optional appeal photo and returns its bytes, or null when the
/// user picked none.
typedef AppealPhotoPicker = Future<Uint8List?> Function();

/// The app's picker: the camera on the phone (as for the delivery photos),
/// the file chooser on the web, scaled down so it fits the 200 KB cap.
Future<Uint8List?> pickAppealPhoto() async {
  final file = await ImagePicker().pickImage(
    source: kIsWeb ? ImageSource.gallery : ImageSource.camera,
    maxWidth: 1024,
    maxHeight: 1024,
    imageQuality: 55,
  );
  return file?.readAsBytes();
}

/// ESC-31: opens the appeal for the user's own [event]
/// (reputation-api.md section 6) and resolves with the appeal state the
/// server stored, or null when nothing was sent.
Future<ReputationAppeal?> showAppealDialog(
  BuildContext context, {
  required ReputationRepository repository,
  required String userId,
  required ReputationEvent event,
  AppealPhotoPicker? pickPhoto,
}) =>
    showDialog<ReputationAppeal>(
      context: context,
      builder: (_) => AppealDialog(
        repository: repository,
        userId: userId,
        event: event,
        pickPhoto: pickPhoto ?? pickAppealPhoto,
      ),
    );

/// The appeal form: what happened (required, up to 500 characters) and an
/// optional photo, sent once. A dialog, not a sheet: a mouse on the web
/// cannot drag a sheet.
///
/// Sending needs a connection (the contract defines no sync operation for
/// an appeal), and the form says so. The text is kept on the device as a
/// draft whenever the dialog closes without a sent appeal, so writing it
/// offline is not lost.
class AppealDialog extends StatefulWidget {
  final ReputationRepository repository;
  final String userId;
  final ReputationEvent event;
  final AppealPhotoPicker pickPhoto;

  const AppealDialog({
    required this.repository,
    required this.userId,
    required this.event,
    required this.pickPhoto,
    super.key,
  });

  @override
  State<AppealDialog> createState() => _AppealDialogState();
}

class _AppealDialogState extends State<AppealDialog> {
  final TextEditingController _text = TextEditingController();
  Uint8List? _photo;
  bool _sending = false;
  bool _sent = false;
  AppealFailure? _failure;

  @override
  void initState() {
    super.initState();
    unawaited(_restoreDraft());
  }

  Future<void> _restoreDraft() async {
    final draft = await widget.repository
        .readAppealDraft(widget.userId, widget.event.eventId);
    // Never over what the user already typed.
    if (!mounted || draft == null || _text.text.isNotEmpty) return;
    setState(() => _text.text = draft);
  }

  @override
  void dispose() {
    if (!_sent) {
      unawaited(
        widget.repository.saveAppealDraft(
          widget.userId,
          widget.event.eventId,
          _text.text,
        ),
      );
    }
    _text.dispose();
    super.dispose();
  }

  Future<void> _addPhoto() async {
    final Uint8List? bytes;
    try {
      bytes = await widget.pickPhoto();
    } on Object catch (e) {
      debugPrint('[AppealDialog] photo pick failed: $e');
      if (mounted) setState(() => _failure = AppealFailure.photo);
      return;
    }
    if (!mounted || bytes == null) return;
    final usable = bytes.isNotEmpty &&
        bytes.length <= ReputationRepository.maxPhotoBytes &&
        ReputationRepository.photoContentTypeOf(bytes) != null;
    setState(() {
      _photo = usable ? bytes : null;
      _failure = usable ? null : AppealFailure.photo;
    });
  }

  Future<void> _send() async {
    if (_sending || _text.text.trim().isEmpty) return;
    setState(() {
      _sending = true;
      _failure = null;
    });
    try {
      final appeal = await widget.repository.submitAppeal(
        userId: widget.userId,
        eventId: widget.event.eventId,
        text: _text.text,
        photo: _photo,
      );
      _sent = true;
      if (mounted) Navigator.of(context).pop(appeal);
    } on AppealException catch (e) {
      if (!mounted) return;
      setState(() {
        _sending = false;
        _failure = e.failure;
        // A refused photo is dropped: the appeal can go without it.
        if (e.failure == AppealFailure.photo) _photo = null;
      });
    }
  }

  String _failureText(AppLocalizations l10n, AppealFailure failure) =>
      switch (failure) {
        AppealFailure.closed => l10n.reputationAppealClosed,
        AppealFailure.exists => l10n.reputationAppealExists,
        AppealFailure.unavailable => l10n.reputationAppealUnavailable,
        AppealFailure.photo => l10n.reputationAppealPhotoProblem,
        AppealFailure.notSent => l10n.reputationAppealFailed,
      };

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context)!;
    final failure = _failure;
    const muted = TextStyle(fontSize: 13, color: AppTheme.mutedSteel);
    return AlertDialog(
      key: const Key('reputation_appeal_dialog'),
      title: Text(l10n.reputationAppeal),
      scrollable: true,
      content: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 440),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(l10n.reputationAppealHint, style: muted),
            const SizedBox(height: 12),
            TextField(
              key: const Key('reputation_appeal_text'),
              controller: _text,
              enabled: !_sending,
              minLines: 3,
              maxLines: 6,
              maxLength: ReputationRepository.maxAppealLength,
              textCapitalization: TextCapitalization.sentences,
              decoration: InputDecoration(
                labelText: l10n.reputationAppealTextLabel,
                alignLabelWithHint: true,
              ),
              onChanged: (_) => setState(() {}),
            ),
            const SizedBox(height: 4),
            if (_photo == null)
              Align(
                alignment: AlignmentDirectional.centerStart,
                child: TextButton.icon(
                  key: const Key('reputation_appeal_add_photo'),
                  onPressed: _sending ? null : _addPhoto,
                  icon: const Icon(Icons.photo_camera_outlined, size: 20),
                  label: Text(l10n.reputationAppealAddPhoto),
                ),
              )
            else
              Row(
                children: [
                  const Icon(
                    Icons.check_circle_outline,
                    size: 20,
                    color: AppTheme.fieldGreen,
                  ),
                  const SizedBox(width: 8),
                  Expanded(child: Text(l10n.reputationAppealPhotoAdded)),
                  TextButton(
                    key: const Key('reputation_appeal_remove_photo'),
                    onPressed:
                        _sending ? null : () => setState(() => _photo = null),
                    child: Text(l10n.reputationAppealRemovePhoto),
                  ),
                ],
              ),
            const SizedBox(height: 8),
            Text(l10n.reputationAppealNeedsConnection, style: muted),
            if (failure != null) ...[
              const SizedBox(height: 12),
              Text(
                _failureText(l10n, failure),
                key: const Key('reputation_appeal_error'),
                style: const TextStyle(
                  fontSize: 13,
                  fontWeight: FontWeight.w600,
                  color: AppTheme.statusError,
                ),
              ),
            ],
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: _sending ? null : () => Navigator.of(context).pop(),
          child: Text(l10n.cancel),
        ),
        FilledButton(
          key: const Key('reputation_appeal_send'),
          onPressed: _sending || _text.text.trim().isEmpty ? null : _send,
          child: _sending
              ? const SizedBox(
                  width: 18,
                  height: 18,
                  child: CircularProgressIndicator(strokeWidth: 2),
                )
              : Text(l10n.reputationAppealSend),
        ),
      ],
    );
  }
}
