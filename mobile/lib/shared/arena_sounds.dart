import 'dart:async';

import 'package:audioplayers/audioplayers.dart';
import 'package:flutter/foundation.dart';

/// The three sounds the auction makes.
enum ArenaSound {
  /// The auction opens: a rising fanfare.
  start('sounds/auction_start.wav'),

  /// The final phase begins: an alarm that does not let go.
  finalPhase('sounds/final_phase.wav'),

  /// A bid lands: one short, bright click.
  bid('sounds/bid.wav');

  const ArenaSound(this.asset);

  /// Relative to `assets/`, which is where audioplayers looks.
  final String asset;
}

/// Plays the arena's sounds.
///
/// Sound is decoration: a phone on silent, a browser that will not play before the person has
/// touched the page, a missing file — none of these may interrupt an auction. So every call is
/// wrapped, failures are swallowed after the first complaint in debug, and the players are held
/// open so the alarm starts on the beat rather than after a load.
class ArenaSounds {
  ArenaSounds._();

  static final ArenaSounds instance = ArenaSounds._();

  final _players = <ArenaSound, AudioPlayer>{};
  bool _muted = false;

  /// Silences everything. The person's own choice, kept for the life of the app.
  bool get muted => _muted;

  set muted(bool value) {
    _muted = value;
    if (value) stopAll();
  }

  Future<void> play(ArenaSound sound, {double volume = 1.0}) async {
    if (_muted) return;

    try {
      final player = _players.putIfAbsent(sound, () {
        final p = AudioPlayer()..setReleaseMode(ReleaseMode.stop);
        // Sound should duck music rather than take over the phone.
        unawaited(p.setPlayerMode(PlayerMode.lowLatency));
        return p;
      });

      await player.stop();
      await player.setVolume(volume);
      await player.play(AssetSource(sound.asset));
    } catch (error, stack) {
      // Never let a speaker stop an auction.
      if (kDebugMode) {
        debugPrint('Arena sound ${sound.name} failed: $error\n$stack');
      }
    }
  }

  Future<void> stopAll() async {
    for (final player in _players.values) {
      try {
        await player.stop();
      } catch (_) {
        // Already stopped, or never started.
      }
    }
  }

  Future<void> dispose() async {
    for (final player in _players.values) {
      try {
        await player.dispose();
      } catch (_) {
        // Nothing to do: the platform has already let it go.
      }
    }
    _players.clear();
  }
}
