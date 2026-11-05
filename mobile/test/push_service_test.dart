import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/push/push_service.dart';

void main() {
  group('a tapped notification opens', () {
    test('the auction it is about', () {
      expect(PushService.locationFor({'type': 'AUCTION_WON', 'auctionId': 'abc'}), '/auctions/abc');
    });

    test('the orders page for an order', () {
      expect(PushService.locationFor({'type': 'ORDER_SHIPPED', 'orderId': 'o1'}), '/store/orders');
    });

    test('the notification list for anything else', () {
      expect(PushService.locationFor({}), '/notifications');
      expect(PushService.locationFor({'auctionId': ''}), '/notifications');
    });
  });
}
