import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../../core/format/formatters.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/providers.dart';
import '../../../core/theme/app_theme.dart';
import '../../../shared/auto_refresh.dart';
import '../../../shared/art_fallback.dart';
import '../../../shared/brand.dart';
import '../../../shared/widgets.dart';
import '../../auctions/data/auction_models.dart';
import '../../auctions/presentation/ended_auction_screen.dart';
import '../../store/data/store_repository.dart';
import '../data/ads_repository.dart';

/// The front page: a photographic header carrying the search, the advertisements an
/// administrator uploaded, the featured auctions, the store's departments, the auctions about to
/// start, quick links and a product feed.
///
/// Every figure on it comes from the server — the prices, the discounts, the seats left and the
/// instants the clocks count to. Nothing here decides anything; it only shows what is already
/// true.
class HomeScreen extends ConsumerStatefulWidget {
  const HomeScreen({super.key});

  @override
  ConsumerState<HomeScreen> createState() => _HomeScreenState();
}

class _HomeScreenState extends ConsumerState<HomeScreen> {
  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final store = ref.watch(storeHomeProvider);
    final live =
        ref.watch(auctionListProvider('live')).valueOrNull?.items ?? const <AuctionSummary>[];
    final upcoming =
        ref.watch(auctionListProvider('upcoming')).valueOrNull?.items ?? const <AuctionSummary>[];
    final ended =
        ref.watch(auctionListProvider('completed')).valueOrNull?.items ?? const <AuctionSummary>[];

    // The row under the advertisement. An administrator's picks, or whatever is running and
    // coming up until somebody picks any.
    final picked = ref.watch(featuredAuctionsProvider).valueOrNull?.items;
    final featured = (picked == null || picked.isEmpty)
        ? [...live, ...upcoming].take(8).toList()
        : picked;

    final home = store.valueOrNull;
    final storeLoading = home == null && store.isLoading;
    // Main departments only; their sub-departments are chosen inside the store.
    final categories =
        (home?.categories ?? const <StoreCategory>[]).where((c) => c.isMain).toList();


    /*
     * An administrator's own advertisements come first, one list per place. Where a place has
     * none — and while the list is still being fetched — the page advertises what it already
     * knows about: the featured auctions and the store's offers.
     */
    final ads = ref.watch(appAdsProvider).valueOrNull ?? const <AppAd>[];

    List<_Poster> placed(String placement) => [
      for (final ad in ads.where((a) => a.placement == placement))
        _Poster(
          imageUrl: ad.imageUrl,
          title: ad.title,
          note: ad.note,
          live: false,
          onTap: () => _openAd(context, ad),
        ),
    ];

    final stripAds = placed(AdPlacement.strip);
    final showAds = placed(AdPlacement.slideshow);
    final belowAds = placed(AdPlacement.underCategories);

    final page = Scaffold(
        body: RefreshIndicator(
          edgeOffset: MediaQuery.paddingOf(context).top,
          onRefresh: () async => _refresh(ref),
          child: CustomScrollView(
            slivers: [
              // A short strip for whatever an administrator is advertising: the picture
              // alone, nothing written or framed on it, the full width of the screen. Nothing set
              // means nothing shown.
              if (stripAds.isNotEmpty)
                SliverToBoxAdapter(
                  child: _AdStrip(
                    banners: [for (final ad in stripAds) _AdPicture(poster: ad)],
                  ),
                ),

              // Under it, the large moving pictures: one auction at a time, changing on their
              // own, big enough to be looked at rather than read.
              if (showAds.isNotEmpty)
                SliverToBoxAdapter(
                  child: _PosterShow(
                    banners: [for (final ad in showAds) _PosterView(poster: ad)],
                  ),
                ),

              // Straight under the advertisement: the auctions an administrator picked out, as
              // small tiles like the categories below them. With none picked, whatever is running
              // or coming up stands in, so the row is never empty.
              if (featured.isNotEmpty)
                SliverToBoxAdapter(child: _FeaturedAuctions(auctions: featured)),

              SliverToBoxAdapter(
                child: _CategoryTiles(categories: categories, loading: storeLoading),
              ),

              // One still picture under the departments: the same strip, but it does not move.
              if (belowAds.isNotEmpty)
                SliverToBoxAdapter(
                  child: _AdStrip(
                    banners: [for (final ad in belowAds) _AdPicture(poster: ad)],
                    still: true,
                  ),
                ),
              if (home == null || home.offers.isNotEmpty)
                SliverToBoxAdapter(
                  child: _OffersSection(
                    title: t.offers,
                    products: home?.offers,
                    seeAll: '/store/products?offers=1',
                  ),
                ),

              // The auctions about to start, each showing how close it is to filling up.
              if (upcoming.isNotEmpty)
                SliverToBoxAdapter(child: _UpcomingAuctions(auctions: upcoming)),

              // The latest auctions to close, with what each went for and who took it.
              if (ended.isNotEmpty)
                SliverToBoxAdapter(child: _EndedAuctions(auctions: ended.take(3).toList())),
              SliverToBoxAdapter(
                child: _QuickTiles(
                  tiles: [
                    _Tile(
                      t.liveAuctions,
                      live.firstOrNull?.imageUrl,
                      Icons.bolt_rounded,
                      () => context.go('/auctions'),
                    ),
                    _Tile(
                      t.upcomingAuctions,
                      upcoming.firstOrNull?.imageUrl,
                      Icons.schedule_rounded,
                      () => context.go('/auctions'),
                    ),
                    _Tile(
                      t.newArrivals,
                      home?.newArrivals.firstOrNull?.imageUrl,
                      Icons.fiber_new_rounded,
                      () => context.go('/store'),
                    ),
                    _Tile(
                      t.popular,
                      home?.popular.firstOrNull?.imageUrl,
                      Icons.local_fire_department_rounded,
                      () => context.go('/store'),
                    ),
                    _Tile(
                      t.myPrizes,
                      null,
                      Icons.emoji_events_rounded,
                      () => context.push('/prizes'),
                    ),
                  ],
                ),
              ),
              const SliverToBoxAdapter(child: _StorePromo()),
              ..._feed(context, t, home, storeLoading),
              const SliverToBoxAdapter(child: SizedBox(height: AppTheme.space8)),
          ],
        ),
      ),
    );

    return AutoRefresh(onRefresh: () => _refresh(ref), child: page);
  }

  void _refresh(WidgetRef ref) {
    ref.invalidate(storeHomeProvider);
    ref.invalidate(featuredAuctionsProvider);
    ref.invalidate(auctionListProvider('live'));
    ref.invalidate(auctionListProvider('upcoming'));
    ref.invalidate(auctionListProvider('completed'));
    ref.invalidate(unreadCountProvider);
    ref.invalidate(appAdsProvider);
  }

  /// Opens what an advertisement points at: a page in the app, or the phone's browser for an
  /// address outside it. An advertisement that points at nothing is simply a picture.
  Future<void> _openAd(BuildContext context, AppAd ad) async {
    final route = ad.route;
    if (route == null || route.isEmpty) return;

    if (!ad.isExternal) {
      context.push(route);
      return;
    }

    final url = Uri.tryParse(route);
    if (url == null) return;

    /*
     * Nobody is waiting on this.
     *
     * The tap calls it and walks away, so a failure here would become an unhandled asynchronous
     * error: nothing on screen, nothing in a log anybody reads. And it can genuinely fail — a
     * WhatsApp advertisement on a phone with no WhatsApp and no browser has nothing to open it.
     * Saying so is the least the person who tapped deserves.
     */
    try {
      final opened = await launchUrl(url, mode: LaunchMode.externalApplication);
      if (opened || !context.mounted) return;
    } catch (_) {
      if (!context.mounted) return;
    }

    ScaffoldMessenger.of(context).showSnackBar(
      SnackBar(content: Text(AppStrings.of(context).couldNotOpenLink)),
    );
  }


  static double _discount(double price, double original) =>
      original <= 0 ? 0 : (1 - price / original).clamp(0.0, 1.0);

  static String _percent(double fraction) => '${(fraction * 100).round()}%';

  List<Widget> _feed(BuildContext context, AppStrings t, StoreHome? home, bool loading) {
    // Each list can carry the same product, so they are merged without repeats.
    final seen = <String>{};
    final products = [
      ...?home?.featured,
      ...?home?.popular,
      ...?home?.newArrivals,
      ...?home?.creditEligible,
    ].where((p) => seen.add(p.id)).toList();

    if (!loading && products.isEmpty) return const [];

    final width = MediaQuery.sizeOf(context).width;
    final columns = width >= 600 ? 3 : 2;
    const gap = 8.0;
    final cell = (width - 2 * AppTheme.space3 - (columns - 1) * gap) / columns;

    return [
      SliverToBoxAdapter(child: _FeedTitle(t.pickedForYou)),
      SliverPadding(
        padding: const EdgeInsets.symmetric(horizontal: AppTheme.space3),
        sliver: SliverGrid(
          gridDelegate: SliverGridDelegateWithFixedCrossAxisCount(
            crossAxisCount: columns,
            mainAxisSpacing: gap,
            crossAxisSpacing: gap,
            // A 3:4 picture plus the name and price under it.
            mainAxisExtent: cell * 4 / 3 + 72,
          ),
          delegate: SliverChildBuilderDelegate(
            (_, i) => loading
                ? const Skeleton(height: double.infinity, radius: AppTheme.radiusSm)
                : _FeedProductCard(product: products[i]),
            childCount: loading ? columns * 2 : products.length,
          ),
        ),
      ),
    ];
  }
}

// ---------------------------------------------------------------------------------- header

/// The advertisement under the search box: the picture on its own, edge to edge, changing by
/// itself. Nothing is written over it, so whatever is on the picture is read as it was drawn.
class _AdStrip extends StatefulWidget {
  const _AdStrip({required this.banners, this.still = false});

  /// Ready-made banners: an uploaded picture, or one the app draws itself.
  final List<Widget> banners;

  /// Shows the first banner alone and leaves it there, with no dots and nothing to swipe.
  final bool still;

  /// Short on purpose: it is a strip, not a picture to study.
  static const _height = 48.0;

  @override
  State<_AdStrip> createState() => _AdStripState();
}

class _AdStripState extends State<_AdStrip> {
  static const _interval = Duration(seconds: 5);

  final _pages = PageController();
  Timer? _timer;
  int _index = 0;

  @override
  void initState() {
    super.initState();
    if (!widget.still) _play();
  }

  void _play() {
    _timer?.cancel();
    if (widget.still) return;

    _timer = Timer.periodic(_interval, (_) {
      if (widget.banners.length < 2 || !_pages.hasClients) return;
      _pages.animateToPage(
        (_index + 1) % widget.banners.length,
        duration: const Duration(milliseconds: 450),
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
    final banners = widget.banners;
    if (banners.isEmpty) return const SizedBox.shrink();

    final scale = MediaQuery.textScalerOf(context).scale(1).clamp(1.0, 1.2);

    if (widget.still) {
      return SizedBox(height: _AdStrip._height * scale, child: banners.first);
    }

    return Listener(
      onPointerDown: (_) => _timer?.cancel(),
      onPointerUp: (_) => _play(),
      onPointerCancel: (_) => _play(),
      child: SizedBox(
        height: _AdStrip._height * scale,
        child: Stack(
          fit: StackFit.expand,
          children: [
            PageView.builder(
              controller: _pages,
              itemCount: banners.length,
              onPageChanged: (i) => setState(() => _index = i),
              itemBuilder: (_, i) => banners[i],
            ),
            if (banners.length > 1)
              PositionedDirectional(
                start: 0,
                end: 0,
                bottom: 4,
                child: _Dots(count: banners.length, index: _index.clamp(0, banners.length - 1)),
              ),
          ],
        ),
      ),
    );
  }
}

/// An advertisement an administrator uploaded, drawn as a banner: the picture, edge to edge,
/// opening whatever it points at.
class _AdPicture extends StatelessWidget {
  const _AdPicture({required this.poster});

  final _Poster poster;

  @override
  Widget build(BuildContext context) => GestureDetector(
    behavior: HitTestBehavior.opaque,
    onTap: poster.onTap,
    child: _Backdrop(url: poster.imageUrl),
  );
}

/// One picture in the big moving advertisement.
class _Poster {
  const _Poster({
    required this.imageUrl,
    required this.title,
    required this.note,
    required this.live,
    required this.onTap,
  });

  final String imageUrl;

  /// Written across the bottom of the picture. Both are left out when there is nothing to say,
  /// which is the usual case for artwork that already has its words drawn on it.
  final String? title;

  /// The small line under the name: a discount, a price, or a call to join.
  final String? note;

  /// A running auction is marked, so the picture says what it is without being read.
  final bool live;

  final VoidCallback onTap;
}

/// The large advertisement under the strip: full-width pictures that change on their own every few
/// seconds, with the name and a price or discount written across the bottom of each.
class _PosterShow extends StatefulWidget {
  const _PosterShow({required this.banners});

  /// A poster an administrator uploaded, or one the app builds from an auction.
  final List<Widget> banners;

  @override
  State<_PosterShow> createState() => _PosterShowState();
}

class _PosterShowState extends State<_PosterShow> {
  static const _interval = Duration(seconds: 4);

  final _pages = PageController(viewportFraction: 0.92);
  Timer? _timer;
  int _index = 0;

  @override
  void initState() {
    super.initState();
    _play();
  }

  /// Moves to the next picture on its own, and holds still while a finger is on it.
  void _play() {
    _timer?.cancel();
    _timer = Timer.periodic(_interval, (_) {
      if (widget.banners.length < 2 || !_pages.hasClients) return;
      _pages.animateToPage(
        (_index + 1) % widget.banners.length,
        duration: const Duration(milliseconds: 550),
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
    final banners = widget.banners;
    final index = _index.clamp(0, banners.length - 1);
    final scale = MediaQuery.textScalerOf(context).scale(1).clamp(1.0, 1.2);

    return Padding(
      padding: const EdgeInsets.only(top: AppTheme.space1),
      child: Column(
        children: [
          Listener(
            onPointerDown: (_) => _timer?.cancel(),
            onPointerUp: (_) => _play(),
            onPointerCancel: (_) => _play(),
            child: SizedBox(
              // Tall enough for a poster to read as a page, short enough that the row of
              // departments and the advertisement under it are on screen without scrolling.
              height: 188 * scale,
              child: PageView.builder(
                controller: _pages,
                itemCount: banners.length,
                onPageChanged: (i) => setState(() => _index = i),
                itemBuilder: (_, i) => Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 5),
                  child: banners[i],
                ),
              ),
            ),
          ),
          const SizedBox(height: 4),
          _Dots(count: banners.length, index: index, onLight: true),
        ],
      ),
    );
  }
}

class _PosterView extends StatelessWidget {
  const _PosterView({required this.poster});

  final _Poster poster;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);

    return Material(
      borderRadius: BorderRadius.circular(AppTheme.radiusLg),
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: poster.onTap,
        child: Stack(
          fit: StackFit.expand,
          children: [
            RemoteImage(poster.imageUrl, radius: 0),

            // Dark only along the bottom, where the words are.
            const Positioned.fill(
              child: DecoratedBox(
                decoration: BoxDecoration(
                  gradient: LinearGradient(
                    begin: Alignment.bottomCenter,
                    end: Alignment.topCenter,
                    colors: [Color(0xD9120A05), Color(0x00120A05)],
                    stops: [0, 0.62],
                  ),
                ),
              ),
            ),

            if (poster.live)
              PositionedDirectional(
                top: 8,
                start: 8,
                child: _Badge(label: t.arenaLive, color: AppTheme.danger, dot: true),
              ),

            PositionedDirectional(
              start: 12,
              end: 12,
              bottom: 10,
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  if (poster.title case final title? when title.isNotEmpty)
                    Text(
                      title,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: const TextStyle(
                        color: Colors.white,
                        fontSize: 15,
                        fontWeight: FontWeight.w900,
                        shadows: [Shadow(color: Color(0x80000000), blurRadius: 10)],
                      ),
                    ),
                  if (poster.note case final note? when note.isNotEmpty) ...[
                    const SizedBox(height: 3),
                    Text(
                      note,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: const TextStyle(
                        color: Colors.white,
                        fontSize: 12.5,
                        fontWeight: FontWeight.w800,
                        shadows: [Shadow(color: Color(0x80000000), blurRadius: 10)],
                      ),
                    ),
                  ],
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}


class _Backdrop extends StatelessWidget {
  const _Backdrop({required this.url});

  final String? url;

  @override
  Widget build(BuildContext context) {
    if (url == null) {
      return const DecoratedBox(
        decoration: BoxDecoration(
          gradient: LinearGradient(
            begin: Alignment.topRight,
            end: Alignment.bottomLeft,
            colors: [AppTheme.accent, AppTheme.ink],
          ),
        ),
      );
    }

    return RemoteImage(url, radius: 0);
  }
}


class _Dots extends StatelessWidget {
  const _Dots({required this.count, required this.index, this.onLight = false});

  final int count;
  final int index;

  /// Outside a photograph the dots sit on the page itself, so they are drawn dark.
  final bool onLight;

  @override
  Widget build(BuildContext context) {
    if (count < 2) return const SizedBox(height: 4);

    return Row(
      mainAxisAlignment: MainAxisAlignment.center,
      children: [
        for (var i = 0; i < count; i++)
          AnimatedContainer(
            duration: const Duration(milliseconds: 250),
            margin: const EdgeInsets.symmetric(horizontal: 2.5),
            width: i == index ? 16 : 6,
            height: 4,
            decoration: BoxDecoration(
              color: (onLight ? AppTheme.brand : Colors.white).withValues(
                alpha: i == index ? 1 : 0.32,
              ),
              borderRadius: BorderRadius.circular(2),
            ),
          ),
      ],
    );
  }
}

// ---------------------------------------------------------------------------------- body

class _Tile {
  const _Tile(this.label, this.imageUrl, this.icon, this.onTap);

  final String label;
  final String? imageUrl;

  /// Drawn on a brand gradient when there is no picture yet.
  final IconData icon;
  final VoidCallback onTap;
}

/// Picture tiles with a dark caption bar, the quickest way into each part of the app.
class _QuickTiles extends StatelessWidget {
  const _QuickTiles({required this.tiles});

  final List<_Tile> tiles;

  @override
  Widget build(BuildContext context) {
    return Container(
      margin: const EdgeInsets.only(top: AppTheme.space2),
      padding: const EdgeInsets.symmetric(vertical: AppTheme.space2),
      color: Theme.of(context).colorScheme.surface,
      child: SizedBox(
        height: 136,
        child: ListView.separated(
          scrollDirection: Axis.horizontal,
          padding: const EdgeInsets.symmetric(horizontal: AppTheme.space2),
          itemCount: tiles.length,
          separatorBuilder: (_, _) => const SizedBox(width: 6),
          itemBuilder: (_, i) => _QuickTile(tile: tiles[i]),
        ),
      ),
    );
  }
}

class _QuickTile extends StatelessWidget {
  const _QuickTile({required this.tile});

  final _Tile tile;

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: tile.onTap,
      child: ClipRRect(
        borderRadius: BorderRadius.circular(6),
        child: SizedBox(
          width: 102,
          child: Stack(
            children: [
              Positioned.fill(
                child: tile.imageUrl != null
                    ? RemoteImage(tile.imageUrl, radius: 0)
                    : DecoratedBox(
                        decoration: const BoxDecoration(
                          gradient: LinearGradient(
                            begin: Alignment.topRight,
                            end: Alignment.bottomLeft,
                            colors: [AppTheme.brandSoft, AppTheme.accent],
                          ),
                        ),
                        child: Align(
                          alignment: const Alignment(0, -0.3),
                          child: Icon(tile.icon, color: Colors.white, size: 38),
                        ),
                      ),
              ),
              Positioned(
                left: 0,
                right: 0,
                bottom: 0,
                child: Container(
                  height: 30,
                  alignment: Alignment.center,
                  padding: const EdgeInsets.symmetric(horizontal: 4),
                  color: const Color(0xD9272A30),
                  child: Text(
                    tile.label,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: const TextStyle(
                      color: Colors.white,
                      fontSize: 12,
                      fontWeight: FontWeight.w700,
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


/// Discounted items in a row, each showing what it now costs against what it cost before.
class _OffersSection extends StatelessWidget {
  const _OffersSection({required this.title, required this.products, required this.seeAll});

  final String title;

  /// Null while they are still being fetched.
  final List<StoreProduct>? products;
  final String seeAll;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    final scale = MediaQuery.textScalerOf(context).scale(1).clamp(1.0, 1.4);

    return Container(
      margin: const EdgeInsets.only(top: AppTheme.space2),
      padding: const EdgeInsets.only(bottom: AppTheme.space3),
      color: theme.colorScheme.surface,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Padding(
            padding: const EdgeInsetsDirectional.fromSTEB(AppTheme.space3, 6, 0, 0),
            child: Row(
              children: [
                const Icon(Icons.local_offer_rounded, size: 18, color: AppTheme.danger),
                const SizedBox(width: 6),
                Expanded(
                  child: Text(
                    title,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: theme.textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w800),
                  ),
                ),
                TextButton(onPressed: () => context.push(seeAll), child: Text(t.seeAll)),
              ],
            ),
          ),
          SizedBox(
            height: 186 * scale,
            child: ListView.separated(
              scrollDirection: Axis.horizontal,
              padding: const EdgeInsets.symmetric(horizontal: AppTheme.space3),
              itemCount: products?.length ?? 4,
              separatorBuilder: (_, _) => const SizedBox(width: AppTheme.space2),
              itemBuilder: (_, i) {
                final list = products;
                if (list == null) {
                  return const SizedBox(
                    width: 110,
                    child: Skeleton(height: double.infinity, radius: AppTheme.radiusSm),
                  );
                }
                return _OfferCard(product: list[i]);
              },
            ),
          ),
        ],
      ),
    );
  }
}

class _OfferCard extends StatelessWidget {
  const _OfferCard({required this.product});

  final StoreProduct product;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final locale = Localizations.localeOf(context).languageCode;
    final theme = Theme.of(context);

    return SizedBox(
      width: 110,
      child: Material(
        color: theme.colorScheme.surface,
        borderRadius: BorderRadius.circular(AppTheme.radiusSm),
        clipBehavior: Clip.antiAlias,
        child: InkWell(
          onTap: () => context.push('/store/products/${product.id}'),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              // The picture takes whatever height the name and prices leave, so neither the
              // second price nor a large font setting can be cut off.
              Expanded(
                child: Stack(
                  fit: StackFit.expand,
                  children: [
                    ColoredBox(
                      color: theme.colorScheme.surfaceContainerHighest,
                      child: RemoteImage(product.imageUrl, radius: 0),
                    ),
                    if (product.discountPercent > 0)
                      PositionedDirectional(
                        top: 6,
                        start: 6,
                        child: _Badge(
                          label: t.discountOff(product.discountPercent),
                          color: AppTheme.danger,
                        ),
                      ),
                    if (!product.inStock)
                      Positioned.fill(
                        child: ColoredBox(
                          color: Colors.black.withValues(alpha: 0.45),
                          child: Center(
                            child: _Badge(label: t.outOfStock, color: AppTheme.ink),
                          ),
                        ),
                      ),
                  ],
                ),
              ),
              Padding(
                padding: const EdgeInsets.fromLTRB(AppTheme.space2, 6, AppTheme.space2, 6),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      product.name,
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: const TextStyle(fontSize: 11.5, fontWeight: FontWeight.w500),
                    ),
                    const SizedBox(height: 2),

                    // Both prices on one line: what it costs now, and what it cost before.
                    FittedBox(
                      fit: BoxFit.scaleDown,
                      alignment: AlignmentDirectional.centerStart,
                      child: Row(
                        mainAxisSize: MainAxisSize.min,
                        textBaseline: TextBaseline.alphabetic,
                        crossAxisAlignment: CrossAxisAlignment.baseline,
                        children: [
                          Text(
                            Fmt.money(product.price, product.currency, locale: locale),
                            maxLines: 1,
                            style: const TextStyle(
                              color: AppTheme.danger,
                              fontSize: 13.5,
                              fontWeight: FontWeight.w800,
                              fontFeatures: AppTheme.numeric,
                            ),
                          ),
                          if (product.isOnOffer) ...[
                            const SizedBox(width: 5),
                            Text(
                              Fmt.money(product.compareAtPrice, product.currency, locale: locale),
                              maxLines: 1,
                              style: TextStyle(
                                color: theme.colorScheme.outline,
                                fontSize: 11.5,
                                fontWeight: FontWeight.w600,
                                decoration: TextDecoration.lineThrough,
                                fontFeatures: AppTheme.numeric,
                              ),
                            ),
                          ],
                        ],
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
}


class _Badge extends StatelessWidget {
  const _Badge({required this.label, required this.color, this.dot = false});

  final String label;
  final Color color;
  final bool dot;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 7, vertical: 3),
      decoration: BoxDecoration(color: color, borderRadius: BorderRadius.circular(4)),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (dot) ...[
            Container(
              width: 6,
              height: 6,
              decoration: const BoxDecoration(color: Colors.white, shape: BoxShape.circle),
            ),
            const SizedBox(width: 4),
          ],
          Text(
            label,
            style: const TextStyle(color: Colors.white, fontSize: 11, fontWeight: FontWeight.w700),
          ),
        ],
      ),
    );
  }
}

class _FeedTitle extends StatelessWidget {
  const _FeedTitle(this.title);

  final String title;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final line = Expanded(child: Divider(color: theme.colorScheme.outlineVariant));

    return Padding(
      padding: const EdgeInsets.fromLTRB(AppTheme.space4, AppTheme.space3, AppTheme.space4, 8),
      child: Row(
        children: [
          line,
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: AppTheme.space3),
            child: Text(
              title,
              style: theme.textTheme.titleMedium?.copyWith(
                fontSize: 14,
                fontWeight: FontWeight.w800,
              ),
            ),
          ),
          line,
        ],
      ),
    );
  }
}

class _FeedProductCard extends StatelessWidget {
  const _FeedProductCard({required this.product});

  final StoreProduct product;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final locale = Localizations.localeOf(context).languageCode;
    final theme = Theme.of(context);

    return Material(
      color: theme.colorScheme.surface,
      borderRadius: BorderRadius.circular(6),
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: () => context.push('/store/products/${product.id}'),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            AspectRatio(
              aspectRatio: 3 / 4,
              child: Stack(
                fit: StackFit.expand,
                children: [
                  RemoteImage(product.imageUrl, radius: 0),
                  if (product.discountPercent > 0)
                    PositionedDirectional(
                      top: 6,
                      start: 6,
                      child: _Badge(
                        label: t.discountOff(product.discountPercent),
                        color: AppTheme.danger,
                      ),
                    ),
                  if (!product.inStock)
                    ColoredBox(
                      color: Colors.black.withValues(alpha: 0.45),
                      child: Center(
                        child: _Badge(label: t.outOfStock, color: AppTheme.danger),
                      ),
                    ),
                ],
              ),
            ),
            Padding(
              padding: const EdgeInsets.fromLTRB(AppTheme.space2, 6, AppTheme.space2, 0),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    product.name,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w500),
                  ),
                  const SizedBox(height: 4),
                  Row(
                    children: [
                      Expanded(
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(
                              Fmt.money(product.price, product.currency, locale: locale),
                              maxLines: 1,
                              style: TextStyle(
                                color: product.isOnOffer ? AppTheme.danger : AppTheme.accentText,
                                fontSize: 14,
                                fontWeight: FontWeight.w800,
                                fontFeatures: AppTheme.numeric,
                              ),
                            ),
                            if (product.isOnOffer)
                              Text(
                                Fmt.money(product.compareAtPrice, product.currency, locale: locale),
                                maxLines: 1,
                                style: TextStyle(
                                  color: theme.colorScheme.outline,
                                  fontSize: 11.5,
                                  fontWeight: FontWeight.w600,
                                  decoration: TextDecoration.lineThrough,
                                  fontFeatures: AppTheme.numeric,
                                ),
                              ),
                          ],
                        ),
                      ),
                      if (product.acceptsShoppingCredit)
                        Tooltip(
                          message: t.buyWithShoppingCredit,
                          child: const Icon(
                            Icons.card_giftcard_rounded,
                            size: 17,
                            color: AppTheme.gold,
                          ),
                        ),
                    ],
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/*
 * The auctions an administrator picked out for the front page.
 *
 * These used to be squares the size of the category links below them, which made the page's
 * showcase read as just another row of shortcuts. A picked auction now looks picked: it carries a
 * mark saying so, it says what it is doing at this second, and it offers the one action that
 * belongs to that state — bid, when it is running; take a seat, when it has not started.
 *
 * Both states share the card because the row mixes them: whatever an administrator stars lands
 * here, live or not, and the fallback fills the row with both.
 */
class _FeaturedAuctions extends StatefulWidget {
  const _FeaturedAuctions({required this.auctions});

  final List<AuctionSummary> auctions;

  @override
  State<_FeaturedAuctions> createState() => _FeaturedAuctionsState();
}

class _FeaturedAuctionsState extends State<_FeaturedAuctions>
    with SingleTickerProviderStateMixin {
  Timer? _tick;
  late final AnimationController _pulse;

  @override
  void initState() {
    super.initState();

    // The clocks move every second; the instants they count to come from the server.
    _tick = Timer.periodic(const Duration(seconds: 1), (_) {
      if (mounted) setState(() {});
    });

    // One beat for the whole row, lent to whichever cards are live.
    _pulse = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 900),
    )..repeat(reverse: true);
  }

  @override
  void dispose() {
    _tick?.cancel();
    _pulse.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);

    return Container(
      margin: const EdgeInsets.only(top: AppTheme.space1),
      padding: const EdgeInsets.only(bottom: AppTheme.space3),
      color: theme.colorScheme.surface,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _SectionHead(
            icon: Icons.star_rounded,
            title: t.featuredAuctions,
            action: t.seeAllAuctions,
            onTap: () => context.go('/auctions'),
          ),
          SizedBox(
            height: 118,
            child: ListView.separated(
              scrollDirection: Axis.horizontal,
              padding: const EdgeInsets.symmetric(horizontal: AppTheme.space3),
              itemCount: widget.auctions.length,
              separatorBuilder: (_, _) => const SizedBox(width: AppTheme.space2),
              itemBuilder: (_, i) => _FeaturedAuctionTile(
                auction: widget.auctions[i],
                pulse: _pulse,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/*
 * The three figures a card shows about money, and how they are told apart.
 *
 * They are read in this order and never in another: how much is saved, what it costs, what it was
 * worth. Size and colour carry that order on their own, so the ranking survives being glanced at
 * sideways on a bright phone — which is how these are actually seen.
 *
 * They sit on two lines rather than one. On a single line the price could only ever be as large as
 * the narrowest card allowed, and it was squeezed between the two figures whose job is to flatter
 * it. The saving and the old price now share a small line above; the price has the line below to
 * itself, at the size it deserves.
 */
class _Price {
  const _Price._();

  /// The saving, on the upper line beside what it used to cost.
  static const saving = Color(0xFFFF3B4A);
  static const savingSize = 14.0;

  /// What it costs now: alone on its own line, and the largest thing on the card by some way.
  static const now = Color(0xFFFFC93C);
  static const nowSize = 20.0;

  /// What it was worth, crossed out.
  ///
  /// Nearly white rather than a faint grey, because a saving is only believed once the figure it
  /// is measured against can actually be read. The line through it is the saving's own red, so
  /// "crossed out" and "this is what you are not paying" are plainly the same statement.
  static const was = Color(0xFFEDEDED);
  static const wasSize = 11.0;
  static const wasStrike = saving;
}

/// One picked auction: what it is, what it saves, where it has got to, and the way in.
class _FeaturedAuctionTile extends StatelessWidget {
  const _FeaturedAuctionTile({required this.auction, required this.pulse});

  final AuctionSummary auction;
  final Animation<double> pulse;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final locale = Localizations.localeOf(context).languageCode;
    final a = auction;

    final live = a.isLive;

    // Live, the price is where bidding has pushed it. Before the start it can only be the opening
    // price, and that is the biggest discount this auction will ever show.
    final price = live ? a.currentPrice : a.startingPrice;
    final off = _HomeScreenState._discount(price, a.originalPrice);

    final seconds = a.remainingSeconds;
    final left = live
        ? (seconds == null ? null : Duration(seconds: seconds))
        : a.scheduledStartAt?.difference(DateTime.now().toUtc());
    final counting = left != null && !left.isNegative;

    return SizedBox(
      width: 116,
      child: Material(
        color: Colors.transparent,
        child: InkWell(
          borderRadius: BorderRadius.circular(AppTheme.radiusMd),
          onTap: () => context.push('/auctions/${a.id}'),
          child: ClipRRect(
            borderRadius: BorderRadius.circular(AppTheme.radiusMd),
            child: Stack(
              fit: StackFit.expand,
              children: [
                ArtOrPicture(imageUrl: a.imageUrl, seed: a.auctionNumber),

                const Positioned.fill(
                  child: DecoratedBox(
                    decoration: BoxDecoration(
                      gradient: LinearGradient(
                        begin: Alignment.bottomCenter,
                        end: Alignment.topCenter,
                        colors: [Color(0xF50E0B08), Color(0xA00E0B08), Color(0x14000000)],
                        stops: [0, 0.54, 1],
                      ),
                    ),
                  ),
                ),

                // What this auction is doing, and how long it has. A live card's dot beats, which
                // is the only motion on the row and the one piece of state worth noticing first.
                PositionedDirectional(
                  top: 6,
                  start: 6,
                  end: 6,
                  child: Row(
                    children: [
                      Flexible(
                        child: FittedBox(
                          fit: BoxFit.scaleDown,
                          alignment: AlignmentDirectional.centerStart,
                          child: live
                              ? FadeTransition(
                                  opacity: Tween<double>(begin: 1, end: 0.5).animate(pulse),
                                  child: _Chip(
                                    icon: Icons.circle,
                                    label: t.arenaLive,
                                    color: AppTheme.danger,
                                  ),
                                )
                              : a.hasJoined
                                  ? _Chip(
                                      icon: Icons.check_circle_rounded,
                                      label: t.joinedBadge,
                                      color: AppTheme.success,
                                    )
                                  : _Chip(
                                      icon: Icons.schedule_rounded,
                                      label: t.upcomingAuctions,
                                      color: AppTheme.ink,
                                    ),
                        ),
                      ),
                      if (counting) ...[
                        const SizedBox(width: 3),
                        _Chip(
                          icon: Icons.timer_outlined,
                          label: Fmt.duration(left),
                          color: AppTheme.ink,
                          mono: true,
                        ),
                      ],
                    ],
                  ),
                ),

                // The mark that an administrator chose this one, which is the whole reason the row
                // exists. Gold, because nothing else on the card is.
                if (a.isFeatured)
                  PositionedDirectional(
                    bottom: 0,
                    end: 0,
                    child: Container(
                      padding: const EdgeInsets.fromLTRB(7, 3, 5, 3),
                      decoration: const BoxDecoration(
                        color: AppTheme.gold,
                        borderRadius: BorderRadiusDirectional.only(
                          topStart: Radius.circular(8),
                        ),
                      ),
                      child: const Icon(Icons.star_rounded, size: 11, color: AppTheme.onGold),
                    ),
                  ),

                PositionedDirectional(
                  start: 7,
                  end: 7,
                  bottom: 7,
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        a.title.isEmpty ? a.mainProductName : a.title,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: const TextStyle(
                          color: Colors.white,
                          fontSize: 13,
                          fontWeight: FontWeight.w800,
                          shadows: [Shadow(color: Color(0xCC000000), blurRadius: 7)],
                        ),
                      ),
                      const SizedBox(height: 2),

                      // The saving and the old price, small, on one line.
                      if (off >= 0.05 || a.originalPrice > price)
                        FittedBox(
                          fit: BoxFit.scaleDown,
                          alignment: AlignmentDirectional.centerStart,
                          child: Row(
                            textBaseline: TextBaseline.alphabetic,
                            crossAxisAlignment: CrossAxisAlignment.baseline,
                            children: [
                              if (off >= 0.05) ...[
                                Text(
                                  _HomeScreenState._percent(off),
                                  textDirection: TextDirection.ltr,
                                  style: const TextStyle(
                                    color: _Price.saving,
                                    fontSize: _Price.savingSize,
                                    height: 1,
                                    fontWeight: FontWeight.w900,
                                    fontFeatures: AppTheme.numeric,
                                    shadows: [Shadow(color: Color(0xCC000000), blurRadius: 8)],
                                  ),
                                ),
                                const SizedBox(width: 5),
                              ],
                              if (a.originalPrice > price)
                                Text(
                                  Fmt.money(a.originalPrice, a.currency, locale: locale),
                                  style: const TextStyle(
                                    color: _Price.was,
                                    fontSize: _Price.wasSize,
                                    fontWeight: FontWeight.w700,
                                    decoration: TextDecoration.lineThrough,
                                    decorationColor: _Price.wasStrike,
                                    decorationThickness: 2,
                                    fontFeatures: AppTheme.numeric,
                                    shadows: [Shadow(color: Color(0xCC000000), blurRadius: 7)],
                                  ),
                                ),
                            ],
                          ),
                        ),

                      // What it actually costs, with the line to itself.
                      FittedBox(
                        fit: BoxFit.scaleDown,
                        alignment: AlignmentDirectional.centerStart,
                        child: Text(
                          Fmt.money(price, a.currency, locale: locale),
                          style: const TextStyle(
                            color: _Price.now,
                            fontSize: _Price.nowSize,
                            height: 1.1,
                            fontWeight: FontWeight.w900,
                            fontFeatures: AppTheme.numeric,
                            shadows: [Shadow(color: Color(0xCC000000), blurRadius: 8)],
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

/*
 * The auctions that have not started yet.
 *
 * An auction here starts the moment its seats are full, so how full it is *is* the excitement —
 * it is the one number that means "soon", and it is real. Each card therefore leads with the
 * filling bar and the clock rather than with another price, and a card that is nearly full says so
 * loudly, because that is the moment somebody joins rather than deciding to come back later.
 *
 * One ticker and one pulse serve the whole row. Per-card timers on a scrolling list are how a
 * front page starts dropping frames.
 */
class _UpcomingAuctions extends StatefulWidget {
  const _UpcomingAuctions({required this.auctions});

  final List<AuctionSummary> auctions;

  @override
  State<_UpcomingAuctions> createState() => _UpcomingAuctionsState();
}

class _UpcomingAuctionsState extends State<_UpcomingAuctions>
    with SingleTickerProviderStateMixin {
  Timer? _tick;
  late final AnimationController _pulse;

  @override
  void initState() {
    super.initState();

    // The countdowns move every second. The instants they count to come from the server.
    _tick = Timer.periodic(const Duration(seconds: 1), (_) {
      if (mounted) setState(() {});
    });

    _pulse = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 1100),
    )..repeat(reverse: true);
  }

  @override
  void dispose() {
    _tick?.cancel();
    _pulse.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);

    return Container(
      margin: const EdgeInsets.only(top: AppTheme.space1),
      padding: const EdgeInsets.only(bottom: AppTheme.space3),
      color: theme.colorScheme.surface,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _SectionHead(
            icon: Icons.rocket_launch_rounded,
            title: t.upcomingAuctions,
            action: t.seeAllAuctions,
            onTap: () => context.go('/auctions'),
          ),
          SizedBox(
            height: 140,
            child: ListView.separated(
              scrollDirection: Axis.horizontal,
              padding: const EdgeInsets.symmetric(horizontal: AppTheme.space3),
              itemCount: widget.auctions.length,
              separatorBuilder: (_, _) => const SizedBox(width: AppTheme.space2),
              itemBuilder: (_, i) => _UpcomingCard(
                auction: widget.auctions[i],
                pulse: _pulse,
              ),
            ),
          ),
        ],
      ),
    );
  }
}

/// One auction that has not started: the picture, how full it is, and the way in.
class _UpcomingCard extends StatelessWidget {
  const _UpcomingCard({required this.auction, required this.pulse});

  final AuctionSummary auction;
  final Animation<double> pulse;

  /// Where the room stops feeling roomy. Past this the card changes colour and says so.
  static const _urgentFrom = 0.8;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final locale = Localizations.localeOf(context).languageCode;
    final a = auction;

    final needed = a.requiredParticipants <= 0 ? 1 : a.requiredParticipants;
    final filled = (a.participantCount / needed).clamp(0.0, 1.0);
    final left = (needed - a.participantCount).clamp(0, needed);
    final urgent = filled >= _urgentFrom;

    // Before it starts the price can only rise, so the discount at the starting price is the most
    // this auction will ever offer. Saying it now is honest, and it is the largest figure here.
    final off = _HomeScreenState._discount(a.startingPrice, a.originalPrice);

    final startsAt = a.scheduledStartAt;
    final until = startsAt?.difference(DateTime.now().toUtc());
    final counting = until != null && !until.isNegative;

    return SizedBox(
      width: 132,
      child: Material(
        color: Colors.transparent,
        child: InkWell(
          borderRadius: BorderRadius.circular(AppTheme.radiusMd),
          onTap: () => context.push('/auctions/${a.id}'),
          child: ClipRRect(
            borderRadius: BorderRadius.circular(AppTheme.radiusMd),
            child: Stack(
              fit: StackFit.expand,
              children: [
                ArtOrPicture(imageUrl: a.imageUrl, seed: a.auctionNumber),

                // Dark where the words go, clear where the picture is worth seeing.
                const Positioned.fill(
                  child: DecoratedBox(
                    decoration: BoxDecoration(
                      gradient: LinearGradient(
                        begin: Alignment.bottomCenter,
                        end: Alignment.topCenter,
                        colors: [Color(0xF50E0B08), Color(0xA00E0B08), Color(0x14000000)],
                        stops: [0, 0.52, 1],
                      ),
                    ),
                  ),
                ),

                // The top line: the clock to the start, or — when no date is set yet — the thing
                // that actually decides it, which is the seats filling up.
                PositionedDirectional(
                  top: 6,
                  start: 6,
                  end: 6,
                  child: Row(
                    children: [
                      Flexible(
                        child: FittedBox(
                          fit: BoxFit.scaleDown,
                          alignment: AlignmentDirectional.centerStart,
                          // The clock, when there is a start time to count to. When there is
                          // not, nothing: the card says what it is and what it costs, and the
                          // badge beside this speaks up on its own once seats run short.
                          child: counting
                              ? _Chip(
                                  icon: Icons.timer_outlined,
                                  label: Fmt.duration(until),
                                  color: AppTheme.ink,
                                  mono: true,
                                )
                              : const SizedBox.shrink(),
                        ),
                      ),
                      if (urgent) ...[
                        const SizedBox(width: 4),
                        // The one moving thing on the card, and only when it has earned it.
                        FadeTransition(
                          opacity: Tween<double>(begin: 1, end: 0.45).animate(pulse),
                          child: _Chip(
                            icon: Icons.local_fire_department_rounded,
                            label: left <= 0 ? t.almostFull : t.lastSeats,
                            color: AppTheme.danger,
                          ),
                        ),
                      ],
                    ],
                  ),
                ),

                PositionedDirectional(
                  start: 7,
                  end: 7,
                  bottom: 7,
                  child: Column(
                    mainAxisSize: MainAxisSize.min,
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        a.title.isEmpty ? a.mainProductName : a.title,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: const TextStyle(
                          color: Colors.white,
                          fontSize: 13,
                          fontWeight: FontWeight.w800,
                          shadows: [Shadow(color: Color(0xCC000000), blurRadius: 7)],
                        ),
                      ),
                      const SizedBox(height: 2),

                      // The saving and the old price, small, on one line.
                      if (off >= 0.05 || a.originalPrice > a.startingPrice)
                        FittedBox(
                          fit: BoxFit.scaleDown,
                          alignment: AlignmentDirectional.centerStart,
                          child: Row(
                            textBaseline: TextBaseline.alphabetic,
                            crossAxisAlignment: CrossAxisAlignment.baseline,
                            children: [
                              if (off >= 0.05) ...[
                                Text(
                                  _HomeScreenState._percent(off),
                                  textDirection: TextDirection.ltr,
                                  style: const TextStyle(
                                    color: _Price.saving,
                                    fontSize: _Price.savingSize,
                                    height: 1,
                                    fontWeight: FontWeight.w900,
                                    fontFeatures: AppTheme.numeric,
                                    shadows: [Shadow(color: Color(0xCC000000), blurRadius: 8)],
                                  ),
                                ),
                                const SizedBox(width: 5),
                              ],
                              if (a.originalPrice > a.startingPrice)
                                Text(
                                  Fmt.money(a.originalPrice, a.currency, locale: locale),
                                  style: const TextStyle(
                                    color: _Price.was,
                                    fontSize: _Price.wasSize,
                                    fontWeight: FontWeight.w700,
                                    decoration: TextDecoration.lineThrough,
                                    decorationColor: _Price.wasStrike,
                                    decorationThickness: 2,
                                    fontFeatures: AppTheme.numeric,
                                    shadows: [Shadow(color: Color(0xCC000000), blurRadius: 7)],
                                  ),
                                ),
                            ],
                          ),
                        ),

                      // What it actually costs, with the line to itself.
                      FittedBox(
                        fit: BoxFit.scaleDown,
                        alignment: AlignmentDirectional.centerStart,
                        child: Text(
                          Fmt.money(a.startingPrice, a.currency, locale: locale),
                          style: const TextStyle(
                            color: _Price.now,
                            fontSize: _Price.nowSize,
                            height: 1.1,
                            fontWeight: FontWeight.w900,
                            fontFeatures: AppTheme.numeric,
                            shadows: [Shadow(color: Color(0xCC000000), blurRadius: 8)],
                          ),
                        ),
                      ),
                      const SizedBox(height: 6),

                      // The way in. This is the one place on the page where the action is "take a
                      // seat before it is gone", so it gets a real button rather than a hint.
                      SizedBox(
                        width: double.infinity,
                        height: 22,
                        child: DecoratedBox(
                          decoration: BoxDecoration(
                            color: urgent ? AppTheme.danger : AppTheme.accent,
                            borderRadius: BorderRadius.circular(6),
                          ),
                          child: Center(
                            child: Text(
                              t.reserveYourSeat,
                              maxLines: 1,
                              overflow: TextOverflow.ellipsis,
                              style: const TextStyle(
                                color: Colors.white,
                                fontSize: 9.5,
                                fontWeight: FontWeight.w800,
                              ),
                            ),
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

/// A small label on a picture: an icon, a word, and enough dark behind it to stay readable.
class _Chip extends StatelessWidget {
  const _Chip({
    required this.icon,
    required this.label,
    required this.color,
    this.mono = false,
  });

  final IconData icon;
  final String label;
  final Color color;

  /// Figures that change every second are set in tabular numerals so they do not jiggle.
  final bool mono;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 5, vertical: 2),
      decoration: BoxDecoration(
        color: color.withValues(alpha: 0.88),
        borderRadius: BorderRadius.circular(5),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon, size: 9, color: Colors.white),
          const SizedBox(width: 2.5),
          Text(
            label,
            textDirection: mono ? TextDirection.ltr : null,
            style: TextStyle(
              color: Colors.white,
              fontSize: 8.5,
              fontWeight: FontWeight.w800,
              fontFeatures: mono ? AppTheme.numeric : null,
            ),
          ),
        ],
      ),
    );
  }
}

/// The store's departments as wide pictures, each opening that department.
class _CategoryTiles extends StatelessWidget {
  const _CategoryTiles({required this.categories, required this.loading});

  final List<StoreCategory> categories;
  final bool loading;

  @override
  Widget build(BuildContext context) {
    if (!loading && categories.isEmpty) return const SizedBox.shrink();

    final t = AppStrings.of(context);
    final theme = Theme.of(context);

    return Container(
      margin: const EdgeInsets.only(top: AppTheme.space1),
      padding: const EdgeInsets.only(bottom: AppTheme.space2),
      color: theme.colorScheme.surface,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _SectionHead(
            icon: Icons.grid_view_rounded,
            title: t.shopByCategory,
            action: t.seeAllCategories,
            onTap: () => context.go('/store'),
          ),
          SizedBox(
            height: 78,
            child: ListView.separated(
              scrollDirection: Axis.horizontal,
              padding: const EdgeInsets.symmetric(horizontal: AppTheme.space3),
              itemCount: loading ? 4 : categories.length,
              separatorBuilder: (_, _) => const SizedBox(width: AppTheme.space2),
              itemBuilder: (_, i) {
                if (loading) {
                  return const SizedBox(
                    width: 104,
                    child: Skeleton(height: double.infinity, radius: AppTheme.radiusMd),
                  );
                }

                final category = categories[i];
                return SizedBox(
                  width: 104,
                  child: ClipRRect(
                    borderRadius: BorderRadius.circular(AppTheme.radiusMd),
                    child: Stack(
                      fit: StackFit.expand,
                      children: [
                        RemoteImage(category.imageUrl, radius: 0),
                        const Positioned.fill(
                          child: DecoratedBox(
                            decoration: BoxDecoration(
                              gradient: LinearGradient(
                                begin: Alignment.bottomCenter,
                                end: Alignment.topCenter,
                                colors: [Color(0xE60F0A06), Color(0x33000000)],
                              ),
                            ),
                          ),
                        ),
                        Material(
                          color: Colors.transparent,
                          child: InkWell(
                            // The shop's own tab, opened at this department — not a page of its
                            // own above the tabs, which left the shop behind to show a bare grid.
                            onTap: () => context.go('/store?categoryId=${category.id}'),
                          ),
                        ),
                        PositionedDirectional(
                          start: 10,
                          end: 10,
                          bottom: 8,
                          child: Row(
                            children: [
                              Expanded(
                                child: Text(
                                  category.name,
                                  maxLines: 1,
                                  overflow: TextOverflow.ellipsis,
                                  style: const TextStyle(
                                    color: Colors.white,
                                    fontSize: 11.5,
                                    fontWeight: FontWeight.w800,
                                  ),
                                ),
                              ),
                              const Icon(
                                Icons.chevron_left_rounded,
                                color: Colors.white,
                                size: 18,
                              ),
                            ],
                          ),
                        ),
                      ],
                    ),
                  ),
                );
              },
            ),
          ),
        ],
      ),
    );
  }
}

/// The way into the store from the front page.
class _StorePromo extends StatelessWidget {
  const _StorePromo();

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);

    return Padding(
      padding: const EdgeInsets.fromLTRB(
        AppTheme.space3,
        AppTheme.space3,
        AppTheme.space3,
        0,
      ),
      child: Material(
        borderRadius: BorderRadius.circular(AppTheme.radiusLg),
        clipBehavior: Clip.antiAlias,
        color: AppTheme.ink,
        child: InkWell(
          onTap: () => context.go('/store'),
          child: Container(
            decoration: const BoxDecoration(
              gradient: LinearGradient(
                begin: AlignmentDirectional.centerStart,
                end: AlignmentDirectional.centerEnd,
                colors: [AppTheme.ink, Color(0xFF2A1A10)],
              ),
            ),
            padding: const EdgeInsets.all(AppTheme.space3),
            child: Row(
              children: [
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Text(
                        t.storeWorldTitle,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: const TextStyle(
                          color: Colors.white,
                          fontSize: 14.5,
                          fontWeight: FontWeight.w900,
                        ),
                      ),
                      const SizedBox(height: 2),
                      Text(
                        t.storeWorldBody,
                        maxLines: 1,
                        overflow: TextOverflow.ellipsis,
                        style: TextStyle(
                          color: Colors.white.withValues(alpha: 0.8),
                          fontSize: 11.5,
                        ),
                      ),
                      const SizedBox(height: AppTheme.space3),
                      Container(
                        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 7),
                        decoration: BoxDecoration(
                          color: AppTheme.brand,
                          borderRadius: BorderRadius.circular(AppTheme.radiusPill),
                        ),
                        child: Row(
                          mainAxisSize: MainAxisSize.min,
                          children: [
                            Text(
                              t.shopNow,
                              style: const TextStyle(
                                color: Colors.white,
                                fontSize: 12,
                                fontWeight: FontWeight.w800,
                              ),
                            ),
                            const SizedBox(width: 4),
                            const Icon(
                              Icons.chevron_left_rounded,
                              color: Colors.white,
                              size: 18,
                            ),
                          ],
                        ),
                      ),
                    ],
                  ),
                ),
                const SizedBox(width: AppTheme.space3),
                const DealGoMark(height: 44, onDark: true),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// The last few auctions to close, the rest of them one tap away.
class _EndedAuctions extends StatelessWidget {
  const _EndedAuctions({required this.auctions});

  final List<AuctionSummary> auctions;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);

    return Container(
      margin: const EdgeInsets.only(top: AppTheme.space1),
      padding: const EdgeInsets.only(bottom: AppTheme.space3),
      color: Theme.of(context).colorScheme.surface,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _SectionHead(
            icon: Icons.flag_rounded,
            title: t.completedAuctions,
            action: t.seeAll,
            onTap: () => context.go('/auctions?tab=completed'),
          ),
          for (final a in auctions)
            Padding(
              padding: const EdgeInsets.fromLTRB(
                  AppTheme.space3, AppTheme.space1, AppTheme.space3, AppTheme.space2),
              child: EndedAuctionCard(auction: a),
            ),
        ],
      ),
    );
  }
}

/// A section's title with its icon and the link that opens the whole list.
class _SectionHead extends StatelessWidget {
  const _SectionHead({
    required this.icon,
    required this.title,
    required this.action,
    required this.onTap,
  });

  final IconData icon;
  final String title;
  final String action;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Padding(
      padding: const EdgeInsetsDirectional.fromSTEB(AppTheme.space3, 4, AppTheme.space2, 0),
      child: Row(
        children: [
          Icon(icon, size: 15, color: AppTheme.brand),
          const SizedBox(width: 6),
          Expanded(
            child: Text(
              title,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: theme.textTheme.titleMedium?.copyWith(
                fontSize: 13,
                fontWeight: FontWeight.w800,
              ),
            ),
          ),
          TextButton.icon(
            onPressed: onTap,
            label: Text(action),
            icon: const Icon(Icons.chevron_left_rounded, size: 18),
            iconAlignment: IconAlignment.end,
            style: TextButton.styleFrom(visualDensity: VisualDensity.compact),
          ),
        ],
      ),
    );
  }
}
