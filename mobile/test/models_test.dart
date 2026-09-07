import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/errors/api_exception.dart';
import 'package:mobile/core/format/formatters.dart';
import 'package:mobile/features/auctions/data/auction_models.dart';

void main() {
  group('AuctionSnapshot', () {
    Map<String, dynamic> snapshotJson({
      String status = 'FinalPhase',
      double currentPrice = 2380,
      bool targetReached = false,
    }) =>
        {
          'id': 'auction-id',
          'status': status,
          'currentPrice': currentPrice,
          'startingPrice': 1000,
          'targetPrice': 2500,
          'finalPhasePrice': 2000,
          'bidIncrement': 20,
          'currency': 'USD',
          'participantCount': 400,
          'requiredParticipants': 400,
          'remainingSeconds': 72,
          'endsAt': '2026-09-07T12:34:56.000Z',
          'targetReached': targetReached,
          'progress': 0.92,
          'myRank': 3,
          'myHighestPrice': 2340,
          'leaderboard': [],
          'lastSequenceNumber': 69,
          'serverTime': '2026-09-07T12:33:44.000Z',
        };

    test('parses the documented snapshot shape', () {
      final s = AuctionSnapshot.fromJson(snapshotJson());

      expect(s.status, 'FinalPhase');
      expect(s.currentPrice, 2380);
      expect(s.targetPrice, 2500);
      expect(s.finalPhasePrice, 2000);
      expect(s.participantCount, 400);
      expect(s.remainingSeconds, 72);
      expect(s.myRank, 3);
      expect(s.myHighestPrice, 2340);
      expect(s.isLive, isTrue);
      expect(s.isFinalPhase, isTrue);
    });

    test('treats Live and FinalPhase as biddable and nothing else', () {
      expect(AuctionSnapshot.fromJson(snapshotJson(status: 'Live')).isLive, isTrue);
      expect(AuctionSnapshot.fromJson(snapshotJson(status: 'FinalPhase')).isLive, isTrue);

      for (final status in ['Ready', 'WaitingForStart', 'Finished', 'Paused', 'Cancelled']) {
        expect(AuctionSnapshot.fromJson(snapshotJson(status: status)).isLive, isFalse,
            reason: '$status must not be treated as live');
      }
    });

    test('tolerates a missing remainingSeconds', () {
      final json = snapshotJson()..remove('remainingSeconds');
      expect(AuctionSnapshot.fromJson(json).remainingSeconds, isNull);
    });
  });

  group('AuctionSummary', () {
    test('reports how many more participants are needed', () {
      final a = AuctionSummary.fromJson({
        'id': 'a', 'auctionNumber': 'AUC-2026-000001', 'title': 'T',
        'mainProductName': 'P', 'originalPrice': 5000, 'startingPrice': 1000,
        'currentPrice': 1000, 'targetPrice': 2500, 'entryFee': 20, 'currency': 'USD',
        'participantCount': 372, 'requiredParticipants': 400,
        'status': 'WaitingForParticipants',
      });

      expect(a.participantsRemaining, 28);
      expect(a.acceptsRegistration, isTrue);
      expect(a.isLive, isFalse);
      expect(a.fillProgress, closeTo(0.93, 0.01));
    });

    test('never reports a negative shortfall once the target is met', () {
      final a = AuctionSummary.fromJson({
        'id': 'a', 'title': 'T', 'mainProductName': 'P',
        'originalPrice': 5000, 'startingPrice': 1000, 'currentPrice': 1000,
        'targetPrice': 2500, 'entryFee': 20, 'currency': 'USD',
        'participantCount': 405, 'requiredParticipants': 400, 'status': 'Ready',
      });

      expect(a.participantsRemaining, 0);
    });
  });

  group('BidResult', () {
    test('carries the server-decided outcome, including the duplicate flag', () {
      final r = BidResult.fromJson({
        'bidId': 'bid-1',
        'sequenceNumber': 75,
        'previousPrice': 2480,
        'newPrice': 2500,
        'targetPrice': 2500,
        'reachedTarget': true,
        'finalPhase': true,
        'auctionStatus': 'Finished',
        'myRank': 1,
        'myHighestPrice': 2500,
        'wasDuplicate': false,
      });

      expect(r.sequenceNumber, 75);
      expect(r.previousPrice, 2480);
      expect(r.newPrice, 2500);
      expect(r.reachedTarget, isTrue);
      expect(r.auctionStatus, 'Finished');
      expect(r.wasDuplicate, isFalse);
    });
  });

  group('ApiException', () {
    test('preserves the documented error envelope', () {
      final e = ApiException.fromResponse({
        'code': 'SHOPPING_WALLET_OPERATION_NOT_ALLOWED',
        'message': 'Shopping Wallet balance can only be used for store purchases.',
        'details': ['a', 'b'],
      }, 403);

      expect(e.code, ErrorCodes.shoppingWalletOperationNotAllowed);
      expect(e.statusCode, 403);
      expect(e.details, hasLength(2));
    });

    test('falls back to an internal error when the body is not the envelope', () {
      final e = ApiException.fromResponse({}, 500);
      expect(e.code, ErrorCodes.internalError);
    });
  });

  group('Fmt', () {
    test('formats money with two decimals and the currency', () {
      expect(Fmt.money(2500, 'USD'), contains('2,500.00'));
      expect(Fmt.money(2500, 'USD'), endsWith('USD'));
      expect(Fmt.money(null, 'USD'), '—');
    });

    test('renders mm:ss under an hour and hh:mm:ss above it', () {
      expect(Fmt.duration(const Duration(seconds: 38)), '00:38');
      expect(Fmt.duration(const Duration(minutes: 2, seconds: 5)), '02:05');
      expect(Fmt.duration(const Duration(hours: 1, minutes: 42, seconds: 35)), '01:42:35');
    });

    test('clamps a negative countdown to zero rather than showing a negative timer', () {
      expect(Fmt.duration(const Duration(seconds: -10)), '00:00');
    });
  });
}
