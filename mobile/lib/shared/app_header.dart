import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../core/localization/app_strings.dart';
import '../core/providers.dart';
import '../core/theme/app_theme.dart';
import 'brand.dart';

/// The bar at the top of every tab: the menu, the mark, the notifications, the basket and the
/// search box.
///
/// It lives in the shell rather than in each page, so it is drawn once and stays put while the
/// page under it scrolls — the same bar, in the same place, wherever the person is. Its picture
/// fills the space behind the phone's own status bar too, so the clock and the battery sit on the
/// brand rather than on whatever the page happens to show.
class AppHeader extends ConsumerWidget {
  const AppHeader({super.key});

  /*
   * The bar is a picture, not a colour. The two shades below are still declared because they are
   * what is drawn underneath it: while the picture is being decoded, and in the moment before it
   * is, the bar must already be dark, or the white mark and the white icons would flash against
   * a blank background.
   */
  static const dark = Color(0xFF12243F);
  static const darkDeep = Color(0xFF0A1729);

  static const image = AssetImage('assets/images/header.png');

  /// The bar's background, shared with every page opened above the tabs so they all match.
  static const decoration = BoxDecoration(
    color: darkDeep,
    gradient: LinearGradient(
      begin: AlignmentDirectional.topStart,
      end: AlignmentDirectional.bottomEnd,
      colors: [darkDeep, dark],
    ),
    image: DecorationImage(image: image, fit: BoxFit.cover),
  );

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);
    final unread = ref.watch(unreadCountProvider).valueOrNull ?? 0;
    final inCart = ref.watch(cartProvider).fold<int>(0, (n, l) => n + l.quantity);

    return AnnotatedRegion<SystemUiOverlayStyle>(
      /*
       * The bar is dark, so the clock and the battery above it are drawn light. This is declared
       * on the bar itself rather than once at start-up: a page pushed over the tabs carries its
       * own bar and its own style, and when it is closed this one must come back — which it does,
       * because whatever is drawn on top decides.
       */
      value: _overlay,
      child: DecoratedBox(
      decoration: decoration,
      child: SafeArea(
        bottom: false,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(
            AppTheme.space2,
            2,
            AppTheme.space2,
            AppTheme.space2,
          ),
          child: Column(
            children: [
              Row(
                children: [
                  _HeaderIcon(
                    icon: Icons.menu_rounded,
                    tooltip: t.menu,
                    count: 0,
                    size: 24,
                    onTap: () => openAppMenu(context),
                  ),
                  const Expanded(
                    child: Center(
                      child: Row(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          DealGoMark(height: 26, onDark: true),
                          SizedBox(width: 6),
                          DealGoWordmark(fontSize: 18, onDark: true),
                        ],
                      ),
                    ),
                  ),
                  _HeaderIcon(
                    icon: Icons.notifications_none_rounded,
                    tooltip: t.notifications,
                    count: unread,
                    size: 24,
                    onTap: () => context.push('/notifications'),
                  ),
                  _HeaderIcon(
                    icon: Icons.shopping_bag_outlined,
                    tooltip: t.cart,
                    count: inCart,
                    size: 24,
                    showCount: true,
                    onTap: () => context.push('/store/cart'),
                  ),
                ],
              ),
              const SizedBox(height: 4),
              // Held in from the edges, so it reads as a box on the bar rather than a second bar.
              const Padding(
                padding: EdgeInsets.symmetric(horizontal: AppTheme.space2),
                child: _SearchRow(),
              ),
            ],
          ),
        ),
      ),
      ),
    );
  }
}

/// The same picture, on the bar of a page opened above the tabs.
///
/// Those pages — the basket, the notifications, an auction, a product — replace the shell for as
/// long as they are open, so without this the top of the screen would turn white halfway through
/// a journey and the phone's own clock would be drawn dark on a dark picture.
class BrandAppBar extends StatelessWidget implements PreferredSizeWidget {
  const BrandAppBar({super.key, this.title, this.actions, this.bottom});

  final Widget? title;
  final List<Widget>? actions;

  /// Tabs or a search box drawn under the title, as on any other bar.
  final PreferredSizeWidget? bottom;

  @override
  Size get preferredSize =>
      Size.fromHeight(kToolbarHeight + (bottom?.preferredSize.height ?? 0));

  @override
  Widget build(BuildContext context) {
    // Exactly the main page's background — the same gradient with the same picture over it —
    // drawn behind a see-through bar. The bar's own picture slot left it a flat dark colour.
    return DecoratedBox(
      decoration: AppHeader.decoration,
      child: AppBar(
      title: title,
      actions: actions,
      bottom: bottom,
      elevation: 0,
      scrolledUnderElevation: 0,
      systemOverlayStyle: _overlay,
      backgroundColor: Colors.transparent,
      surfaceTintColor: Colors.transparent,
      shape: const Border(),
      foregroundColor: Colors.white,
      iconTheme: const IconThemeData(color: Colors.white),
      actionsIconTheme: const IconThemeData(color: Colors.white),
      titleTextStyle: const TextStyle(
        color: Colors.white,
        fontSize: 17,
        fontWeight: FontWeight.w800,
      ),
      ),
    );
  }
}

/// White clock, white battery: what every dark bar in this app asks for.
const _overlay = SystemUiOverlayStyle(
  statusBarBrightness: Brightness.dark,
  statusBarIconBrightness: Brightness.light,
  statusBarColor: Colors.transparent,
);

/// Everywhere in the app that is not one of the five tabs along the bottom.
///
/// The list scrolls inside a sheet no taller than most of the screen. It is seven lines on a
/// large phone and the same seven lines on a small one with large text, where a fixed column
/// would run off the bottom.
void openAppMenu(BuildContext context) {
  final t = AppStrings.of(context);

  showModalBottomSheet<void>(
    context: context,
    showDragHandle: true,
    isScrollControlled: true,
    constraints: BoxConstraints(maxHeight: MediaQuery.sizeOf(context).height * 0.8),
    builder: (sheet) {
      Widget item(IconData icon, String label, VoidCallback go) => ListTile(
        leading: Icon(icon, color: AppTheme.brand),
        title: Text(label),
        onTap: () {
          Navigator.of(sheet).pop();
          go();
        },
      );

      return SafeArea(
        child: ListView(
          shrinkWrap: true,
          children: [
            item(Icons.gavel_rounded, t.auctions, () => context.go('/auctions')),
            item(Icons.storefront_rounded, t.store, () => context.go('/store')),
            item(Icons.account_balance_wallet_rounded, t.wallet, () => context.go('/wallet')),
            item(Icons.emoji_events_rounded, t.myPrizes, () => context.push('/prizes')),
            item(Icons.receipt_long_rounded, t.myOrders, () => context.push('/store/orders')),
            item(
              Icons.notifications_none_rounded,
              t.notifications,
              () => context.push('/notifications'),
            ),
            item(Icons.person_rounded, t.profile, () => context.go('/profile')),
          ],
        ),
      );
    },
  );
}

/// The search box as a bar's bottom, for the store pages that open above the tabs.
///
/// Those pages replace the shell while they are open, so without this a shop loses its search the
/// moment somebody opens anything in it — which is exactly when they are most likely to want it.
class HeaderSearchBar extends StatelessWidget implements PreferredSizeWidget {
  const HeaderSearchBar({super.key});

  static const _height = 44.0;

  @override
  Size get preferredSize => const Size.fromHeight(_height);

  @override
  Widget build(BuildContext context) {
    // No colour of its own: the bar's picture shows through, as it does on the main page.
    return Padding(
      padding: const EdgeInsets.fromLTRB(
        AppTheme.space4, 0, AppTheme.space4, AppTheme.space2),
      child: const _SearchRow(),
    );
  }
}

class _SearchRow extends ConsumerStatefulWidget {
  const _SearchRow();

  @override
  ConsumerState<_SearchRow> createState() => _SearchRowState();
}

class _SearchRowState extends ConsumerState<_SearchRow> {
  final _query = TextEditingController();

  @override
  void dispose() {
    _query.dispose();
    super.dispose();
  }

  void _search() {
    final q = _query.text.trim();
    FocusScope.of(context).unfocus();

    /*
     * An empty box is not a search.
     *
     * It used to push a bare list of everything: a grid above the tabs, with a back arrow and
     * none of the shop around it — no departments, no banner, no offers. Somebody who tapped the
     * magnifier without typing landed in what looked like a different application. Now it simply
     * opens the shop, which is what they were asking for.
     */
    if (q.isEmpty) {
      context.go('/store');
      return;
    }

    context.push('/store/products?q=${Uri.encodeQueryComponent(q)}');
  }

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);

    return Container(
      height: 32,
      padding: const EdgeInsetsDirectional.only(start: 10, end: 3),
      decoration: BoxDecoration(color: Colors.white, borderRadius: BorderRadius.circular(9)),
      child: Row(
        children: [
          Expanded(
            child: TextField(
              controller: _query,
              textInputAction: TextInputAction.search,
              textAlignVertical: TextAlignVertical.center,
              onSubmitted: (_) => _search(),
              cursorColor: AppTheme.brand,
              style: const TextStyle(color: AppTheme.ink, fontSize: 14),
              // Every border is cleared explicitly: the app theme draws outlined fields, and this
              // one sits inside its own white pill.
              decoration: InputDecoration(
                isCollapsed: true,
                filled: false,
                border: InputBorder.none,
                enabledBorder: InputBorder.none,
                focusedBorder: InputBorder.none,
                hintText: t.searchProducts,
                hintStyle: const TextStyle(color: Color(0xFF7A808A), fontSize: 14),
              ),
            ),
          ),
          Material(
            color: AppHeader.darkDeep,
            borderRadius: BorderRadius.circular(7),
            child: InkWell(
              borderRadius: BorderRadius.circular(7),
              onTap: _search,
              child: const SizedBox(
                width: 34,
                height: 26,
                child: Icon(Icons.search_rounded, color: Colors.white, size: 17),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _HeaderIcon extends StatelessWidget {
  const _HeaderIcon({
    required this.icon,
    required this.tooltip,
    required this.count,
    required this.onTap,
    this.showCount = false,
    this.size = 27,
  });

  final IconData icon;
  final String tooltip;
  final int count;

  /// A number on the badge; otherwise just a dot that says there is something new.
  final bool showCount;
  final VoidCallback onTap;
  final double size;

  @override
  Widget build(BuildContext context) {
    final glyph = Icon(icon, color: Colors.white, size: size);

    return IconButton(
      tooltip: tooltip,
      onPressed: onTap,
      visualDensity: VisualDensity.compact,
      constraints: const BoxConstraints(minWidth: AppTheme.tapTarget, minHeight: AppTheme.tapTarget),
      icon: showCount
          ? Badge.count(count: count, isLabelVisible: count > 0, child: glyph)
          : Badge(isLabelVisible: count > 0, smallSize: 8, child: glyph),
    );
  }
}
