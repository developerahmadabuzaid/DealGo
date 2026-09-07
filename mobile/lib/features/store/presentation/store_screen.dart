import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../../core/errors/api_exception.dart';
import '../../../core/format/formatters.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/providers.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/widgets.dart';
import '../data/store_repository.dart';

class StoreScreen extends ConsumerWidget {
  const StoreScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);
    final homeAsync = ref.watch(storeHomeProvider);
    final cart = ref.watch(cartProvider);

    return Scaffold(
      appBar: AppBar(
        title: Text(t.store),
        actions: [
          IconButton(
            onPressed: () => context.push('/store/cart'),
            icon: Badge(
              isLabelVisible: cart.isNotEmpty,
              label: Text('${cart.fold<int>(0, (s, l) => s + l.quantity)}'),
              child: const Icon(Icons.shopping_cart_outlined),
            ),
          ),
          IconButton(
            onPressed: () => context.push('/store/orders'),
            icon: const Icon(Icons.receipt_long_outlined),
          ),
        ],
      ),
      body: homeAsync.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => ErrorView(
          message: e is ApiException ? e.message : e.toString(),
          onRetry: () => ref.invalidate(storeHomeProvider),
        ),
        data: (home) => RefreshIndicator(
          onRefresh: () async => ref.invalidate(storeHomeProvider),
          child: ListView(
            padding: const EdgeInsets.only(bottom: 32),
            children: [
              // The shopping credit balance leads the store: it is what most people come here to spend.
              Padding(
                padding: const EdgeInsets.fromLTRB(16, 16, 16, 0),
                child: Card(
                  color: AppTheme.gold.withValues(alpha: 0.1),
                  child: Padding(
                    padding: const EdgeInsets.all(16),
                    child: Row(
                      children: [
                        const Icon(Icons.card_giftcard_rounded, color: AppTheme.gold),
                        const SizedBox(width: 12),
                        Expanded(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text(t.shoppingCredit,
                                  style: Theme.of(context).textTheme.bodySmall),
                              Text(
                                Fmt.money(home.shoppingCreditBalance, home.currency,
                                    locale: Localizations.localeOf(context).languageCode),
                                style: Theme.of(context)
                                    .textTheme
                                    .titleLarge
                                    ?.copyWith(fontWeight: FontWeight.bold),
                              ),
                            ],
                          ),
                        ),
                        StatusPill(t.storePurchasesOnly, color: AppTheme.gold),
                      ],
                    ),
                  ),
                ),
              ),

              if (home.categories.isNotEmpty) ...[
                SectionHeader(t.categories),
                SizedBox(
                  height: 42,
                  child: ListView.separated(
                    scrollDirection: Axis.horizontal,
                    padding: const EdgeInsets.symmetric(horizontal: 16),
                    itemCount: home.categories.length,
                    separatorBuilder: (_, _) => const SizedBox(width: 8),
                    itemBuilder: (_, i) {
                      final c = home.categories[i];
                      return ActionChip(
                        label: Text('${c.name} (${c.productCount})'),
                        onPressed: () => context.push('/store/products?categoryId=${c.id}'),
                      );
                    },
                  ),
                ),
              ],

              if (home.creditEligible.isNotEmpty)
                _ProductRow(title: t.buyWithShoppingCredit, products: home.creditEligible),
              if (home.featured.isNotEmpty)
                _ProductRow(title: t.featured, products: home.featured),
              if (home.newArrivals.isNotEmpty)
                _ProductRow(title: t.newArrivals, products: home.newArrivals),
              if (home.popular.isNotEmpty)
                _ProductRow(title: t.popular, products: home.popular),
            ],
          ),
        ),
      ),
    );
  }
}

class _ProductRow extends StatelessWidget {
  const _ProductRow({required this.title, required this.products});

  final String title;
  final List<StoreProduct> products;

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        SectionHeader(title),
        SizedBox(
          height: 236,
          child: ListView.separated(
            scrollDirection: Axis.horizontal,
            padding: const EdgeInsets.symmetric(horizontal: 16),
            itemCount: products.length,
            separatorBuilder: (_, _) => const SizedBox(width: 12),
            itemBuilder: (_, i) => SizedBox(
              width: 158,
              child: ProductCard(product: products[i]),
            ),
          ),
        ),
      ],
    );
  }
}

class ProductCard extends ConsumerWidget {
  const ProductCard({super.key, required this.product});

  final StoreProduct product;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);
    final locale = Localizations.localeOf(context).languageCode;

    return Card(
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: () => context.push('/store/products/${product.id}'),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            RemoteImage(product.imageUrl, height: 116, width: double.infinity, radius: 0),
            Expanded(
              child: Padding(
                padding: const EdgeInsets.all(10),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(product.name,
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                        style: const TextStyle(fontWeight: FontWeight.w600, fontSize: 13)),
                    const Spacer(),
                    Text(Fmt.money(product.price, product.currency, locale: locale),
                        style: Theme.of(context)
                            .textTheme
                            .titleSmall
                            ?.copyWith(fontWeight: FontWeight.bold)),
                    const SizedBox(height: 4),
                    if (!product.inStock)
                      StatusPill(t.outOfStock, color: Theme.of(context).colorScheme.error)
                    else if (product.acceptsShoppingCredit)
                      StatusPill(t.buyWithShoppingCredit, color: AppTheme.gold),
                  ],
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// A filtered product list, reachable from a category chip or a search.
class StoreProductsScreen extends ConsumerStatefulWidget {
  const StoreProductsScreen({super.key, this.categoryId});

  final String? categoryId;

  @override
  ConsumerState<StoreProductsScreen> createState() => _StoreProductsScreenState();
}

class _StoreProductsScreenState extends ConsumerState<StoreProductsScreen> {
  final _search = TextEditingController();
  late Future<dynamic> _future;

  @override
  void initState() {
    super.initState();
    _reload();
  }

  void _reload() {
    _future = ref.read(storeRepositoryProvider).products(
          search: _search.text.trim().isEmpty ? null : _search.text.trim(),
          categoryId: widget.categoryId,
          pageSize: 40,
        );
  }

  @override
  void dispose() {
    _search.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);

    return Scaffold(
      appBar: AppBar(title: Text(t.store)),
      body: Column(
        children: [
          Padding(
            padding: const EdgeInsets.all(16),
            child: TextField(
              controller: _search,
              decoration: InputDecoration(
                prefixIcon: const Icon(Icons.search_rounded),
                hintText: t.store,
              ),
              onSubmitted: (_) => setState(_reload),
            ),
          ),
          Expanded(
            child: FutureBuilder(
              future: _future,
              builder: (context, snapshot) {
                if (snapshot.connectionState == ConnectionState.waiting) {
                  return const Center(child: CircularProgressIndicator());
                }
                if (snapshot.hasError) {
                  return ErrorView(
                      message: snapshot.error.toString(), onRetry: () => setState(_reload));
                }

                final items = (snapshot.data as dynamic).items as List<StoreProduct>;
                if (items.isEmpty) return const EmptyState(icon: Icons.storefront_rounded);

                return GridView.builder(
                  padding: const EdgeInsets.fromLTRB(16, 0, 16, 24),
                  gridDelegate: const SliverGridDelegateWithMaxCrossAxisExtent(
                    maxCrossAxisExtent: 200,
                    childAspectRatio: 0.66,
                    crossAxisSpacing: 12,
                    mainAxisSpacing: 12,
                  ),
                  itemCount: items.length,
                  itemBuilder: (_, i) => ProductCard(product: items[i]),
                );
              },
            ),
          ),
        ],
      ),
    );
  }
}

class ProductDetailScreen extends ConsumerWidget {
  const ProductDetailScreen({super.key, required this.productId});

  final String productId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);
    final locale = Localizations.localeOf(context).languageCode;

    return Scaffold(
      appBar: AppBar(),
      body: FutureBuilder(
        future: ref.read(storeRepositoryProvider).product(productId),
        builder: (context, snapshot) {
          if (snapshot.connectionState == ConnectionState.waiting) {
            return const Center(child: CircularProgressIndicator());
          }
          if (snapshot.hasError || snapshot.data == null) {
            return ErrorView(message: snapshot.error?.toString() ?? t.somethingWentWrong);
          }

          final p = snapshot.data!;

          return ListView(
            padding: const EdgeInsets.fromLTRB(16, 0, 16, 110),
            children: [
              RemoteImage(p.imageUrl, height: 250, width: double.infinity),
              const SizedBox(height: 16),
              Text(p.name,
                  style: Theme.of(context)
                      .textTheme
                      .headlineSmall
                      ?.copyWith(fontWeight: FontWeight.bold)),
              const SizedBox(height: 6),
              Text(Fmt.money(p.price, p.currency, locale: locale),
                  style: Theme.of(context).textTheme.titleLarge?.copyWith(
                      color: Theme.of(context).colorScheme.primary,
                      fontWeight: FontWeight.bold)),
              const SizedBox(height: 12),

              Wrap(
                spacing: 8,
                runSpacing: 8,
                children: [
                  if (p.acceptsShoppingCredit)
                    StatusPill(t.buyWithShoppingCredit, color: AppTheme.gold),
                  StatusPill(
                    p.inStock ? '${t.inStock} (${p.availableQuantity})' : t.outOfStock,
                    color: p.inStock ? AppTheme.success : Theme.of(context).colorScheme.error,
                  ),
                  if (p.requiresShipping && p.shippingFee > 0)
                    StatusPill('${t.shipping}: ${Fmt.money(p.shippingFee, p.currency, locale: locale)}'),
                ],
              ),

              if (p.description != null) ...[
                const SizedBox(height: 18),
                Text(p.description!),
              ],
            ],
          );
        },
      ),
      bottomNavigationBar: FutureBuilder(
        future: ref.read(storeRepositoryProvider).product(productId),
        builder: (context, snapshot) {
          final p = snapshot.data;
          if (p == null) return const SizedBox.shrink();

          return SafeArea(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: FilledButton.icon(
                onPressed: p.inStock
                    ? () {
                        ref.read(cartProvider.notifier).add(p);
                        ScaffoldMessenger.of(context)
                            .showSnackBar(SnackBar(content: Text(t.addToCart)));
                      }
                    : null,
                icon: const Icon(Icons.add_shopping_cart_rounded),
                label: Text(p.inStock ? t.addToCart : t.outOfStock),
              ),
            ),
          );
        },
      ),
    );
  }
}
