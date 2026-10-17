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

/// What one client was sent, split by component column.
class ClientBytes {
  const ClientBytes({required this.columns, required this.overhead});

  /// Bytes of component data, keyed by component name.
  final Map<String, int> columns;

  /// Network ids, group headers and the despawn list.
  final int overhead;

  int get total => overhead + columns.values.fold(0, (sum, n) => sum + n);
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
    required this.bytesSent,
  });

  final int ticks;
  final Duration tickAverage;
  final Duration tickMax;
  final Duration driftAverage;
  final Duration driftMax;
  final Map<String, PhaseStats> phases;

  /// Per client, totals since the host started. The window does not apply:
  /// a window would need a map per client per tick.
  final Map<String, ClientBytes> bytesSent;
}

class _Sent {
  final Map<String, int> columns = {};
  int overhead = 0;
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
/// the tick starts, not where it is reported. With no clock, phases are still
/// timed and there is no drift or overrun to report.
class PhaseTimes {
  PhaseTimes({TickClock? clock, this.onOverrun, this.capacity = 300})
    : _clock = clock,
      _now = clock?.now ?? monotonicNow();

  /// Quiet time between calls of [onOverrun], so one stall does not log a
  /// line for every late tick behind it.
  static const Duration overrunEvery = Duration(seconds: 5);

  final TickClock? _clock;
  final Duration Function() _now;
  final void Function(PhaseSummary summary)? onOverrun;
  final int capacity;

  final Map<String, _Sent> _sent = {};
  final List<_Tick> _ring = [];
  _Tick? _open;
  Duration? _lastOverrun;

  /// Runs [body], adds its time to the open tick under [name], and returns
  /// what it returned. Run twice in a tick, a name adds up.
  T phase<T>(String name, T Function() body) {
    final tick = _open ??= _begin();
    final start = _now();
    try {
      return body();
    } finally {
      final took = _now() - start;
      tick.phases[name] = (tick.phases[name] ?? Duration.zero) + took;
    }
  }

  /// Closes the open tick. Calls [onOverrun] when it ran past the period.
  void finish() {
    final tick = _open ??= _begin();
    _open = null;
    tick.length = _now() - tick.start;
    _ring.add(tick);
    if (_ring.length > capacity) _ring.removeAt(0);
    final period = _clock?.period;
    if (period != null && tick.length > period) _reportOverrun();
  }

  /// Counts [bytes] sent to [client] under component [column]. A null column
  /// is the rest of the message: network ids, headers and the despawn list.
  void countSent(String client, String? column, int bytes) {
    final sent = _sent[client] ??= _Sent();
    if (column == null) {
      sent.overhead += bytes;
    } else {
      sent.columns[column] = (sent.columns[column] ?? 0) + bytes;
    }
  }

  void forgetClient(String client) => _sent.remove(client);

  PhaseSummary summary({int last = 30}) {
    final window = _ring.length > last
        ? _ring.sublist(_ring.length - last)
        : _ring;
    return _summarise(window, _sent);
  }

  _Tick _begin() {
    final clock = _clock;
    clock?.begin();
    return _Tick(_now(), clock?.drift);
  }

  void _reportOverrun() {
    final hook = onOverrun;
    if (hook == null) return;
    final at = _now();
    final last = _lastOverrun;
    if (last != null && at - last < overrunEvery) return;
    _lastOverrun = at;
    hook(summary());
  }

  static PhaseSummary _summarise(List<_Tick> window, Map<String, _Sent> sent) {
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
        final seen = maxes[entry.key];
        if (seen == null || entry.value > seen) maxes[entry.key] = entry.value;
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
            max: maxes[entry.key] ?? Duration.zero,
          ),
      },
      bytesSent: {
        for (final entry in sent.entries)
          entry.key: ClientBytes(
            columns: Map.unmodifiable(entry.value.columns),
            overhead: entry.value.overhead,
          ),
      },
    );
  }
}
