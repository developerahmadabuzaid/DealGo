import 'package:audioplayers/audioplayers.dart';
import 'package:flutter/foundation.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// What the live bidding screen is playing.
enum AuctionTrack {
  /// The auction is running: a steady one-bar loop.
  live('audio/auction_live.wav'),

  /// The final phase (فترة الحسم): one bar from the piece's climb to its climax, which builds
  /// up again on every repeat.
  finalPhase('audio/auction_final.wav');

  const AuctionTrack(this.asset);

  /// Relative to `assets/`, which is where audioplayers looks.
  final String asset;
}

/// The music behind live bidding.
///
/// Both loops were cut on the piece's bar lines (120 BPM, one bar every two seconds), so they
/// repeat without a seam, and they are WAV because compressed formats pad the start and end with
/// silence that would be heard as a gap on every repeat.
///
/// Music is decoration. A phone on silent, a browser that will not play before the page has been
/// touched, a missing file: none of these may get in the way of a bid, so every call is wrapped
/// and a failure is only reported in debug.
class AuctionMusic {
  AuctionMusic._();

  static final AuctionMusic instance = AuctionMusic._();

  static const _mutedKey = 'auction_music_muted';

  AudioPlayer? _player;
  AuctionTrack? _track;
  bool _paused = false;

  /// Bumped by every start and every stop. Starting a track takes several awaits; a start that
  /// finds the number changed under it has been overtaken — the screen closed, the person muted,
  /// a newer track was asked for — and must not leave its sound playing.
  int _generation = 0;

  /// The person's own choice, kept on the device.
  final ValueNotifier<bool> muted = ValueNotifier(false);

  bool _loaded = false;

  Future<void> _loadPreference() async {
    if (_loaded) return;
    _loaded = true;
    try {
      final prefs = await SharedPreferences.getInstance();
      muted.value = prefs.getBool(_mutedKey) ?? false;
    } catch (_) {
      // No stored choice: music on.
    }
  }

  Future<void> setMuted(bool value) async {
    muted.value = value;

    // The sound answers the switch at once; remembering it can come after.
    if (value) {
      await _silence();
    } else if (_track != null && !_paused) {
      await _start(_track!);
    }

    try {
      final prefs = await SharedPreferences.getInstance();
      await prefs.setBool(_mutedKey, value);
    } catch (_) {
      // Not remembered this time; the switch still works for now.
    }
  }

  /// Plays [track], or stops when it is null. Asking for what is already playing does nothing,
  /// so this can be called on every rebuild.
  Future<void> play(AuctionTrack? track) async {
    if (track == _track) return;
    _track = track;

    if (track == null) {
      await _silence();
      return;
    }

    await _loadPreference();
    // Asked for something else while the preference was being read.
    if (_track != track || muted.value || _paused) return;
    await _start(track);
  }

  /// The app went to the background: stop the sound but remember what was playing.
  Future<void> pause() async {
    _paused = true;
    await _silence();
  }

  /// Back in front: carry on with whatever should be playing now.
  Future<void> resume() async {
    if (!_paused) return;
    _paused = false;
    if (_track != null && !muted.value) await _start(_track!);
  }

  /// Leaves the screen: silence, and forget the track.
  Future<void> stop() async {
    _track = null;
    _paused = false;
    await _silence();
  }

  Future<void> _silence() async {
    _generation++;
    await _guard(() async => _player?.stop());
  }

  Future<void> _start(AuctionTrack track) async {
    final generation = ++_generation;
    bool overtaken() => generation != _generation;

    await _guard(() async {
      final player = _player ??= AudioPlayer();
      await player.setReleaseMode(ReleaseMode.loop);
      await player.stop();
      if (overtaken()) return;
      await player.setVolume(0.6);
      if (overtaken()) return;
      await player.play(AssetSource(track.asset));

      // Stopped while the track was loading: the stop ran first, so undo this late start. When
      // a newer start overtook this one instead, that start owns the player and is left alone.
      final shouldPlay = _track != null && !muted.value && !_paused;
      if (overtaken() && !shouldPlay) await player.stop();
    });
  }

  Future<void> _guard(Future<void> Function() action) async {
    try {
      await action();
    } catch (error, stack) {
      if (kDebugMode) debugPrint('Auction music failed: $error\n$stack');
    }
  }
}
