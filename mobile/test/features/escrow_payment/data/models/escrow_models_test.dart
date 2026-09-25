import 'package:flutter_test/flutter_test.dart';

import 'package:wereda_connect/features/escrow_payment/data/models/escrow_models.dart';

void main() {
  group('InitiateEscrowRequest', () {
    test('toJson produces correct map', () {
      const request = InitiateEscrowRequest(
        deliveryId: 'del-001',
        buyerId: 'buyer-001',
        farmerId: 'farmer-001',
        amount: 15000.0,
        idempotencyKey: 'buyer-001:del-001:initiate:1708300000000',
      );

      final json = request.toJson();
      expect(json['deliveryId'], 'del-001');
      expect(json['buyerId'], 'buyer-001');
      expect(json['farmerId'], 'farmer-001');
      expect(json['amount'], 15000.0);
      expect(json['currency'], 'ETB');
      expect(
        json['idempotencyKey'],
        'buyer-001:del-001:initiate:1708300000000',
      );
    });

    test('toJson uses default currency ETB', () {
      const request = InitiateEscrowRequest(
        deliveryId: 'del-001',
        buyerId: 'buyer-001',
        farmerId: 'farmer-001',
        amount: 100.0,
        idempotencyKey: 'key-1',
      );

      expect(request.toJson()['currency'], 'ETB');
    });

    test('toJson does not include commission fields (MH3)', () {
      const request = InitiateEscrowRequest(
        deliveryId: 'del-001',
        buyerId: 'buyer-001',
        farmerId: 'farmer-001',
        amount: 15000.0,
        idempotencyKey: 'key-1',
      );

      final json = request.toJson();
      expect(json.containsKey('commission'), isFalse);
      expect(json.containsKey('commissionRate'), isFalse);
      expect(json.containsKey('farmerPayout'), isFalse);
    });
  });

  group('DisputeEscrowRequest', () {
    test('toJson produces correct map with all fields', () {
      const request = DisputeEscrowRequest(
        escrowId: 'esc-001',
        disputedBy: 'buyer-001',
        reason: 'damaged_goods',
        idempotencyKey: 'buyer-001:esc-001:dispute:1708300000000',
        description: 'Bags were wet and moldy',
        evidenceRefs: ['photo-1.jpg', 'photo-2.jpg'],
      );

      final json = request.toJson();
      expect(json['escrowId'], 'esc-001');
      expect(json['disputedBy'], 'buyer-001');
      expect(json['reason'], 'damaged_goods');
      expect(json['idempotencyKey'], 'buyer-001:esc-001:dispute:1708300000000');
      expect(json['description'], 'Bags were wet and moldy');
      expect(json['evidenceRefs'], ['photo-1.jpg', 'photo-2.jpg']);
    });

    test('toJson excludes null optional fields', () {
      const request = DisputeEscrowRequest(
        escrowId: 'esc-001',
        disputedBy: 'buyer-001',
        reason: 'not_delivered',
        idempotencyKey: 'key-1',
      );

      final json = request.toJson();
      expect(json.containsKey('description'), isFalse);
      expect(json.containsKey('evidenceRefs'), isFalse);
    });
  });

  group('InitiateEscrowResponse', () {
    test('fromJson parses all fields correctly', () {
      final json = <String, dynamic>{
        'escrowId': 'esc-001',
        'deliveryId': 'del-001',
        'buyerId': 'buyer-001',
        'farmerId': 'farmer-001',
        'amount': 15000,
        'commission': 375.0,
        'farmerPayout': 14625.0,
        'currency': 'ETB',
        'status': 'escrowed',
        'createdAt': '2026-02-19T10:00:00Z',
        'expiresAt': '2026-02-26T10:00:00Z',
        'telebirrRef': 'TBR-202602190001',
        'message': 'Escrow created successfully',
        'isDuplicate': false,
      };

      final response = InitiateEscrowResponse.fromJson(json);
      expect(response.escrowId, 'esc-001');
      expect(response.deliveryId, 'del-001');
      expect(response.buyerId, 'buyer-001');
      expect(response.farmerId, 'farmer-001');
      expect(response.amount, 15000.0);
      expect(response.commission, 375.0);
      expect(response.farmerPayout, 14625.0);
      expect(response.currency, 'ETB');
      expect(response.status, 'escrowed');
      expect(response.createdAt, '2026-02-19T10:00:00Z');
      expect(response.expiresAt, '2026-02-26T10:00:00Z');
      expect(response.telebirrRef, 'TBR-202602190001');
      expect(response.message, 'Escrow created successfully');
      expect(response.isDuplicate, false);
    });

    test('fromJson handles null optional fields', () {
      final json = <String, dynamic>{
        'escrowId': 'esc-001',
        'deliveryId': 'del-001',
        'buyerId': 'buyer-001',
        'farmerId': 'farmer-001',
        'amount': 100,
        'commission': 5.0,
        'farmerPayout': 95.0,
        'currency': 'ETB',
        'status': 'initiated',
        'createdAt': '2026-02-19T10:00:00Z',
        'expiresAt': '2026-02-26T10:00:00Z',
      };

      final response = InitiateEscrowResponse.fromJson(json);
      expect(response.telebirrRef, isNull);
      expect(response.message, isNull);
      expect(response.isDuplicate, false);
      expect(response.toPayUrl, isNull);
    });

    test('fromJson parses toPayUrl for async (real gateway) initiate', () {
      final json = <String, dynamic>{
        'escrowId': 'esc-001',
        'deliveryId': 'del-001',
        'buyerId': 'buyer-001',
        'farmerId': 'farmer-001',
        'amount': 15000,
        'commission': 375.0,
        'farmerPayout': 14625.0,
        'currency': 'ETB',
        'status': 'initiated',
        'createdAt': '2026-02-19T10:00:00Z',
        'expiresAt': '2026-02-26T10:00:00Z',
        'toPayUrl':
            'https://developerportal.ethiotelebirr.et:38443/payment/web/paygate?prepay_id=P1&sign=x',
      };

      final response = InitiateEscrowResponse.fromJson(json);
      expect(response.status, 'initiated');
      expect(response.toPayUrl, contains('prepay_id=P1'));
    });

    test('fromJson handles integer amounts', () {
      final json = <String, dynamic>{
        'escrowId': 'esc-001',
        'deliveryId': 'del-001',
        'buyerId': 'buyer-001',
        'farmerId': 'farmer-001',
        'amount': 500,
        'commission': 12,
        'farmerPayout': 488,
        'currency': 'ETB',
        'status': 'escrowed',
        'createdAt': '2026-02-19T10:00:00Z',
        'expiresAt': '2026-02-26T10:00:00Z',
      };

      final response = InitiateEscrowResponse.fromJson(json);
      expect(response.amount, 500.0);
      expect(response.commission, 12.0);
      expect(response.farmerPayout, 488.0);
    });
  });

  group('DisputeEscrowResponse', () {
    test('fromJson parses all fields correctly', () {
      final json = <String, dynamic>{
        'escrowId': 'esc-001',
        'status': 'disputed',
        'disputeId': 'disp-001',
        'disputedBy': 'buyer-001',
        'reason': 'damaged_goods',
        'disputeWindowEndsAt': '2026-02-21T10:00:00Z',
        'createdAt': '2026-02-19T10:00:00Z',
        'message': 'Dispute created',
      };

      final response = DisputeEscrowResponse.fromJson(json);
      expect(response.escrowId, 'esc-001');
      expect(response.status, 'disputed');
      expect(response.disputeId, 'disp-001');
      expect(response.disputedBy, 'buyer-001');
      expect(response.reason, 'damaged_goods');
      expect(response.disputeWindowEndsAt, '2026-02-21T10:00:00Z');
      expect(response.createdAt, '2026-02-19T10:00:00Z');
      expect(response.message, 'Dispute created');
    });

    test('fromJson handles null message', () {
      final json = <String, dynamic>{
        'escrowId': 'esc-001',
        'status': 'disputed',
        'disputeId': 'disp-001',
        'disputedBy': 'buyer-001',
        'reason': 'not_delivered',
        'disputeWindowEndsAt': '2026-02-21T10:00:00Z',
        'createdAt': '2026-02-19T10:00:00Z',
      };

      final response = DisputeEscrowResponse.fromJson(json);
      expect(response.message, isNull);
    });
  });

  group('EscrowDetailResponse', () {
    test('fromJson parses full response with dispute', () {
      final json = <String, dynamic>{
        'escrowId': 'esc-001',
        'deliveryId': 'del-001',
        'buyerId': 'buyer-001',
        'farmerId': 'farmer-001',
        'amount': 15000.0,
        'commission': 375.0,
        'commissionRate': 0.025,
        'farmerPayout': 14625.0,
        'currency': 'ETB',
        'status': 'disputed',
        'createdAt': '2026-02-17T10:00:00Z',
        'updatedAt': '2026-02-19T10:00:00Z',
        'expiresAt': '2026-02-24T10:00:00Z',
        'version': 3,
        'telebirrRef': 'TBR-001',
        'dispute': {
          'reason': 'damaged_goods',
          'description': 'Teff bags were wet',
        },
        'disputeWindowEndsAt': '2026-02-21T10:00:00Z',
        'releasedAt': null,
        'refundedAt': null,
        'completedAt': null,
      };

      final response = EscrowDetailResponse.fromJson(json);
      expect(response.escrowId, 'esc-001');
      expect(response.amount, 15000.0);
      expect(response.commissionRate, 0.025);
      expect(response.version, 3);
      expect(response.dispute?['reason'], 'damaged_goods');
      expect(response.dispute?['description'], 'Teff bags were wet');
      expect(response.disputeWindowEndsAt, '2026-02-21T10:00:00Z');
      expect(response.releasedAt, isNull);
    });

    test('fromJson handles minimal response without optional fields', () {
      final json = <String, dynamic>{
        'escrowId': 'esc-002',
        'deliveryId': 'del-002',
        'buyerId': 'buyer-001',
        'farmerId': 'farmer-002',
        'amount': 500.0,
        'commission': 12.50,
        'commissionRate': 0.025,
        'farmerPayout': 487.50,
        'currency': 'ETB',
        'status': 'escrowed',
        'createdAt': '2026-02-19T10:00:00Z',
        'updatedAt': '2026-02-19T10:00:00Z',
        'expiresAt': '2026-02-26T10:00:00Z',
        'version': 1,
      };

      final response = EscrowDetailResponse.fromJson(json);
      expect(response.telebirrRef, isNull);
      expect(response.dispute, isNull);
      expect(response.disputeWindowEndsAt, isNull);
      expect(response.releasedAt, isNull);
      expect(response.refundedAt, isNull);
      expect(response.completedAt, isNull);
    });
  });

  group('EscrowHistoryResponse', () {
    test('fromJson parses list of escrows', () {
      final json = <String, dynamic>{
        'escrows': [
          {
            'escrowId': 'esc-001',
            'deliveryId': 'del-001',
            'amount': 15000.0,
            'commission': 375.0,
            'currency': 'ETB',
            'status': 'completed',
            'createdAt': '2026-02-17T10:00:00Z',
            'counterpartyName': 'Alemayehu Bekele',
            'completedAt': '2026-02-18T10:00:00Z',
          },
          {
            'escrowId': 'esc-002',
            'deliveryId': 'del-002',
            'amount': 8500.0,
            'commission': 212.50,
            'currency': 'ETB',
            'status': 'escrowed',
            'createdAt': '2026-02-19T10:00:00Z',
          },
        ],
        'hasMore': true,
        'total': 5,
        'nextToken': 'abc123',
      };

      final response = EscrowHistoryResponse.fromJson(json);
      expect(response.escrows.length, 2);
      expect(response.hasMore, true);
      expect(response.total, 5);
      expect(response.nextToken, 'abc123');
      expect(response.escrows[0].escrowId, 'esc-001');
      expect(response.escrows[0].counterpartyName, 'Alemayehu Bekele');
      expect(response.escrows[1].counterpartyName, isNull);
    });

    test('fromJson handles empty escrow list', () {
      final json = <String, dynamic>{
        'escrows': <dynamic>[],
        'hasMore': false,
        'total': 0,
      };

      final response = EscrowHistoryResponse.fromJson(json);
      expect(response.escrows, isEmpty);
      expect(response.hasMore, false);
      expect(response.total, 0);
      expect(response.nextToken, isNull);
    });

    test('fromJson handles missing optional fields with defaults', () {
      final json = <String, dynamic>{
        'escrows': <dynamic>[],
      };

      final response = EscrowHistoryResponse.fromJson(json);
      expect(response.hasMore, false);
      expect(response.total, 0);
      expect(response.nextToken, isNull);
    });
  });

  group('EscrowSummary', () {
    test('fromJson parses all fields', () {
      final json = <String, dynamic>{
        'escrowId': 'esc-001',
        'deliveryId': 'del-001',
        'amount': 15000.0,
        'commission': 375.0,
        'currency': 'ETB',
        'status': 'completed',
        'createdAt': '2026-02-17T10:00:00Z',
        'counterpartyName': 'Alemayehu B.',
        'completedAt': '2026-02-18T10:00:00Z',
        'farmerPayout': 13625.0,
        'truckerPayout': 1000.0,
        'truckerId': 'trucker-789',
      };

      final summary = EscrowSummary.fromJson(json);
      expect(summary.escrowId, 'esc-001');
      expect(summary.amount, 15000.0);
      expect(summary.commission, 375.0);
      expect(summary.counterpartyName, 'Alemayehu B.');
      expect(summary.completedAt, '2026-02-18T10:00:00Z');
      // Per-party legs reconcile to the held amount (SIM-15).
      expect(summary.farmerPayout, 13625.0);
      expect(summary.truckerPayout, 1000.0);
      expect(summary.truckerId, 'trucker-789');
      expect(
        summary.farmerPayout! + summary.truckerPayout! + summary.commission,
        summary.amount,
      );
    });

    test('fromJson handles null optional fields', () {
      final json = <String, dynamic>{
        'escrowId': 'esc-002',
        'deliveryId': 'del-002',
        'amount': 500.0,
        'commission': 12.50,
        'currency': 'ETB',
        'status': 'escrowed',
        'createdAt': '2026-02-19T10:00:00Z',
      };

      final summary = EscrowSummary.fromJson(json);
      expect(summary.counterpartyName, isNull);
      expect(summary.completedAt, isNull);
      // Pre-SIM-15 servers send no legs at all.
      expect(summary.farmerPayout, isNull);
      expect(summary.truckerPayout, isNull);
      expect(summary.truckerId, isNull);
    });
  });

  group('EscrowDetailResponse trucker leg (SIM-15)', () {
    test('parses truckerPayout and truckerId, null when absent', () {
      final withTrucker = EscrowDetailResponse.fromJson(<String, dynamic>{
        'escrowId': 'esc-001',
        'status': 'escrowed',
        'amount': 16000.0,
        'commission': 375.0,
        'farmerPayout': 14625.0,
        'truckerPayout': 1000.0,
        'truckerId': 'trucker-789',
      });
      expect(withTrucker.truckerPayout, 1000.0);
      expect(withTrucker.truckerId, 'trucker-789');
      expect(
        withTrucker.farmerPayout +
            withTrucker.truckerPayout! +
            withTrucker.commission,
        withTrucker.amount,
      );

      final withoutTrucker = EscrowDetailResponse.fromJson(<String, dynamic>{
        'escrowId': 'esc-002',
        'status': 'escrowed',
      });
      expect(withoutTrucker.truckerPayout, isNull);
      expect(withoutTrucker.truckerId, isNull);
    });
  });

  group('EscrowAuditEntry', () {
    test('fromJson parses all fields', () {
      final json = <String, dynamic>{
        'auditId': 'audit-001',
        'escrowId': 'esc-001',
        'action': 'initiated',
        'fromState': 'none',
        'toState': 'escrowed',
        'actor': 'buyer-001',
        'timestamp': '2026-02-19T10:00:00Z',
        'telebirrRef': 'TBR-001',
        'metadata': {'key': 'value'},
      };

      final entry = EscrowAuditEntry.fromJson(json);
      expect(entry.auditId, 'audit-001');
      expect(entry.escrowId, 'esc-001');
      expect(entry.action, 'initiated');
      expect(entry.fromState, 'none');
      expect(entry.toState, 'escrowed');
      expect(entry.actor, 'buyer-001');
      expect(entry.telebirrRef, 'TBR-001');
      expect(entry.metadata, {'key': 'value'});
    });

    test('fromJson handles null optional fields', () {
      final json = <String, dynamic>{
        'auditId': 'audit-002',
        'escrowId': 'esc-001',
        'action': 'dispute_raised',
        'fromState': 'escrowed',
        'toState': 'disputed',
        'actor': 'buyer-001',
        'timestamp': '2026-02-19T12:00:00Z',
      };

      final entry = EscrowAuditEntry.fromJson(json);
      expect(entry.telebirrRef, isNull);
      expect(entry.metadata, isNull);
    });
  });
}
