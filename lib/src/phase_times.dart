import 'tick_clock.dart';

/// How long one named phase took across a window of ticks.
class PhaseStats {
  const PhaseStats({
    required this.total,
    required this.average,
    required this.max,
  });

  final Duration total;

  /// Per tick in the window, so the phases add up to the tick average.
  final Duration average;

  /// The most it took in a single tick.
  final Duration max;
}

/// Where the time went over the last few ticks.
class PhaseSummary {
  const PhaseSummary({
    required this.ticks,
    required this.tickAverage,
    required this.tickMax,
    required this.driftAverage,
    required this.driftMax,
    required this.phases,
  });

  final int ticks;
  final Duration tickAverage;
  final Duration tickMax;
  final Duration driftAverage;
  final Duration driftMax;
  final Map<String, PhaseStats> phases;
}

class _Tick {
  _Tick(this.start, this.drift);

  final Duration start;
  final Duration? drift;
  final Map<String, Duration> phases = {};
  Duration length = Duration.zero;
}

/// Times the named parts of each tick and keeps the last few ticks.
///
/// A tick opens at the first [phase] after the previous one closed, and closes
/// at [finish]. Opening asks the clock to begin, so drift is measured where
/// the tick starts, not where it is reported.
class PhaseTimes {
  PhaseTimes({required TickClock clock, this.onOverrun, this.capacity = 300})
    : _clock = clock;

  /// Quiet time between calls of [onOverrun], so one stall does not log a
  /// line for every late tick behind it.
  static const Duration overrunEvery = Duration(seconds: 5);

  final TickClock _clock;
  final void Function(PhaseSummary summary)? onOverrun;
  final int capacity;

  final List<_Tick> _ring = [];
  _Tick? _open;
  Duration? _lastOverrun;

  /// Runs [body], adds its time to the open tick under [name], and returns
  /// what it returned. Run twice in a tick, a name adds up.
  T phase<T>(String name, T Function() body) {
    final tick = _open ??= _begin();
    final start = _clock.now();
    try {
      return body();
    } finally {
      final took = _clock.now() - start;
      tick.phases[name] = (tick.phases[name] ?? Duration.zero) + took;
    }
  }

  /// Closes the open tick. Calls [onOverrun] when it ran past the period.
  void finish() {
    final tick = _open ??= _begin();
    _open = null;
    tick.length = _clock.now() - tick.start;
    _ring.add(tick);
    if (_ring.length > capacity) _ring.removeAt(0);
    if (tick.length > _clock.period) _reportOverrun();
  }

  PhaseSummary summary({int last = 30}) {
    final window = _ring.length > last
        ? _ring.sublist(_ring.length - last)
        : _ring;
    return _summarise(window);
  }

  _Tick _begin() {
    _clock.begin();
    return _Tick(_clock.now(), _clock.drift);
  }

  void _reportOverrun() {
    final hook = onOverrun;
    if (hook == null) return;
    final at = _clock.now();
    final last = _lastOverrun;
    if (last != null && at - last < overrunEvery) return;
    _lastOverrun = at;
    hook(summary());
  }

  static PhaseSummary _summarise(List<_Tick> window) {
    final count = window.length;
    final totals = <String, Duration>{};
    final maxes = <String, Duration>{};
    var lengthSum = Duration.zero;
    var lengthMax = Duration.zero;
    var driftSum = Duration.zero;
    var driftMax = Duration.zero;
    var drifted = 0;

    for (final tick in window) {
      lengthSum += tick.length;
      if (tick.length > lengthMax) lengthMax = tick.length;
      final drift = tick.drift;
      if (drift != null) {
        drifted++;
        driftSum += drift;
        if (drift > driftMax) driftMax = drift;
      }
      for (final entry in tick.phases.entries) {
        totals[entry.key] = (totals[entry.key] ?? Duration.zero) + entry.value;
        final seen = maxes[entry.key] ?? Duration.zero;
        if (entry.value > seen) maxes[entry.key] = entry.value;
      }
    }

    return PhaseSummary(
      ticks: count,
      tickAverage: count == 0 ? Duration.zero : lengthSum ~/ count,
      tickMax: lengthMax,
      driftAverage: drifted == 0 ? Duration.zero : driftSum ~/ drifted,
      driftMax: driftMax,
      phases: {
        for (final entry in totals.entries)
          entry.key: PhaseStats(
            total: entry.value,
            average: entry.value ~/ count,
            max: maxes[entry.key]!,
          ),
      },
    );
  }
}
