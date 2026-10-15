import 'dart:async';

import 'package:flutter/widgets.dart';

/// Re-fetches a screen's data on a timer, so auction states, counts and balances change on their
/// own instead of waiting for the user to pull down.
///
/// It only ticks while the screen can actually be seen: the app is in front, the screen's route is
/// the top one, and it is not a hidden tab. A screen in the background costs no requests. Coming
/// back to the app refreshes straight away, since that is when the data is most likely stale.
///
/// Live events over the hub still carry bids the moment they happen; this is the steady refresh for
/// everything else, and the safety net for anything an event missed.
class AutoRefresh extends StatefulWidget {
  const AutoRefresh({
    super.key,
    required this.onRefresh,
    required this.child,
    this.interval = const Duration(seconds: 10),
  });

  final VoidCallback onRefresh;
  final Widget child;
  final Duration interval;

  @override
  State<AutoRefresh> createState() => _AutoRefreshState();
}

class _AutoRefreshState extends State<AutoRefresh> with WidgetsBindingObserver {
  Timer? _timer;
  bool _inForeground = true;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _timer = Timer.periodic(widget.interval, (_) => _tick());
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    final wasInForeground = _inForeground;
    _inForeground = state == AppLifecycleState.resumed;
    if (_inForeground && !wasInForeground) _tick();
  }

  bool get _isVisible {
    if (!_inForeground) return false;
    // Hidden tabs and routes under another one have their tickers switched off.
    if (!TickerMode.valuesOf(context).enabled) return false;
    return ModalRoute.of(context)?.isCurrent ?? true;
  }

  void _tick() {
    if (mounted && _isVisible) widget.onRefresh();
  }

  @override
  void dispose() {
    _timer?.cancel();
    WidgetsBinding.instance.removeObserver(this);
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => widget.child;
}
