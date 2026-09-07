import '../../../core/network/api_client.dart';

double _num(dynamic v) => (v as num?)?.toDouble() ?? 0;

class StoreCategory {
  const StoreCategory({
    required this.id,
    required this.name,
    required this.imageUrl,
    required this.productCount,
  });

  final String id;
  final String name;
  final String? imageUrl;
  final int productCount;

  factory StoreCategory.fromJson(Map<String, dynamic> j) => StoreCategory(
        id: j['id'] as String,
        name: j['name'] as String? ?? '',
        imageUrl: j['imageUrl'] as String?,
        productCount: j['productCount'] as int? ?? 0,
      );
}

class StoreProduct {
  const StoreProduct({
    required this.id,
    required this.name,
    required this.sku,
    required this.categoryName,
    required this.price,
    required this.currency,
    required this.imageUrl,
    required this.availableQuantity,
    required this.inStock,
    required this.acceptsShoppingCredit,
    required this.isFeatured,
  });

  final String id;
  final String name;
  final String sku;
  final String? categoryName;
  final double price;
  final String currency;
  final String? imageUrl;
  final int availableQuantity;
  final bool inStock;
  final bool acceptsShoppingCredit;
  final bool isFeatured;

  factory StoreProduct.fromJson(Map<String, dynamic> j) => StoreProduct(
        id: j['id'] as String,
        name: j['name'] as String? ?? '',
        sku: j['sku'] as String? ?? '',
        categoryName: j['categoryName'] as String?,
        price: _num(j['price']),
        currency: j['currency'] as String? ?? 'USD',
        imageUrl: j['primaryImageUrl'] as String?,
        availableQuantity: j['availableQuantity'] as int? ?? 0,
        inStock: j['inStock'] as bool? ?? false,
        acceptsShoppingCredit: j['acceptsShoppingCredit'] as bool? ?? false,
        isFeatured: j['isFeatured'] as bool? ?? false,
      );
}

class StoreProductDetail extends StoreProduct {
  const StoreProductDetail({
    required super.id,
    required super.name,
    required super.sku,
    required super.categoryName,
    required super.price,
    required super.currency,
    required super.imageUrl,
    required super.availableQuantity,
    required super.inStock,
    required super.acceptsShoppingCredit,
    required super.isFeatured,
    required this.description,
    required this.acceptsMainWallet,
    required this.requiresShipping,
    required this.shippingFee,
    required this.images,
  });

  final String? description;
  final bool acceptsMainWallet;
  final bool requiresShipping;
  final double shippingFee;
  final List<String> images;

  factory StoreProductDetail.fromJson(Map<String, dynamic> j) {
    final images = ((j['images'] as List?) ?? const [])
        .map((e) => (e as Map<String, dynamic>)['url'] as String)
        .toList();

    return StoreProductDetail(
      id: j['id'] as String,
      name: j['name'] as String? ?? '',
      sku: j['sku'] as String? ?? '',
      categoryName: j['categoryName'] as String?,
      price: _num(j['price']),
      currency: j['currency'] as String? ?? 'USD',
      imageUrl: images.isNotEmpty ? images.first : null,
      availableQuantity: j['availableQuantity'] as int? ?? 0,
      inStock: j['inStock'] as bool? ?? false,
      acceptsShoppingCredit: j['acceptsShoppingCredit'] as bool? ?? false,
      isFeatured: j['isFeatured'] as bool? ?? false,
      description: j['description'] as String?,
      acceptsMainWallet: j['acceptsMainWallet'] as bool? ?? false,
      requiresShipping: j['requiresShipping'] as bool? ?? false,
      shippingFee: _num(j['shippingFee']),
      images: images,
    );
  }
}

class StoreHome {
  const StoreHome({
    required this.categories,
    required this.featured,
    required this.newArrivals,
    required this.popular,
    required this.creditEligible,
    required this.shoppingCreditBalance,
    required this.currency,
  });

  final List<StoreCategory> categories;
  final List<StoreProduct> featured;
  final List<StoreProduct> newArrivals;
  final List<StoreProduct> popular;
  final List<StoreProduct> creditEligible;
  final double shoppingCreditBalance;
  final String currency;

  static List<StoreProduct> _products(dynamic list) =>
      ((list as List?) ?? const []).map((e) => StoreProduct.fromJson(e as Map<String, dynamic>)).toList();

  factory StoreHome.fromJson(Map<String, dynamic> j) => StoreHome(
        categories: ((j['categories'] as List?) ?? const [])
            .map((e) => StoreCategory.fromJson(e as Map<String, dynamic>))
            .toList(),
        featured: _products(j['featured']),
        newArrivals: _products(j['newArrivals']),
        popular: _products(j['popular']),
        creditEligible: _products(j['shoppingCreditEligible']),
        shoppingCreditBalance: _num(j['shoppingCreditBalance']),
        currency: j['currency'] as String? ?? 'USD',
      );
}

/// Server-calculated totals. The client never computes an order total itself.
class CheckoutQuote {
  const CheckoutQuote({
    required this.subtotal,
    required this.discount,
    required this.shippingFee,
    required this.tax,
    required this.total,
    required this.currency,
    required this.availableBalance,
    required this.remainingAfterPurchase,
    required this.canAfford,
  });

  final double subtotal;
  final double discount;
  final double shippingFee;
  final double tax;
  final double total;
  final String currency;
  final double availableBalance;
  final double remainingAfterPurchase;
  final bool canAfford;

  factory CheckoutQuote.fromJson(Map<String, dynamic> j) => CheckoutQuote(
        subtotal: _num(j['subtotal']),
        discount: _num(j['discount']),
        shippingFee: _num(j['shippingFee']),
        tax: _num(j['tax']),
        total: _num(j['total']),
        currency: j['currency'] as String? ?? 'USD',
        availableBalance: _num(j['availableBalance']),
        remainingAfterPurchase: _num(j['remainingAfterPurchase']),
        canAfford: j['canAfford'] as bool? ?? false,
      );
}

class StoreOrderItem {
  const StoreOrderItem({
    required this.productName,
    required this.imageUrl,
    required this.unitPrice,
    required this.quantity,
    required this.totalPrice,
  });

  final String productName;
  final String? imageUrl;
  final double unitPrice;
  final int quantity;
  final double totalPrice;

  factory StoreOrderItem.fromJson(Map<String, dynamic> j) => StoreOrderItem(
        productName: j['productName'] as String? ?? '',
        imageUrl: j['imageUrl'] as String?,
        unitPrice: _num(j['unitPrice']),
        quantity: j['quantity'] as int? ?? 0,
        totalPrice: _num(j['totalPrice']),
      );
}

class StoreOrder {
  const StoreOrder({
    required this.id,
    required this.orderNumber,
    required this.subtotal,
    required this.shippingFee,
    required this.tax,
    required this.total,
    required this.currency,
    required this.paymentMethod,
    required this.status,
    required this.items,
    required this.createdAt,
  });

  final String id;
  final String orderNumber;
  final double subtotal;
  final double shippingFee;
  final double tax;
  final double total;
  final String currency;
  final String paymentMethod;
  final String status;
  final List<StoreOrderItem> items;
  final DateTime createdAt;

  bool get isCancellable =>
      status == 'Pending' || status == 'Paid' || status == 'Processing';

  factory StoreOrder.fromJson(Map<String, dynamic> j) => StoreOrder(
        id: j['id'] as String,
        orderNumber: j['orderNumber'] as String? ?? '',
        subtotal: _num(j['subtotal']),
        shippingFee: _num(j['shippingFee']),
        tax: _num(j['tax']),
        total: _num(j['total']),
        currency: j['currency'] as String? ?? 'USD',
        paymentMethod: j['paymentMethod'] as String? ?? 'ShoppingWallet',
        status: j['status'] as String? ?? '',
        items: ((j['items'] as List?) ?? const [])
            .map((e) => StoreOrderItem.fromJson(e as Map<String, dynamic>))
            .toList(),
        createdAt: DateTime.parse(j['createdAt'] as String).toUtc(),
      );
}

/// One line in the local cart. Prices shown here are re-validated by the server at checkout.
class CartLine {
  CartLine({required this.product, this.quantity = 1});

  final StoreProduct product;
  int quantity;

  double get lineTotal => product.price * quantity;
}

class StoreRepository {
  StoreRepository(this._api);

  final ApiClient _api;

  Future<StoreHome> home() async {
    final data = await _api.get('/store/home');
    return StoreHome.fromJson(data as Map<String, dynamic>);
  }

  Future<Paged<StoreProduct>> products({
    String? search,
    String? categoryId,
    bool? shoppingCreditOnly,
    int page = 1,
    int pageSize = 20,
  }) async {
    final data = await _api.get('/store/products', query: {
      'search': search,
      'categoryId': categoryId,
      'paymentMode': shoppingCreditOnly == true ? 'ShoppingWallet' : null,
      'page': page,
      'pageSize': pageSize,
    });

    return Paged.fromJson(data as Map<String, dynamic>, StoreProduct.fromJson);
  }

  Future<StoreProductDetail> product(String id) async {
    final data = await _api.get('/store/products/$id');
    return StoreProductDetail.fromJson(data as Map<String, dynamic>);
  }

  Future<List<StoreCategory>> categories() async {
    final data = await _api.get('/store/categories');
    return (data as List).map((e) => StoreCategory.fromJson(e as Map<String, dynamic>)).toList();
  }

  Map<String, dynamic> _cartBody(List<CartLine> cart, String paymentMethod, String key) => {
        'items': cart
            .map((l) => {'productId': l.product.id, 'quantity': l.quantity})
            .toList(),
        'paymentMethod': paymentMethod,
        'shippingAddressId': null,
        'idempotencyKey': key,
      };

  Future<CheckoutQuote> quote(List<CartLine> cart, String paymentMethod) async {
    final data = await _api.post('/store/checkout/quote',
        body: _cartBody(cart, paymentMethod, 'quote'));
    return CheckoutQuote.fromJson(data as Map<String, dynamic>);
  }

  /// [idempotencyKey] makes a retried checkout return the original order instead of buying twice.
  Future<StoreOrder> checkout(
      List<CartLine> cart, String paymentMethod, String idempotencyKey) async {
    final data = await _api.post('/store/orders',
        body: _cartBody(cart, paymentMethod, idempotencyKey));
    return StoreOrder.fromJson(data as Map<String, dynamic>);
  }

  Future<Paged<StoreOrder>> orders({int page = 1, int pageSize = 20}) async {
    final data = await _api.get('/store/orders', query: {'page': page, 'pageSize': pageSize});
    return Paged.fromJson(data as Map<String, dynamic>, StoreOrder.fromJson);
  }

  Future<StoreOrder> cancelOrder(String id, String reason) async {
    final data = await _api.post('/store/orders/$id/cancel', body: {'reason': reason});
    return StoreOrder.fromJson(data as Map<String, dynamic>);
  }
}
