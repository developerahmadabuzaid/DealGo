import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/push/push_service.dart';
import 'package:mobile/features/prizes/data/prize_repository.dart';

void main() {
  group('a tapped notification opens', () {
    test('the auction it is about', () {
      expect(PushService.locationFor({'type': 'AUCTION_STARTED', 'auctionId': 'abc'}),
          '/auctions/abc');
      expect(PushService.locationFor({'type': 'NEW_AUCTION', 'auctionId': 'abc'}), '/auctions/abc');
      expect(PushService.locationFor({'type': 'YOU_LOST', 'auctionId': 'abc'}), '/auctions/abc');
    });

    test('the prizes page for a prize won', () {
      expect(PushService.locationFor({'type': 'YOU_WON', 'auctionId': 'abc', 'prizeClaimId': 'p'}),
          '/prizes');
    });

    test('the wallet for money back or shopping credit', () {
      expect(
          PushService.locationFor({'type': 'ENTRY_FEE_REFUNDED', 'auctionId': 'abc'}), '/wallet');
      expect(
          PushService.locationFor({'type': 'SHOPPING_CREDIT_ADDED', 'auctionId': 'abc'}), '/wallet');
    });

    test('the orders page for an order', () {
      expect(PushService.locationFor({'type': 'ORDER_SHIPPED', 'orderId': 'o1'}), '/store/orders');
      expect(PushService.locationFor({'type': 'STORE_ORDER_CREATED', 'orderId': 'o1'}),
          '/store/orders');
    });

    test('the notification list for a plain message', () {
      expect(PushService.locationFor({'type': 'ADMIN_MESSAGE'}), '/notifications');
      expect(PushService.locationFor({}), '/notifications');
      expect(PushService.locationFor({'auctionId': ''}), '/notifications');
    });

    test('nothing from the list itself for a plain message', () {
      expect(PushService.pageFor({'type': 'ADMIN_MESSAGE'}), isNull);
    });
  });

  group('a notification in the list', () {
    Map<String, dynamic> json(Object? data) => {
          'id': 'n1',
          'type': 'YouWon',
          'title': 't',
          'body': 'b',
          'data': data,
          'readAt': null,
          'createdAt': '2026-11-06T10:00:00Z',
        };

    test('reads what it is about from the server JSON text', () {
      final n = AppNotification.fromJson(json('{"type":"YOU_WON","auctionId":"abc"}'));
      expect(PushService.pageFor(n.data), '/prizes');
    });

    test('without data, or with broken data, leads nowhere', () {
      expect(AppNotification.fromJson(json(null)).data, isEmpty);
      expect(AppNotification.fromJson(json('not json')).data, isEmpty);
    });
  });
}
