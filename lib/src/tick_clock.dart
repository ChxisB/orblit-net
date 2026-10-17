/// A fixed-rate schedule that measures how late each tick starts.
///
/// The next tick is due one period after the last one was due, not one
/// period after it ran, so a late tick does not shift the ones behind it.
/// When the clock falls more than two periods behind, it restarts from now
/// instead of running ticks back to back, which would stall the host further.
class TickClock {
  /// [now] must be monotonic. A wall clock that resyncs reads as lag that
  /// never happened.
  TickClock({required this.period, Duration Function()? now})
    : now = now ?? monotonicNow();

  final Duration period;
  final Duration Function() now;

  Duration? _due;
  Duration? _drift;

  /// How late the last tick started. Null until the second tick, since the
  /// first one only sets the schedule.
  Duration? get drift => _drift;

  /// How long until a tick is due. Zero when one is due now.
  Duration untilNext() {
    final due = _due;
    if (due == null) return Duration.zero;
    final left = due - now();
    return left.isNegative ? Duration.zero : left;
  }

  /// Starts a tick.
  void begin() {
    final current = now();
    final due = _due;
    if (due == null) {
      _due = current + period;
      return;
    }
    final behind = current - due;
    _drift = behind;
    _due = behind > period * 2 ? current + period : due + period;
  }
}

/// Time since this call, read off a [Stopwatch] so it never goes backwards.
Duration Function() monotonicNow() {
  final watch = Stopwatch()..start();
  return () => watch.elapsed;
}
