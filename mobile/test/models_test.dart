import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/config/app_config.dart';
import 'package:mobile/core/errors/api_exception.dart';
import 'package:mobile/core/format/formatters.dart';
import 'package:mobile/features/auctions/data/auction_models.dart';
import 'package:mobile/features/prizes/data/prize_repository.dart';

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
    test('formats money with two decimals and the currency symbol', () {
      expect(Fmt.money(2500, 'SAR'), contains('2,500.00'));
      expect(Fmt.money(2500, 'SAR'), endsWith(''));
      expect(Fmt.money(null, 'SAR'), '—');
    });

    test('shows the symbol for the currency the server sent, not a fixed one', () {
      // The amount always carries its own currency, so a row still stored in another
      // currency must not be relabelled as riyals.
      expect(Fmt.money(10, 'USD'), endsWith(r'$'));
      expect(Fmt.money(10, 'AED'), endsWith('د.إ'));
    });

    test('falls back to the raw code for a currency with no symbol', () {
      expect(Fmt.money(10, 'JPY'), endsWith('JPY'));
    });

    test('keeps Western digits so amounts read the same everywhere', () {
      // Arabic-Indic digits would not match the Latin ids and codes shown beside them.
      expect(Fmt.money(1234.5, 'SAR'), contains('1,234.50'));
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

  group('AppConfig.mediaUrl', () {
    test('gives an uploaded picture the API host it is actually served from', () {
      // What the server stores for anything an administrator uploads. On its own this is not a
      // URL the app can open, which is why these pictures showed as the empty placeholder.
      expect(
        AppConfig.mediaUrl('/uploads/89b41f97.png'),
        '${AppConfig.apiBaseUrl}/uploads/89b41f97.png',
      );
    });

    test('leaves an address that already names its own host alone', () {
      const outside = 'https://picsum.photos/seed/phone/800/600';
      expect(AppConfig.mediaUrl(outside), outside);
    });

    test('still joins a path that was stored without its leading slash', () {
      expect(
        AppConfig.mediaUrl('uploads/a.jpg'),
        '${AppConfig.apiBaseUrl}/uploads/a.jpg',
      );
    });

    test('reports nothing to show rather than an address pointing at the host root', () {
      // A blank value has to come back as null: '' would otherwise resolve to the API root and
      // the screen would try to draw the home page as a photograph.
      expect(AppConfig.mediaUrl(null), isNull);
      expect(AppConfig.mediaUrl(''), isNull);
      expect(AppConfig.mediaUrl('   '), isNull);
    });
  });
  group('Prize discount', () {
    // Same cases as the server's AuctionRules.PrizePaymentAmount tests, so the two cannot drift.
    test('every prize gets the auction discount, not only the first', () {
      expect(prizePriceAt(5000, 2500, 5000), 2500);
      expect(prizePriceAt(1200, 2500, 5000), 600);
      expect(prizePriceAt(1200, 2000, 5000), 480);
      expect(prizePriceAt(999, 2500, 5000), 499.5);
    });

    test('no usable value or price leaves the prize at full price', () {
      expect(prizePriceAt(1200, 2500, 0), 1200);
      expect(prizePriceAt(1200, 0, 5000), 1200);
      expect(discountAt(2500, 0), 0);
    });

    test('discount is the share of the value taken off', () {
      expect(discountAt(2500, 5000), 0.5);
      expect(discountAt(2000, 5000), closeTo(0.6, 1e-9));
      expect(discountAt(6000, 5000), 0);
    });

    test('a claim reports the discount between full price and what is owed', () {
      final claim = PrizeClaim.fromJson({
        'id': 'c1',
        'rank': 4,
        'paymentAmount': 600,
        'prizeOriginalPrice': 1200,
        'prizeDescription': 'Silver, 256 GB',
        'status': 'PaymentRequired',
      });

      expect(claim.prizeOriginalPrice, 1200);
      expect(claim.prizeDescription, 'Silver, 256 GB');
      expect(claim.discount, 0.5);
    });

    test('a claim from an older server has no discount to show', () {
      final claim = PrizeClaim.fromJson({'id': 'c1', 'paymentAmount': 600, 'status': 'Paid'});
      expect(claim.discount, 0);
    });
  });

  group('Auction picture', () {
    Map<String, dynamic> summary({String? cover}) => {
          'id': 'a1',
          'mainImageUrl': '/uploads/prize.png',
          'coverImageUrl': ?cover,
        };

    test('the auction shows its own picture when it has one', () {
      expect(AuctionSummary.fromJson(summary(cover: '/uploads/cover.png')).imageUrl,
          '/uploads/cover.png');
    });

    test('the first prize picture stands in when it has none', () {
      expect(AuctionSummary.fromJson(summary()).imageUrl, '/uploads/prize.png');
    });
  });
}
