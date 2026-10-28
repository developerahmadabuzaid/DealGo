import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../../core/errors/api_exception.dart';
import '../../../core/format/formatters.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/providers.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/app_header.dart';
import '../../../shared/widgets.dart';
import '../../home/data/ads_repository.dart';
import '../data/store_repository.dart';

class StoreScreen extends ConsumerStatefulWidget {
  const StoreScreen({super.key, this.categoryId});

  /// Which department to open on, from `/store?categoryId=…`. Null is "All".
  final String? categoryId;

  @override
  ConsumerState<StoreScreen> createState() => _StoreScreenState();
}

class _StoreScreenState extends ConsumerState<StoreScreen> {
  /// The department chosen in the row of tabs. Null is "All".
  String? _categoryId;

  @override
  void initState() {
    super.initState();
    _categoryId = widget.categoryId;
  }

  @override
  void didUpdateWidget(StoreScreen oldWidget) {
    super.didUpdateWidget(oldWidget);

    /*
     * Only when the address actually names a different department.
     *
     * This tab is kept alive behind the others, so it is rebuilt for all sorts of reasons while
     * somebody is using it. Reading the address every time would snap their chosen department back
     * to whatever they arrived on; comparing it against the previous one means an arrival changes
     * the tab and nothing else does.
     */
    if (oldWidget.categoryId != widget.categoryId) {
      _categoryId = widget.categoryId;
    }
  }

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final homeAsync = ref.watch(storeHomeProvider);

    return Scaffold(
      // No bar of its own: the app's own bar, with the search box and the basket, is drawn above
      // every tab by the shell and stays there while this page scrolls.
      body: homeAsync.when(
        loading: () => const Padding(
          padding: EdgeInsets.all(AppTheme.space4),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Skeleton(height: 74, radius: AppTheme.radiusLg),
              SizedBox(height: AppTheme.space6),
              Skeleton(height: 20, width: 140),
              SizedBox(height: AppTheme.space3),
              Row(
                children: [
                  Expanded(child: Skeleton(height: 210, radius: AppTheme.radiusLg)),
                  SizedBox(width: AppTheme.space3),
                  Expanded(child: Skeleton(height: 210, radius: AppTheme.radiusLg)),
                ],
              ),
            ],
          ),
        ),
        error: (e, _) => ErrorView(
          message: e is ApiException ? e.displayMessage : e.toString(),
          onRetry: () => ref.invalidate(storeHomeProvider),
        ),
        data: (home) {
          /*
           * Two levels. The tabs and the grid show main departments; a main department's
           * sub-departments are chips inside it. What was chosen — from a tab, a chip, or an
           * address such as an advertisement's — may be either, so it is resolved to the main
           * department to show and, when it is a sub-department, the chip to light.
           *
           * A department that has since disappeared falls back to "All".
           */
          final mains = home.categories.where((c) => c.isMain).toList();
          final chosen = home.categories.where((c) => c.id == _categoryId).firstOrNull;
          final selected = chosen == null
              ? null
              : chosen.isMain
                  ? chosen
                  : mains.where((m) => m.id == chosen.parentId).firstOrNull;
          final selectedSub = chosen != null && !chosen.isMain && selected != null ? chosen : null;

          return RefreshIndicator(
            onRefresh: () async {
              ref.invalidate(storeHomeProvider);
              if (selected != null) {
                ref.invalidate(categoryProductsProvider((selectedSub ?? selected).id));
              }
            },
            child: ListView(
              padding: const EdgeInsets.only(bottom: 32),
              children: [
                if (mains.isNotEmpty)
                  _CategoryTabs(
                    categories: mains,
                    selectedId: selected?.id,
                    onSelect: (id) => setState(() => _categoryId = id),
                  ),

                // One department, or the whole store.
                if (selected != null)
                  _CategoryProducts(
                    key: ValueKey(selected.id),
                    category: selected,
                    subcategories:
                        home.categories.where((c) => c.parentId == selected.id).toList(),
                    selectedSub: selectedSub,
                    onSelectSub: (id) => setState(() => _categoryId = id ?? selected.id),
                    artIndex: mains.indexOf(selected),
                  )
                else ...[
                  const _StoreBanner(),

                  // What is on offer comes first, everywhere. A shopper opening a shop is looking
                  // for the price that is down today, not for the shelf plan.
                  if (home.offers.isNotEmpty) _OfferGrid(title: t.offers, products: home.offers),

                  _CollectionRow(home: home),
                  if (mains.isNotEmpty)
                    _DepartmentGrid(
                      categories: mains,
                      onSelect: (id) => setState(() => _categoryId = id),
                    ),
                  if (home.creditEligible.isNotEmpty)
                    _ProductRow(title: t.buyWithShoppingCredit, products: home.creditEligible),
                  if (home.featured.isNotEmpty)
                    _ProductRow(title: t.featured, products: home.featured),
                  if (home.newArrivals.isNotEmpty)
                    _ProductRow(title: t.newArrivals, products: home.newArrivals),
                  if (home.popular.isNotEmpty)
                    _ProductRow(title: t.popular, products: home.popular),
                ],
              ],
            ),
          );
        },
      ),
    );
  }
}

/// The advertisement across the top of the store.
///
/// It shows what an administrator set for the app's large slideshow. There is no separate list
/// for the store: an advertisement worth the front page is worth this page, and keeping one list
/// means nobody has to remember to set the same picture twice.
class _StoreBanner extends ConsumerStatefulWidget {
  const _StoreBanner();

  @override
  ConsumerState<_StoreBanner> createState() => _StoreBannerState();
}

class _StoreBannerState extends ConsumerState<_StoreBanner> {
  static const _interval = Duration(seconds: 4);

  final _pages = PageController();
  Timer? _timer;
  int _index = 0;
  int _count = 0;

  @override
  void initState() {
    super.initState();
    _timer = Timer.periodic(_interval, (_) {
      if (_count < 2 || !_pages.hasClients) return;
      _pages.animateToPage(
        (_index + 1) % _count,
        duration: const Duration(milliseconds: 500),
        curve: Curves.easeOutCubic,
      );
    });
  }

  @override
  void dispose() {
    _timer?.cancel();
    _pages.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final ads = (ref.watch(appAdsProvider).valueOrNull ?? const <AppAd>[])
        .where((a) => a.placement == AdPlacement.slideshow)
        .toList();

    _count = ads.length;
    if (ads.isEmpty) return const SizedBox.shrink();

    return Padding(
      padding: const EdgeInsets.fromLTRB(AppTheme.space3, AppTheme.space2, AppTheme.space3, 0),
      child: ClipRRect(
        borderRadius: BorderRadius.circular(AppTheme.radiusMd),
        child: SizedBox(
          height: 128,
          child: Stack(
            fit: StackFit.expand,
            children: [
              PageView.builder(
                controller: _pages,
                itemCount: ads.length,
                onPageChanged: (i) => setState(() => _index = i),
                itemBuilder: (_, i) => GestureDetector(
                  behavior: HitTestBehavior.opaque,
                  onTap: () => _open(ads[i]),
                  child: RemoteImage(ads[i].imageUrl, radius: 0),
                ),
              ),
              if (ads.length > 1)
                PositionedDirectional(
                  start: 0,
                  end: 0,
                  bottom: 6,
                  child: Row(
                    mainAxisAlignment: MainAxisAlignment.center,
                    children: [
                      for (var i = 0; i < ads.length; i++)
                        Container(
                          margin: const EdgeInsets.symmetric(horizontal: 2.5),
                          width: i == _index ? 14 : 5,
                          height: 4,
                          decoration: BoxDecoration(
                            color: Colors.white.withValues(alpha: i == _index ? 1 : 0.55),
                            borderRadius: BorderRadius.circular(2),
                          ),
                        ),
                    ],
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }

  Future<void> _open(AppAd ad) async {
    final route = ad.route;
    if (route == null || route.isEmpty) return;

    if (!ad.isExternal) {
      if (mounted) context.push(route);
      return;
    }

    final url = Uri.tryParse(route);
    if (url != null) await launchUrl(url, mode: LaunchMode.externalApplication);
  }
}

/// The store's few big doors — offers, new arrivals, what sells, and what shopping credit buys —
/// as picture cards with their name written across the bottom.
class _CollectionRow extends StatelessWidget {
  const _CollectionRow({required this.home});

  final StoreHome home;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);

    final tiles = <(String, String?, String)>[
      if (home.offers.isNotEmpty)
        (t.offers, home.offers.first.imageUrl, '/store/products?offers=1'),
      if (home.newArrivals.isNotEmpty)
        (t.newArrivals, home.newArrivals.first.imageUrl, '/store/products?sort=new'),
      if (home.popular.isNotEmpty)
        (t.popular, home.popular.first.imageUrl, '/store/products?sort=popular'),
      if (home.creditEligible.isNotEmpty)
        (t.buyWithShoppingCredit, home.creditEligible.first.imageUrl, '/store/products?credit=1'),
    ];

    if (tiles.isEmpty) return const SizedBox.shrink();

    return SizedBox(
      height: 124,
      child: ListView.separated(
        scrollDirection: Axis.horizontal,
        padding: const EdgeInsets.fromLTRB(AppTheme.space3, AppTheme.space2, AppTheme.space3, 0),
        itemCount: tiles.length,
        separatorBuilder: (_, _) => const SizedBox(width: 6),
        itemBuilder: (_, i) {
          final (label, image, route) = tiles[i];

          return SizedBox(
            width: 108,
            child: ClipRRect(
              borderRadius: BorderRadius.circular(AppTheme.radiusSm),
              child: Stack(
                fit: StackFit.expand,
                children: [
                  RemoteImage(image, radius: 0),
                  Material(
                    color: Colors.transparent,
                    child: InkWell(onTap: () => context.push(route)),
                  ),
                  PositionedDirectional(
                    start: 0,
                    end: 0,
                    bottom: 0,
                    child: Container(
                      color: Colors.black.withValues(alpha: 0.62),
                      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 5),
                      child: Text(
                        label,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: const TextStyle(
                          color: Colors.white,
                          fontSize: 11.5,
                          fontWeight: FontWeight.w700,
                        ),
                      ),
                    ),
                  ),
                ],
              ),
            ),
          );
        },
      ),
    );
  }
}

/// Every department as a small square picture with its name under it, four to a row.
///
/// Tapping one moves the tabs above rather than opening another page: the department's items
/// then fill this same screen, which is one step fewer than pushing a page to come back from.
class _DepartmentGrid extends StatelessWidget {
  const _DepartmentGrid({required this.categories, required this.onSelect});

  final List<StoreCategory> categories;
  final ValueChanged<String?> onSelect;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);

    return Container(
      margin: const EdgeInsets.only(top: AppTheme.space2),
      padding: const EdgeInsets.fromLTRB(
        AppTheme.space3,
        AppTheme.space2,
        AppTheme.space3,
        AppTheme.space2,
      ),
      color: theme.colorScheme.surface,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            t.shopByCategory,
            style: theme.textTheme.titleMedium?.copyWith(fontSize: 14, fontWeight: FontWeight.w800),
          ),
          const SizedBox(height: AppTheme.space2),
          GridView.builder(
            shrinkWrap: true,
            physics: const NeverScrollableScrollPhysics(),
            padding: EdgeInsets.zero,
            gridDelegate: const SliverGridDelegateWithFixedCrossAxisCount(
              crossAxisCount: 4,
              mainAxisSpacing: 8,
              crossAxisSpacing: 8,
              // The square picture, plus two lines for the longest department name.
              childAspectRatio: 0.78,
            ),
            itemCount: categories.length,
            itemBuilder: (_, i) {
              final category = categories[i];

              return InkWell(
                borderRadius: BorderRadius.circular(AppTheme.radiusSm),
                onTap: () => onSelect(category.id),
                child: Column(
                  children: [
                    Expanded(
                      child: AspectRatio(
                        aspectRatio: 1,
                        child: RemoteImage(category.imageUrl, radius: AppTheme.radiusSm),
                      ),
                    ),
                    const SizedBox(height: 4),
                    Text(
                      category.name,
                      maxLines: 2,
                      textAlign: TextAlign.center,
                      overflow: TextOverflow.ellipsis,
                      style: const TextStyle(
                        fontSize: 11,
                        fontWeight: FontWeight.w600,
                        height: 1.2,
                      ),
                    ),
                  ],
                ),
              );
            },
          ),
        ],
      ),
    );
  }
}

/// "All" and then every department, as tabs across the top of the store.
class _CategoryTabs extends StatelessWidget {
  const _CategoryTabs({required this.categories, required this.selectedId, required this.onSelect});

  final List<StoreCategory> categories;
  final String? selectedId;
  final ValueChanged<String?> onSelect;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);

    return Container(
      height: 38,
      color: theme.colorScheme.surface,
      child: ListView.builder(
        scrollDirection: Axis.horizontal,
        padding: const EdgeInsets.symmetric(horizontal: AppTheme.space2),
        itemCount: categories.length + 1,
        itemBuilder: (context, i) {
          final id = i == 0 ? null : categories[i - 1].id;
          final selected = id == selectedId;
          final label = i == 0 ? t.allCategories : categories[i - 1].name;

          return InkWell(
            borderRadius: BorderRadius.circular(AppTheme.radiusSm),
            onTap: () => onSelect(id),
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 10),
              child: Column(
                mainAxisAlignment: MainAxisAlignment.center,
                children: [
                  Text(
                    label,
                    style: TextStyle(
                      color: selected ? AppTheme.accentText : theme.colorScheme.onSurfaceVariant,
                      fontSize: 14,
                      fontWeight: selected ? FontWeight.w800 : FontWeight.w600,
                    ),
                  ),
                  const SizedBox(height: 4),
                  Container(
                    height: 2.5,
                    width: selected ? 20 : 0,
                    decoration: BoxDecoration(
                      color: AppTheme.brand,
                      borderRadius: BorderRadius.circular(2),
                    ),
                  ),
                ],
              ),
            ),
          );
        },
      ),
    );
  }
}

/// The items in one department, in a grid under the tabs.
class _CategoryProducts extends ConsumerWidget {
  const _CategoryProducts({
    super.key,
    required this.category,
    required this.artIndex,
    this.subcategories = const [],
    this.selectedSub,
    this.onSelectSub,
  });

  /// The main department.
  final StoreCategory category;

  /// Its sub-departments, as chips under the banner; empty when it has none.
  final List<StoreCategory> subcategories;

  /// The chip that is lit; null is "all of this department".
  final StoreCategory? selectedSub;
  final ValueChanged<String?>? onSelectSub;

  /// Which of the six banner artworks this department was given.
  final int artIndex;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);
    // The main department's list already holds its sub-departments' products too.
    final shown = selectedSub ?? category;
    final async = ref.watch(categoryProductsProvider(shown.id));
    final items = async.valueOrNull?.items;

    final chips = subcategories.isEmpty
        ? const SizedBox.shrink()
        : _SubcategoryChips(
            all: category,
            subcategories: subcategories,
            selectedId: selectedSub?.id,
            onSelect: (id) => onSelectSub?.call(id),
          );

    // The banner and the chips stay put while a sub-department's products load, so nothing jumps.
    if (items == null) {
      if (async.hasError) {
        return Column(
          children: [
            _DepartmentAd(category: category, artIndex: artIndex),
          chips,
            Padding(
              padding: const EdgeInsets.all(AppTheme.space4),
              child: Center(
                child: TextButton.icon(
                  onPressed: () => ref.invalidate(categoryProductsProvider(shown.id)),
                  icon: const Icon(Icons.refresh_rounded),
                  label: Text(t.retry),
                ),
              ),
            ),
          ],
        );
      }

      return Column(
        children: [
          _DepartmentAd(category: category, artIndex: artIndex),
          chips,
          const Padding(
            padding: EdgeInsets.all(AppTheme.space5),
            child: Center(child: CircularProgressIndicator()),
          ),
        ],
      );
    }

    if (items.isEmpty) {
      return Column(
        children: [
          _DepartmentAd(category: category, artIndex: artIndex),
          chips,
          const Padding(
            padding: EdgeInsets.symmetric(vertical: AppTheme.space6),
            child: EmptyState(icon: Icons.storefront_rounded),
          ),
        ],
      );
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        // Every department opens on its own advertisement, the way a shop puts its poster at the
        // front of the aisle rather than halfway down it.
        _DepartmentAd(category: category, artIndex: artIndex),

        // Its sub-departments, right under the banner: "all", then each one.
        chips,

        // Then this department's own offers, before its shelves.
        _CategoryOffers(category: shown),

        SectionHeader(
          shown.name,
          action: TextButton(
            onPressed: () => context.push('/store/products?categoryId=${shown.id}'),
            child: Text(t.seeAll),
          ),
        ),
        GridView.builder(
          padding: const EdgeInsets.fromLTRB(AppTheme.space3, 0, AppTheme.space3, AppTheme.space3),
          shrinkWrap: true,
          physics: const NeverScrollableScrollPhysics(),
          gridDelegate: const SliverGridDelegateWithMaxCrossAxisExtent(
            maxCrossAxisExtent: 124,
            // A height, not a ratio: see ProductCard for why.
            mainAxisExtent: ProductCard.tileHeight,
            crossAxisSpacing: 7,
            mainAxisSpacing: 10,
          ),
          itemCount: items.length,
          itemBuilder: (_, i) => ProductCard(product: items[i]),
        ),
      ],
    );
  }
}

/// A main department's sub-departments as a row of chips: "all" first, then each one.
class _SubcategoryChips extends StatelessWidget {
  const _SubcategoryChips({
    required this.all,
    required this.subcategories,
    required this.selectedId,
    required this.onSelect,
  });

  final StoreCategory all;
  final List<StoreCategory> subcategories;
  final String? selectedId;
  final ValueChanged<String?> onSelect;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);

    Widget chip(String label, String? id, String? image, int count) {
      final selected = id == selectedId;
      return ChoiceChip(
        selected: selected,
        showCheckmark: false,
        onSelected: (_) => onSelect(id),
        avatar: image == null
            ? null
            : ClipOval(child: RemoteImage(image, width: 22, height: 22, radius: 0)),
        // The name, then how many products are in it, in a small pill of its own.
        label: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(label),
            const SizedBox(width: 6),
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 1),
              decoration: BoxDecoration(
                color: selected
                    ? Colors.white.withValues(alpha: 0.25)
                    : AppTheme.accent.withValues(alpha: 0.1),
                borderRadius: BorderRadius.circular(10),
              ),
              child: Text(
                '$count',
                style: TextStyle(
                  fontSize: 11,
                  fontWeight: FontWeight.w800,
                  fontFeatures: AppTheme.numeric,
                  color: selected ? Colors.white : AppTheme.accent,
                ),
              ),
            ),
          ],
        ),
        labelStyle: TextStyle(
          fontSize: 13,
          fontWeight: selected ? FontWeight.w800 : FontWeight.w600,
          color: selected ? Colors.white : null,
        ),
        selectedColor: AppTheme.accent,
        shape: StadiumBorder(
          side: BorderSide(color: selected ? AppTheme.accent : Theme.of(context).dividerColor),
        ),
        visualDensity: VisualDensity.compact,
      );
    }

    return SizedBox(
      height: 52,
      child: ListView(
        scrollDirection: Axis.horizontal,
        padding: const EdgeInsets.symmetric(horizontal: AppTheme.space3, vertical: 8),
        children: [
          chip(t.allCategories, null, null, all.productCount),
          for (final sub in subcategories) ...[
            const SizedBox(width: 8),
            chip(sub.name, sub.id, sub.imageUrl, sub.productCount),
          ],
        ],
      ),
    );
  }
}

/// A grid of everything on offer, with the saving written on each picture.
class _OfferGrid extends StatelessWidget {
  const _OfferGrid({required this.title, required this.products});

  final String title;
  final List<StoreProduct> products;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        SectionHeader(
          title,
          action: TextButton(
            onPressed: () => context.push('/store/products?offers=1'),
            child: Text(t.seeAll),
          ),
        ),
        GridView.builder(
          padding: const EdgeInsets.fromLTRB(AppTheme.space3, 0, AppTheme.space3, AppTheme.space2),
          shrinkWrap: true,
          physics: const NeverScrollableScrollPhysics(),
          gridDelegate: const SliverGridDelegateWithMaxCrossAxisExtent(
            maxCrossAxisExtent: 124,
            // A height, not a ratio: see ProductCard for why.
            mainAxisExtent: ProductCard.tileHeight,
            crossAxisSpacing: 7,
            mainAxisSpacing: 10,
          ),
          itemCount: products.length,
          itemBuilder: (_, i) => ProductCard(product: products[i]),
        ),
      ],
    );
  }
}

/// One department's offers, biggest saving first, above its shelves.
class _CategoryOffers extends ConsumerWidget {
  const _CategoryOffers({required this.category});

  final StoreCategory category;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);
    final offers = ref.watch(categoryOffersProvider(category.id)).valueOrNull?.items;

    if (offers == null || offers.isEmpty) return const SizedBox.shrink();

    return _OfferGrid(title: '${t.offers} · ${category.name}', products: offers);
  }
}

/// The banner at the top of a department: its own picture, its name, and the way in.
class _DepartmentAd extends ConsumerWidget {
  const _DepartmentAd({required this.category, required this.artIndex});

  final StoreCategory category;
  final int artIndex;

  /// Six backgrounds, handed out in turn, so neighbouring departments never look alike.
  static const _art = [
    'assets/images/ads/dept_1.png',
    'assets/images/ads/dept_2.png',
    'assets/images/ads/dept_3.png',
    'assets/images/ads/dept_4.png',
    'assets/images/ads/dept_5.png',
    'assets/images/ads/dept_6.png',
  ];

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);

    // The saving this department is actually giving, so the banner has a figure on it worth
    // reading. Without one it says how much there is to look at instead.
    final offers = ref.watch(categoryOffersProvider(category.id)).valueOrNull?.items;
    final best = (offers ?? const <StoreProduct>[])
        .map((p) => p.discountPercent)
        .fold<int>(0, (a, b) => b > a ? b : a);

    return Padding(
      padding: const EdgeInsets.fromLTRB(AppTheme.space3, AppTheme.space2, AppTheme.space3, 0),
      child: Material(
        borderRadius: BorderRadius.circular(AppTheme.radiusMd),
        clipBehavior: Clip.antiAlias,
        color: const Color(0xFF14110F),
        child: InkWell(
          onTap: () => context.push('/store/products?categoryId=${category.id}'),
          child: SizedBox(
            height: 96,
            child: Stack(
              fit: StackFit.expand,
              children: [
                Image.asset(_art[artIndex.abs() % _art.length], fit: BoxFit.cover),

                // The department's own picture, round, at the far end of the banner.
                PositionedDirectional(
                  end: 12,
                  top: 10,
                  bottom: 10,
                  child: AspectRatio(
                    aspectRatio: 1,
                    child: Container(
                      decoration: BoxDecoration(
                        shape: BoxShape.circle,
                        border: Border.all(color: Colors.white.withValues(alpha: 0.75), width: 2),
                      ),
                      child: ClipOval(child: RemoteImage(category.imageUrl, radius: 0)),
                    ),
                  ),
                ),

                PositionedDirectional(
                  start: 12,
                  end: 96,
                  top: 0,
                  bottom: 0,
                  child: Column(
                    mainAxisAlignment: MainAxisAlignment.center,
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        category.name,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: const TextStyle(
                          color: Colors.white,
                          fontSize: 18,
                          height: 1.1,
                          fontWeight: FontWeight.w900,
                          shadows: [Shadow(color: Color(0x99000000), blurRadius: 8)],
                        ),
                      ),
                      const SizedBox(height: 3),
                      Text(
                        best >= 5
                            ? t.departmentSaveUpTo(best)
                            : t.departmentAdBody(category.productCount),
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: const TextStyle(
                          color: Color(0xFFE9E4DF),
                          fontSize: 11.5,
                          fontWeight: FontWeight.w600,
                          shadows: [Shadow(color: Color(0x99000000), blurRadius: 8)],
                        ),
                      ),
                      const SizedBox(height: 7),
                      Container(
                        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 6),
                        decoration: BoxDecoration(
                          color: AppTheme.brand,
                          borderRadius: BorderRadius.circular(18),
                        ),
                        child: Text(
                          t.shopNow,
                          style: const TextStyle(
                            color: Colors.white,
                            fontSize: 11.5,
                            fontWeight: FontWeight.w800,
                          ),
                        ),
                      ),
                    ],
                  ),
                ),
              ],
            ),
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
          height: ProductCard.tileHeight,
          child: ListView.separated(
            scrollDirection: Axis.horizontal,
            padding: const EdgeInsets.symmetric(horizontal: AppTheme.space3),
            itemCount: products.length,
            separatorBuilder: (_, _) => const SizedBox(width: AppTheme.space2),
            itemBuilder: (_, i) => SizedBox(width: 116, child: ProductCard(product: products[i])),
          ),
        ),
      ],
    );
  }
}

class ProductCard extends ConsumerWidget {
  const ProductCard({super.key, required this.product});

  final StoreProduct product;

  /*
   * How tall one tile is, wherever it is laid out.
   *
   * It used to come from an aspect ratio, which made the card's height follow its width — so the
   * band left under the picture for the name and the prices shrank on the phones that fit four
   * columns and grew on the ones that fit three. The writing is the same height either way, so the
   * narrow case overflowed.
   *
   * A fixed height ends it: the picture is square and still fills the width, and the words below
   * always get the same room. The figure is the widest tile this grid allows (124) plus the band
   * the text needs — two lines of name, the two prices, and the shopping-credit line — with enough
   * slack left that a phone set to larger text does not tip it over.
   */
  static const tileHeight = 196.0;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);
    final locale = Localizations.localeOf(context).languageCode;

    /*
     * A picture, a name and a price — nothing else.
     *
     * The card was a framed panel with a badge under every item, which at three to a row left the
     * picture smaller than the writing around it. A shop is read picture first, so the picture now
     * takes the whole width of the tile and everything else is small underneath it.
     */
    return Material(
      color: Colors.transparent,
      child: InkWell(
        borderRadius: BorderRadius.circular(AppTheme.radiusMd),
        onTap: () => context.push('/store/products/${product.id}'),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            ClipRRect(
              borderRadius: BorderRadius.circular(AppTheme.radiusMd),
              child: Stack(
                children: [
                  AspectRatio(
                    aspectRatio: 1,
                    child: RemoteImage(product.imageUrl, width: double.infinity, radius: 0),
                  ),

                  if (product.discountPercent > 0)
                    PositionedDirectional(
                      top: 4,
                      start: 4,
                      child: StatusPill(
                        t.discountOff(product.discountPercent),
                        color: AppTheme.danger,
                        filled: true,
                      ),
                    ),

                  // Out of stock is said on the image, where the eye lands first, so nobody
                  // reads the price and then discovers they cannot buy it.
                  if (!product.inStock)
                    Positioned.fill(
                      child: ColoredBox(
                        color: Colors.black.withValues(alpha: 0.45),
                        child: Center(
                          // The pill is wider than a tile three or four to a row, so it is allowed
                          // to shrink. Without this it ran off the side of every narrow card.
                          child: Padding(
                            padding: const EdgeInsets.symmetric(horizontal: 4),
                            child: FittedBox(
                              fit: BoxFit.scaleDown,
                              child: StatusPill(
                                t.outOfStock,
                                color: Theme.of(context).colorScheme.error,
                                icon: Icons.remove_shopping_cart_rounded,
                                filled: true,
                              ),
                            ),
                          ),
                        ),
                      ),
                    ),
                ],
              ),
            ),
            Expanded(
              child: Padding(
                padding: const EdgeInsets.fromLTRB(2, 4, 2, 0),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      product.name,
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                      style: const TextStyle(
                        fontWeight: FontWeight.w600,
                        fontSize: 11,
                        height: 1.25,
                      ),
                    ),
                    const Spacer(),
                    Row(
                      children: [
                        Flexible(
                          child: Text(
                            Fmt.money(product.price, product.currency, locale: locale),
                            maxLines: 1,
                            style: TextStyle(
                              color: product.isOnOffer ? AppTheme.danger : null,
                              fontSize: 12.5,
                              fontWeight: FontWeight.w800,
                              fontFeatures: AppTheme.numeric,
                            ),
                          ),
                        ),
                        if (product.isOnOffer) ...[
                          const SizedBox(width: 4),
                          Flexible(
                            child: Text(
                              Fmt.money(product.compareAtPrice, product.currency, locale: locale),
                              maxLines: 1,
                              style: TextStyle(
                                color: Theme.of(context).colorScheme.outline,
                                fontSize: 10,
                                decoration: TextDecoration.lineThrough,
                                fontFeatures: AppTheme.numeric,
                              ),
                            ),
                          ),
                        ],
                      ],
                    ),

                    if (product.inStock && product.acceptsShoppingCredit)
                      Padding(
                        padding: const EdgeInsets.only(top: 2),
                        child: Row(
                          children: [
                            const Icon(Icons.card_giftcard_rounded, size: 11, color: AppTheme.gold),
                            const SizedBox(width: 3),
                            Flexible(
                              child: Text(
                                t.shoppingCredit,
                                maxLines: 1,
                                overflow: TextOverflow.ellipsis,
                                style: const TextStyle(
                                  color: AppTheme.gold,
                                  fontSize: 9.5,
                                  fontWeight: FontWeight.w700,
                                ),
                              ),
                            ),
                          ],
                        ),
                      ),
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
  const StoreProductsScreen({
    super.key,
    this.categoryId,
    this.initialSearch,
    this.offersOnly = false,
    this.creditOnly = false,
    this.sortBy,
    this.title,
  });

  final String? categoryId;

  /// Opened from an offers section: only discounted items, biggest saving first.
  final bool offersOnly;

  /// Opened from the shopping-credit collection: only what that wallet can pay for.
  final bool creditOnly;

  /// `new` or `popular`, when the collection that opened this list has its own order.
  final String? sortBy;

  /// What to call the list. Without one it is named after what it is filtered by.
  final String? title;

  /// What was typed into the home page's search bar, so the results open already filtered.
  final String? initialSearch;

  @override
  ConsumerState<StoreProductsScreen> createState() => _StoreProductsScreenState();
}

class _StoreProductsScreenState extends ConsumerState<StoreProductsScreen> {
  late final _search = TextEditingController(text: widget.initialSearch);
  late Future<dynamic> _future;

  @override
  void initState() {
    super.initState();
    _reload();
  }

  void _reload() {
    _future = ref
        .read(storeRepositoryProvider)
        .products(
          search: _search.text.trim().isEmpty ? null : _search.text.trim(),
          categoryId: widget.categoryId,
          onOfferOnly: widget.offersOnly,
          shoppingCreditOnly: widget.creditOnly ? true : null,
          sortBy: widget.offersOnly ? 'discount' : widget.sortBy,
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
      appBar: BrandAppBar(
        title: Text(
          widget.title ??
              (widget.offersOnly
                  ? t.offers
                  : widget.creditOnly
                  ? t.buyWithShoppingCredit
                  : t.store),
        ),
      ),
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
                    message: snapshot.error.toString(),
                    onRetry: () => setState(_reload),
                  );
                }

                final items = (snapshot.data as dynamic).items as List<StoreProduct>;
                if (items.isEmpty) return const EmptyState(icon: Icons.storefront_rounded);

                return GridView.builder(
                  padding: const EdgeInsets.fromLTRB(
                    AppTheme.space3,
                    0,
                    AppTheme.space3,
                    AppTheme.space6,
                  ),
                  gridDelegate: const SliverGridDelegateWithMaxCrossAxisExtent(
                    maxCrossAxisExtent: 124,
                    mainAxisExtent: ProductCard.tileHeight,
                    crossAxisSpacing: 7,
                    mainAxisSpacing: 10,
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

class ProductDetailScreen extends ConsumerStatefulWidget {
  const ProductDetailScreen({super.key, required this.productId});

  final String productId;

  @override
  ConsumerState<ProductDetailScreen> createState() => _ProductDetailScreenState();
}

class _ProductDetailScreenState extends ConsumerState<ProductDetailScreen> {
  /// How many the customer means to add. Kept here so the bottom bar and its total agree.
  int _quantity = 1;

  void _addToCart(StoreProductDetail p) {
    final t = AppStrings.of(context);
    HapticFeedback.lightImpact();
    ref.read(cartProvider.notifier).add(p, quantity: _quantity);

    final messenger = ScaffoldMessenger.of(context);
    messenger.hideCurrentSnackBar();
    messenger.showSnackBar(
      SnackBar(
        behavior: SnackBarBehavior.floating,
        // A snack bar with a button stays up until dismissed unless told otherwise, and this one
        // followed the user back to the store list. A quick confirmation is all it needs to be.
        duration: const Duration(seconds: 2),
        persist: false,
        content: Row(
          children: [
            const Icon(Icons.check_circle_rounded, color: AppTheme.success, size: 20),
            const SizedBox(width: 8),
            Expanded(child: Text(t.addedToCart)),
          ],
        ),
        action: SnackBarAction(label: t.viewCart, onPressed: () => context.push('/store/cart')),
      ),
    );
    setState(() => _quantity = 1);
  }

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final product = ref.watch(productDetailProvider(widget.productId));
    // Only the count: the page does not rebuild when a line elsewhere in the cart changes.
    final inCart = ref.watch(cartProvider.select((c) => c.fold<int>(0, (n, l) => n + l.quantity)));

    return Scaffold(
      backgroundColor: Theme.of(context).colorScheme.surface,
      appBar: BrandAppBar(
        title: Text(t.productDetails),
        actions: [
          IconButton(
            tooltip: t.cart,
            onPressed: () => context.push('/store/cart'),
            icon: Badge(
              isLabelVisible: inCart > 0,
              label: Text('$inCart'),
              backgroundColor: AppTheme.accent,
              child: const Icon(Icons.shopping_bag_outlined),
            ),
          ),
        ],
        bottom: const HeaderSearchBar(),
      ),
      body: product.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => ErrorView(
          message: e is ApiException ? e.displayMessage : t.somethingWentWrong,
          onRetry: () => ref.invalidate(productDetailProvider(widget.productId)),
        ),
        data: (p) => RefreshIndicator(
          onRefresh: () => ref.refresh(productDetailProvider(widget.productId).future),
          child: SingleChildScrollView(
            physics: const AlwaysScrollableScrollPhysics(),
            padding: const EdgeInsets.only(top: 16, bottom: 24),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 16),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      _ProductGallery(product: p),
                      const SizedBox(height: 16),
                      _ProductInfo(product: p),
                    ],
                  ),
                ),
                if (p.categoryId != null) ...[
                  const SizedBox(height: 8),
                  _SimilarProducts(categoryId: p.categoryId!, excludeId: p.id),
                ],
              ],
            ),
          ),
        ),
      ),
      bottomNavigationBar: product.maybeWhen(
        data: (p) => _BuyBar(
          product: p,
          quantity: _quantity,
          onQuantity: (q) => setState(() => _quantity = q),
          onAdd: () => _addToCart(p),
        ),
        orElse: () => const SizedBox.shrink(),
      ),
    );
  }
}

/// The bar pinned under the page: how many, and one button that says what it will cost.
class _BuyBar extends StatelessWidget {
  const _BuyBar({
    required this.product,
    required this.quantity,
    required this.onQuantity,
    required this.onAdd,
  });

  final StoreProductDetail product;
  final int quantity;
  final ValueChanged<int> onQuantity;
  final VoidCallback onAdd;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final p = product;
    final max = p.availableQuantity > 0 ? p.availableQuantity : 1;

    return Container(
      decoration: BoxDecoration(
        color: Theme.of(context).colorScheme.surface,
        boxShadow: const [
          BoxShadow(color: Color(0x1F000000), blurRadius: 12, offset: Offset(0, -3)),
        ],
      ),
      child: SafeArea(
        top: false,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(16, 10, 16, 10),
          child: Row(
            children: [
              if (p.inStock) ...[
                _QuantityCounter(value: quantity, max: max, onChanged: onQuantity),
                const SizedBox(width: 12),
              ],
              Expanded(
                child: SizedBox(
                  height: 50,
                  child: ElevatedButton(
                    style: ElevatedButton.styleFrom(
                      backgroundColor: AppTheme.accent,
                      foregroundColor: Colors.white,
                      disabledBackgroundColor: Colors.grey[300],
                      elevation: 3,
                      shadowColor: AppTheme.accent.withValues(alpha: 0.45),
                      padding: const EdgeInsets.symmetric(horizontal: 14),
                      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(14)),
                      textStyle: const TextStyle(
                        fontSize: 15,
                        fontWeight: FontWeight.w800,
                        fontFamilyFallback: AppTheme.riyalFallback,
                      ),
                    ),
                    onPressed: p.inStock ? onAdd : null,
                    // Shrinks rather than overflows on a narrow phone or with large text.
                    child: FittedBox(
                      fit: BoxFit.scaleDown,
                      child: Row(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          Icon(p.inStock
                              ? Icons.shopping_cart_outlined
                              : Icons.remove_shopping_cart_outlined),
                          const SizedBox(width: 8),
                          Text(p.inStock ? t.addToCart : t.outOfStock),
                          if (p.inStock) ...[
                            const SizedBox(width: 12),
                            _Money(
                              p.price * quantity,
                              p.currency,
                              style: const TextStyle(
                                fontSize: 15,
                                fontWeight: FontWeight.w900,
                                fontFeatures: AppTheme.numeric,
                              ),
                            ),
                          ],
                        ],
                      ),
                    ),
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Minus, the number, plus, on a light grey pill. Never below one, never above what is in stock.
class _QuantityCounter extends StatelessWidget {
  const _QuantityCounter({required this.value, required this.max, required this.onChanged});

  final int value;
  final int max;
  final ValueChanged<int> onChanged;

  @override
  Widget build(BuildContext context) {
    Widget button(IconData icon, VoidCallback? onTap) => IconButton(
          visualDensity: VisualDensity.compact,
          iconSize: 20,
          onPressed: onTap == null
              ? null
              : () {
                  HapticFeedback.selectionClick();
                  onTap();
                },
          icon: Icon(icon),
        );

    return Container(
      height: 50,
      decoration: BoxDecoration(
        color: Theme.of(context).brightness == Brightness.dark
            ? Colors.white10
            : Colors.grey[200],
        borderRadius: BorderRadius.circular(14),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          button(Icons.remove_rounded, value > 1 ? () => onChanged(value - 1) : null),
          SizedBox(
            width: 24,
            child: Text(
              '$value',
              textAlign: TextAlign.center,
              style: const TextStyle(
                fontSize: 16,
                fontWeight: FontWeight.w800,
                fontFeatures: AppTheme.numeric,
              ),
            ),
          ),
          button(Icons.add_rounded, value < max ? () => onChanged(value + 1) : null),
        ],
      ),
    );
  }
}

/// The product's pictures in a light grey rounded box: swipe between them, tap to zoom, dots
/// underneath, and the short facts floating on top.
class _ProductGallery extends StatefulWidget {
  const _ProductGallery({required this.product});

  final StoreProductDetail product;

  @override
  State<_ProductGallery> createState() => _ProductGalleryState();
}

class _ProductGalleryState extends State<_ProductGallery> {
  final _pages = PageController();
  int _index = 0;

  @override
  void dispose() {
    _pages.dispose();
    super.dispose();
  }

  void _openFullScreen(List<String> pictures) {
    Navigator.of(context).push(PageRouteBuilder<void>(
      opaque: false,
      barrierColor: Colors.black,
      pageBuilder: (_, _, _) => _Gallery(pictures: pictures, initial: _index),
      transitionsBuilder: (_, a, _, child) => FadeTransition(opacity: a, child: child),
    ));
  }

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final p = widget.product;
    final dark = Theme.of(context).brightness == Brightness.dark;
    final pictures = <String>[
      ...p.images.where((u) => u.trim().isNotEmpty),
      if (p.images.isEmpty && p.imageUrl != null) p.imageUrl!,
    ];

    return Column(
      children: [
        ClipRRect(
          borderRadius: BorderRadius.circular(16),
          child: Container(
            // Square: as tall as the box is wide, so the picture gets the full width of the
            // screen. Capped so a tablet or a phone on its side still shows the price below.
            height: (MediaQuery.sizeOf(context).width - 32).clamp(280.0, 460.0),
            color: dark ? Colors.white10 : Colors.grey[100],
            child: Stack(
              fit: StackFit.expand,
              children: [
                if (pictures.isEmpty)
                  const Center(
                      child: Icon(Icons.image_outlined, size: 56, color: Colors.black26))
                else
                  RepaintBoundary(
                    child: PageView.builder(
                      controller: _pages,
                      itemCount: pictures.length,
                      onPageChanged: (i) => setState(() => _index = i),
                      itemBuilder: (_, i) => GestureDetector(
                        onTap: () => _openFullScreen(pictures),
                        child: Padding(
                          padding: const EdgeInsets.all(10),
                          child: Hero(
                            tag: 'product-picture-${pictures[i]}',
                            child: RemoteImage(pictures[i], radius: 0, fit: BoxFit.contain),
                          ),
                        ),
                      ),
                    ),
                  ),

                // Top: the saving.
                if (p.isOnOffer)
                  PositionedDirectional(
                    top: 12,
                    start: 12,
                    child: _HeroBadge(t.discountOff(p.discountPercent),
                        color: AppTheme.danger, icon: Icons.local_offer_rounded),
                  ),
                if (p.isFeatured)
                  PositionedDirectional(
                    top: 12,
                    end: 12,
                    child: _HeroBadge(t.featuredBadge,
                        color: AppTheme.gold, icon: Icons.star_rounded),
                  ),

                // Bottom: fast shipping, while there is something to ship.
                if (p.inStock)
                  PositionedDirectional(
                    bottom: 12,
                    start: 12,
                    child: _HeroBadge('⚡ ${t.fastShipping}', color: AppTheme.ink, soft: true),
                  ),
                if (!p.inStock)
                  PositionedDirectional(
                    bottom: 12,
                    start: 12,
                    child: _HeroBadge(t.outOfStock, color: AppTheme.danger),
                  ),

                if (pictures.isNotEmpty)
                  PositionedDirectional(
                    bottom: 10,
                    end: 10,
                    child: Material(
                      color: Colors.white.withValues(alpha: 0.9),
                      shape: const CircleBorder(),
                      child: InkWell(
                        customBorder: const CircleBorder(),
                        onTap: () => _openFullScreen(pictures),
                        child: const Padding(
                          padding: EdgeInsets.all(8),
                          child: Icon(Icons.zoom_out_map_rounded, size: 18, color: AppTheme.ink),
                        ),
                      ),
                    ),
                  ),
              ],
            ),
          ),
        ),

        // Where they are in the pictures.
        if (pictures.length > 1) ...[
          const SizedBox(height: 10),
          Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              for (var i = 0; i < pictures.length; i++)
                GestureDetector(
                  onTap: () => _pages.animateToPage(i,
                      duration: const Duration(milliseconds: 300), curve: Curves.easeOut),
                  child: AnimatedContainer(
                    duration: const Duration(milliseconds: 250),
                    curve: Curves.easeOut,
                    margin: const EdgeInsets.symmetric(horizontal: 3),
                    width: i == _index ? 22 : 8,
                    height: 8,
                    decoration: BoxDecoration(
                      color: i == _index ? AppTheme.accent : Colors.grey[300],
                      borderRadius: BorderRadius.circular(4),
                    ),
                  ),
                ),
            ],
          ),
        ],
      ],
    );
  }
}

/// The pictures full screen, with pinch to zoom.
class _Gallery extends StatefulWidget {
  const _Gallery({required this.pictures, required this.initial});

  final List<String> pictures;
  final int initial;

  @override
  State<_Gallery> createState() => _GalleryState();
}

class _GalleryState extends State<_Gallery> {
  late final _pages = PageController(initialPage: widget.initial);
  late int _index = widget.initial;

  @override
  void dispose() {
    _pages.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: Colors.black,
      body: Stack(
        children: [
          PageView.builder(
            controller: _pages,
            itemCount: widget.pictures.length,
            onPageChanged: (i) => setState(() => _index = i),
            itemBuilder: (_, i) => InteractiveViewer(
              maxScale: 4,
              child: Center(
                child: Hero(
                  tag: 'product-picture-${widget.pictures[i]}',
                  child: RemoteImage(widget.pictures[i], radius: 0, fit: BoxFit.contain),
                ),
              ),
            ),
          ),
          SafeArea(
            child: Padding(
              padding: const EdgeInsets.all(8),
              child: Row(
                children: [
                  IconButton.filledTonal(
                    onPressed: () => Navigator.of(context).pop(),
                    icon: const Icon(Icons.close_rounded),
                  ),
                  const Spacer(),
                  if (widget.pictures.length > 1)
                    Text(
                      '${_index + 1} / ${widget.pictures.length}',
                      style: const TextStyle(color: Colors.white, fontWeight: FontWeight.w700),
                    ),
                  const SizedBox(width: 12),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// A label laid on the product picture.
class _HeroBadge extends StatelessWidget {
  const _HeroBadge(this.label, {required this.color, this.soft = false, this.icon});

  final String label;
  final Color color;
  final IconData? icon;

  /// A quiet outline rather than a solid block, for facts rather than offers.
  final bool soft;

  @override
  Widget build(BuildContext context) {
    final fg = soft ? color : Colors.white;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 5),
      decoration: BoxDecoration(
        color: soft ? Colors.white.withValues(alpha: 0.92) : color,
        borderRadius: BorderRadius.circular(20),
        border: soft ? Border.all(color: color.withValues(alpha: 0.18)) : null,
        boxShadow: const [BoxShadow(color: Color(0x1F000000), blurRadius: 8, offset: Offset(0, 2))],
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (icon != null) ...[Icon(icon, size: 13, color: fg), const SizedBox(width: 4)],
          Text(
            label,
            style: TextStyle(color: fg, fontSize: 12, fontWeight: FontWeight.w800),
          ),
        ],
      ),
    );
  }
}

/// The product's name, its power rating, the price and what can be said about it in a word.
class _ProductInfo extends StatelessWidget {
  const _ProductInfo({required this.product});

  final StoreProductDetail product;

  /// A power rating written in the product's name — "27W", "20 W", "65w" — shown as a tag.
  /// Only what the name already says; nothing is made up for products without one.
  static String? powerOf(String name) {
    final m = RegExp(r'(\d{1,3}(?:\.\d)?)\s?[wW](?![a-zA-Z])').firstMatch(name);
    return m == null ? null : '${m.group(1)}W';
  }

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final scheme = theme.colorScheme;
    final dark = theme.brightness == Brightness.dark;
    final p = product;
    final power = powerOf(p.name);
    final saving = p.isOnOffer ? p.compareAtPrice! - p.price : 0.0;
    final lowStock = p.inStock && p.availableQuantity > 0 && p.availableQuantity <= 5;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          p.name,
          style: TextStyle(
            fontSize: 19,
            fontWeight: FontWeight.bold,
            height: 1.4,
            color: dark ? scheme.onSurface : Colors.black87,
          ),
        ),

        if (power != null || p.categoryPath != null) ...[
          const SizedBox(height: 10),
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              if (power != null) _SpecTag('⚡ $power'),
              if (p.categoryPath != null)
                _SpecTag(
                  p.categoryPath!,
                  muted: true,
                  onTap: p.categoryId == null
                      ? null
                      : () => context.go('/store?categoryId=${p.categoryId}'),
                ),
            ],
          ),
        ],
        const SizedBox(height: 14),

        // The price, large; what it was and what that saves beside it.
        Wrap(
          crossAxisAlignment: WrapCrossAlignment.center,
          spacing: 10,
          runSpacing: 6,
          children: [
            _Money(
              p.price,
              p.currency,
              style: const TextStyle(
                fontSize: 22,
                fontWeight: FontWeight.bold,
                color: Colors.deepOrange,
                fontFeatures: AppTheme.numeric,
              ),
            ),
            if (p.isOnOffer) ...[
              _Money(
                p.compareAtPrice!,
                p.currency,
                style: TextStyle(
                  color: scheme.onSurfaceVariant,
                  fontSize: 14,
                  decoration: TextDecoration.lineThrough,
                  decorationColor: AppTheme.danger,
                  decorationThickness: 2,
                  fontFeatures: AppTheme.numeric,
                ),
              ),
              _SoftBadge(
                t.youSave(Fmt.money(saving, p.currency)),
                color: AppTheme.danger,
              ),
            ],
          ],
        ),
        const SizedBox(height: 14),

        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: [
            if (lowStock)
              _SoftBadge(t.onlyLeft(p.availableQuantity),
                  color: AppTheme.warning, icon: Icons.local_fire_department_rounded)
            else if (p.inStock)
              _SoftBadge(t.inStock, color: AppTheme.success, icon: Icons.check_circle_rounded)
            else
              _SoftBadge(t.outOfStock, color: scheme.error, icon: Icons.cancel_rounded),
            if (p.acceptsShoppingCredit)
              _SoftBadge(t.buyWithShoppingCredit,
                  color: Colors.orange.shade800, icon: Icons.card_giftcard_rounded),
            if (p.requiresShipping && p.shippingFee > 0)
              _SoftBadge('${t.shipping}: ${Fmt.money(p.shippingFee, p.currency)}',
                  color: AppTheme.info, icon: Icons.local_shipping_outlined),
          ],
        ),

        if (p.description != null && p.description!.trim().isNotEmpty) ...[
          const SizedBox(height: 20),
          const Divider(height: 1),
          const SizedBox(height: 16),
          Text(t.aboutProduct,
              style: theme.textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w800)),
          const SizedBox(height: 8),
          _ExpandableText(p.description!.trim()),
        ],
      ],
    );
  }
}

/// A small chip for a fact about the product: light background, rounded, accent border and text.
class _SpecTag extends StatelessWidget {
  const _SpecTag(this.label, {this.muted = false, this.onTap});

  final String label;
  final bool muted;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final color = muted ? Theme.of(context).colorScheme.onSurfaceVariant : AppTheme.accent;
    final tag = Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.08),
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: color.withValues(alpha: muted ? 0.25 : 0.6)),
      ),
      child: Text(
        label,
        style: TextStyle(color: color, fontSize: 12.5, fontWeight: FontWeight.w700),
      ),
    );
    if (onTap == null) return tag;
    return InkWell(borderRadius: BorderRadius.circular(8), onTap: onTap, child: tag);
  }
}

/// A soft coloured badge: tinted background, coloured icon and text.
class _SoftBadge extends StatelessWidget {
  const _SoftBadge(this.label, {required this.color, this.icon});

  final String label;
  final Color color;
  final IconData? icon;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 5),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.1),
        borderRadius: BorderRadius.circular(20),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (icon != null) ...[
            Icon(icon, size: 14, color: color),
            const SizedBox(width: 4),
          ],
          Text(
            label,
            style: TextStyle(
              color: color,
              fontSize: 12,
              fontWeight: FontWeight.w700,
              fontFamilyFallback: AppTheme.riyalFallback,
            ),
          ),
        ],
      ),
    );
  }
}

/// A long description shows its first lines, with "read more" to open the rest.
class _ExpandableText extends StatefulWidget {
  const _ExpandableText(this.text);

  final String text;

  @override
  State<_ExpandableText> createState() => _ExpandableTextState();
}

class _ExpandableTextState extends State<_ExpandableText> {
  static const _collapsedLines = 5;
  bool _open = false;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final style = Theme.of(context).textTheme.bodyMedium?.copyWith(height: 1.75);

    return LayoutBuilder(
      builder: (context, box) {
        // Measure once: only offer "read more" when there really is more.
        final painter = TextPainter(
          text: TextSpan(text: widget.text, style: style),
          maxLines: _collapsedLines,
          textDirection: Directionality.of(context),
          textScaler: MediaQuery.textScalerOf(context),
        )..layout(maxWidth: box.maxWidth);
        final long = painter.didExceedMaxLines;

        return Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            AnimatedSize(
              duration: const Duration(milliseconds: 250),
              curve: Curves.easeOut,
              alignment: AlignmentDirectional.topStart,
              child: Text(
                widget.text,
                style: style,
                maxLines: _open || !long ? null : _collapsedLines,
                overflow: _open || !long ? null : TextOverflow.fade,
              ),
            ),
            if (long)
              TextButton.icon(
                style: TextButton.styleFrom(
                  padding: EdgeInsets.zero,
                  visualDensity: VisualDensity.compact,
                ),
                onPressed: () => setState(() => _open = !_open),
                icon: Icon(_open ? Icons.expand_less_rounded : Icons.expand_more_rounded),
                label: Text(_open ? t.showLess : t.readMore),
              ),
          ],
        );
      },
    );
  }
}

/// More from the same department, under the product.
class _SimilarProducts extends ConsumerWidget {
  const _SimilarProducts({required this.categoryId, required this.excludeId});

  final String categoryId;
  final String excludeId;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);
    final items = ref
        .watch(categoryProductsProvider(categoryId))
        .valueOrNull
        ?.items
        .where((p) => p.id != excludeId)
        .take(12)
        .toList();

    // Nothing to show is not worth a spinner or a message here.
    if (items == null || items.isEmpty) return const SizedBox.shrink();

    return _ProductRow(title: t.similarProducts, products: items);
  }
}

/// An amount and its currency sign, drawn as two pieces.
///
/// The Riyal sign comes from its own small font. Written as one string with the number, a strike
/// line ran through it and a heavy weight smeared it, so the sign looked broken next to the price.
/// Here the number carries the style; the sign keeps the colour and size but never the strike
/// line or the extra weight.
class _Money extends StatelessWidget {
  const _Money(this.value, this.currency, {this.style});

  final num value;
  final String currency;
  final TextStyle? style;

  @override
  Widget build(BuildContext context) {
    final full = Fmt.money(value, currency);
    final cut = full.lastIndexOf(' ');
    final number = cut < 0 ? full : full.substring(0, cut);
    final sign = cut < 0 ? '' : full.substring(cut + 1);
    final size = style?.fontSize ?? 14;

    return Text.rich(
      TextSpan(
        children: [
          TextSpan(text: number, style: style),
          if (sign.isNotEmpty) ...[
            const TextSpan(text: ' '),
            TextSpan(
              text: sign,
              style: TextStyle(
                color: style?.color,
                fontSize: size * 0.85,
                fontWeight: FontWeight.w400,
                decoration: TextDecoration.none,
                // Named here as well, so the sign draws inside buttons and anything else with a
                // text style of its own.
                fontFamily: 'RiyalSign',
                fontFamilyFallback: AppTheme.riyalFallback,
              ),
            ),
          ],
        ],
      ),
      maxLines: 1,
    );
  }
}
