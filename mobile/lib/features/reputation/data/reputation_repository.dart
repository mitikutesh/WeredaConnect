import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';

import 'package:wereda_connect/core/network/api_client.dart';
import 'package:wereda_connect/core/network/api_exception.dart';
import 'package:wereda_connect/features/reputation/data/reputation_cache.dart';
import 'package:wereda_connect/features/reputation/domain/models/own_reputation.dart';
import 'package:wereda_connect/features/reputation/domain/models/reputation_event.dart';

/// The user's own reputation with when it was fetched. [stale] says the
/// request failed (offline, a server error) and this is the saved copy.
class OwnReputationSnapshot {
  final OwnReputation reputation;
  final DateTime fetchedAt;
  final bool stale;

  const OwnReputationSnapshot({
    required this.reputation,
    required this.fetchedAt,
    this.stale = false,
  });
}

/// A page of the user's own events. [stale] as on [OwnReputationSnapshot];
/// a stale page is the saved first page and has no next page to ask for.
class ReputationEventsResult {
  final ReputationEventsPage page;
  final bool stale;

  const ReputationEventsResult({required this.page, this.stale = false});
}

/// Why an appeal was not taken, for the text the sheet shows.
enum AppealFailure {
  /// The window passed, the event is no longer active or cannot be appealed
  /// (`REPUTATION_APPEAL_CLOSED`, or the event is gone).
  closed,

  /// The event already has another appeal (`REPUTATION_APPEAL_EXISTS`).
  exists,

  /// The feature is switched off (`REPUTATION_UNAVAILABLE`).
  unavailable,

  /// The photo is too large, of another type, or its upload was refused.
  photo,

  /// Offline, a timeout or a server error: the draft is kept, try again.
  notSent,
}

class AppealException implements Exception {
  const AppealException(this.failure);

  final AppealFailure failure;

  @override
  String toString() => 'AppealException: ${failure.name}';
}

/// ESC-31: the signed-in farmer's or trucker's OWN reputation
/// (reputation-api.md sections 2, 4, 5, 6 and 13).
///
/// One implementation for the phone and the web: every call is HTTP through
/// [ApiClient], and the offline copy lives in [ReputationCache]
/// (SharedPreferences), so there is no Isar or `dart:io` here and no
/// platform pair to keep in step.
///
/// Role visibility: every route here is a `/reputation/me` route or one the
/// server looks up under the caller's own id. Nothing in this class can ask
/// for another user's reputation, and a buyer never calls it (the server
/// answers a buyer `403`; buyers have no reputation).
class ReputationRepository {
  ReputationRepository({
    required ApiClient api,
    ReputationCache? cache,
    Dio? storage,
    DateTime Function()? now,
  })  : _api = api,
        _cache = cache ?? ReputationCache(),
        _storage = storage,
        _now = now ?? DateTime.now;

  final ApiClient _api;
  final ReputationCache _cache;
  Dio? _storage;
  final DateTime Function() _now;

  /// `GET /reputation/me` is asked at most this often (spec section 8);
  /// in between, the saved answer is shown. Pull-to-refresh passes `force`.
  static const Duration refreshInterval = Duration(minutes: 30);

  /// A modest page: the server reads up to its whole cap of the caller's
  /// rows to sort each page, and the list is read on 3G.
  static const int eventsPageSize = 20;

  /// The appeal photo cap (200 KiB, reputation-api.md section 5).
  static const int maxPhotoBytes = 204800;

  /// The appeal text cap after trimming.
  static const int maxAppealLength = 500;

  /// The user's own reputation: the saved answer while it is younger than
  /// [refreshInterval] (unless [force]), else a fresh one, else the saved
  /// one marked stale. Rethrows the request's failure only when nothing is
  /// saved.
  Future<OwnReputationSnapshot> loadOwn(
    String userId, {
    bool force = false,
  }) async {
    final cached = await _cache.readOwn(userId);
    final cachedReputation =
        cached == null ? null : OwnReputation.fromJson(cached.body);
    if (cached != null && cachedReputation != null && !force) {
      final age = _now().toUtc().difference(cached.fetchedAt);
      if (!age.isNegative && age < refreshInterval) {
        return OwnReputationSnapshot(
          reputation: cachedReputation,
          fetchedAt: cached.fetchedAt,
        );
      }
    }
    try {
      final data = await _api.get<Map<String, dynamic>>('/reputation/me');
      final body = data['reputation'];
      final reputation = OwnReputation.fromJson(body);
      if (body is! Map<String, dynamic> || reputation == null) {
        throw const ServerException('Unexpected reputation answer.', 200);
      }
      final fetchedAt = _now().toUtc();
      await _cache.writeOwn(userId, body, fetchedAt);
      return OwnReputationSnapshot(
        reputation: reputation,
        fetchedAt: fetchedAt,
      );
    } on Object catch (e) {
      if (cached != null && cachedReputation != null) {
        debugPrint('[ReputationRepository] own read failed, saved copy: $e');
        return OwnReputationSnapshot(
          reputation: cachedReputation,
          fetchedAt: cached.fetchedAt,
          stale: true,
        );
      }
      rethrow;
    }
  }

  /// One page of the user's own events, newest first. The first page
  /// ([nextToken] null) is saved and, when the request fails, read back as
  /// a stale page; a later page rethrows its failure.
  Future<ReputationEventsResult> loadEvents(
    String userId, {
    String? nextToken,
  }) async {
    try {
      final data = await _api.get<Map<String, dynamic>>(
        '/reputation/me/events',
        queryParameters: {
          'limit': '$eventsPageSize',
          if (nextToken != null) 'nextToken': nextToken,
        },
      );
      if (nextToken == null) {
        await _cache.writeEvents(userId, data, _now().toUtc());
      }
      return ReputationEventsResult(page: ReputationEventsPage.fromJson(data));
    } on Object catch (e) {
      if (nextToken == null) {
        final cached = await _cache.readEvents(userId);
        if (cached != null) {
          debugPrint('[ReputationRepository] events failed, saved page: $e');
          return ReputationEventsResult(
            // The saved page's token may be long expired: no next page.
            page: ReputationEventsPage(
              events: ReputationEventsPage.fromJson(cached.body).events,
            ),
            stale: true,
          );
        }
      }
      rethrow;
    }
  }

  /// The unsent appeal text for [eventId], kept so a party who writes it
  /// without a connection does not lose it.
  Future<String?> readAppealDraft(String userId, String eventId) =>
      _cache.readDraft(userId, eventId);

  Future<void> saveAppealDraft(String userId, String eventId, String text) =>
      _cache.writeDraft(userId, eventId, text);

  /// Sends the appeal for the user's own [eventId]: the optional photo to
  /// its presigned upload first, then the appeal. An online request (the
  /// contract defines no sync operation for it, and the server judges the
  /// 7 day window by its own clock).
  ///
  /// Returns the event's appeal state as the server stored it. The same
  /// appeal sent twice is answered as already taken, so a retry after a
  /// lost answer is safe. Throws [AppealException]; on
  /// [AppealFailure.notSent] the draft is still there.
  Future<ReputationAppeal> submitAppeal({
    required String userId,
    required String eventId,
    required String text,
    Uint8List? photo,
    String? photoContentType,
  }) async {
    final trimmed = text.trim();
    if (trimmed.isEmpty || trimmed.length > maxAppealLength) {
      throw const AppealException(AppealFailure.notSent);
    }
    String? photoRef;
    if (photo != null) {
      photoRef = await _uploadPhoto(photo, photoContentType);
    }
    final Map<String, dynamic> data;
    try {
      data = await _api.post<Map<String, dynamic>>(
        '/reputation/events/${Uri.encodeComponent(eventId)}/appeal',
        data: {
          'text': trimmed,
          if (photoRef != null) 'photoRef': photoRef,
        },
      );
    } on ApiException catch (e) {
      throw AppealException(_failureOf(e));
    } on Object {
      throw const AppealException(AppealFailure.notSent);
    }

    final event = data['event'];
    final appeal = event is Map && event['appeal'] is Map
        ? ReputationAppeal.fromJson(event['appeal'])
        : ReputationAppeal.sent;
    await _cache.writeDraft(userId, eventId, '');
    await _rememberAppeal(userId, eventId, appeal);
    return appeal;
  }

  /// The content type of an appeal photo from its first bytes: the server
  /// takes JPEG and WebP only. Null for anything else.
  static String? photoContentTypeOf(Uint8List bytes) {
    if (bytes.length >= 3 &&
        bytes[0] == 0xFF &&
        bytes[1] == 0xD8 &&
        bytes[2] == 0xFF) {
      return 'image/jpeg';
    }
    if (bytes.length >= 12 &&
        bytes[0] == 0x52 && // R
        bytes[1] == 0x49 && // I
        bytes[2] == 0x46 && // F
        bytes[3] == 0x46 && // F
        bytes[8] == 0x57 && // W
        bytes[9] == 0x45 && // E
        bytes[10] == 0x42 && // B
        bytes[11] == 0x50) {
      return 'image/webp';
    }
    return null;
  }

  Future<String> _uploadPhoto(Uint8List bytes, String? contentType) async {
    final type = contentType ?? photoContentTypeOf(bytes);
    if (bytes.isEmpty ||
        bytes.length > maxPhotoBytes ||
        (type != 'image/jpeg' && type != 'image/webp')) {
      throw const AppealException(AppealFailure.photo);
    }
    final Map<String, dynamic> answer;
    try {
      answer = await _api.post<Map<String, dynamic>>(
        '/reputation/appeals/photo-url',
        data: {'sizeBytes': bytes.length, 'contentType': type},
      );
    } on ApiException catch (e) {
      final failure = _failureOf(e);
      throw AppealException(
        // A refused size or type is the photo's problem, not the appeal's.
        e.statusCode == 400 ? AppealFailure.photo : failure,
      );
    } on Object {
      throw const AppealException(AppealFailure.notSent);
    }
    final uploadUrl = answer['uploadUrl'];
    final photoRef = answer['photoRef'];
    if (uploadUrl is! String ||
        uploadUrl.isEmpty ||
        photoRef is! String ||
        !photoRef.startsWith('uploads/appeals/')) {
      throw const AppealException(AppealFailure.notSent);
    }
    try {
      // Its own plain Dio, as for the delivery photos: the API client's
      // auth header would clash with the presigned signature, and its
      // certificate pins are for the API host only.
      final storage = _storage ??= Dio(
        BaseOptions(
          connectTimeout: const Duration(seconds: 30),
          sendTimeout: const Duration(seconds: 90),
          receiveTimeout: const Duration(seconds: 30),
        ),
      );
      await storage.put<void>(
        uploadUrl,
        data: Stream<List<int>>.value(bytes),
        options: Options(
          // Both headers are signed into the URL. A browser sets
          // Content-Length itself and refuses a script that tries to.
          headers: {
            Headers.contentTypeHeader: type,
            if (!kIsWeb) Headers.contentLengthHeader: bytes.length,
          },
          contentType: type,
        ),
      );
    } on DioException catch (e) {
      final status = e.response?.statusCode;
      throw AppealException(
        status != null && status >= 400 && status < 500
            ? AppealFailure.photo
            : AppealFailure.notSent,
      );
    }
    return photoRef;
  }

  static AppealFailure _failureOf(ApiException e) => switch (e.code) {
        'REPUTATION_APPEAL_CLOSED' ||
        'REPUTATION_EVENT_NOT_FOUND' =>
          AppealFailure.closed,
        'REPUTATION_APPEAL_EXISTS' => AppealFailure.exists,
        'REPUTATION_UNAVAILABLE' => AppealFailure.unavailable,
        _ => AppealFailure.notSent,
      };

  /// Writes the new appeal state into the saved first page, so the offline
  /// copy does not offer the appeal again.
  Future<void> _rememberAppeal(
    String userId,
    String eventId,
    ReputationAppeal appeal,
  ) async {
    final cached = await _cache.readEvents(userId);
    final rows = cached?.body['events'];
    if (cached == null || rows is! List) return;
    var changed = false;
    final updated = [
      for (final row in rows)
        if (row is Map && row['eventId'] == eventId)
          () {
            changed = true;
            return {
              ...row,
              'appeal': {'status': appeal.status, 'canAppeal': false},
            };
          }()
        else
          row,
    ];
    if (!changed) return;
    await _cache.writeEvents(
      userId,
      {...cached.body, 'events': updated},
      cached.fetchedAt,
    );
  }
}
