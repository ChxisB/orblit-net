import 'package:orblit_net/orblit_net.dart';
import 'package:orblit_net/src/phase_times.dart';
import 'package:test/test.dart';

Duration ms(int n) => Duration(milliseconds: n);

void main() {
  const period = Duration(milliseconds: 50);
  late Duration now;
  late TickClock clock;
  late List<PhaseSummary> overruns;
  late PhaseTimes times;

  /// Runs one tick whose phases take the given milliseconds, then closes it.
  void tick(Map<String, int> phases, {int idle = 0}) {
    for (final entry in phases.entries) {
      times.phase(entry.key, () => now += ms(entry.value));
    }
    now += ms(idle);
    times.finish();
  }

  setUp(() {
    now = Duration.zero;
    clock = TickClock(period: period, now: () => now);
    overruns = [];
    times = PhaseTimes(clock: clock, onOverrun: overruns.add, capacity: 3);
  });

  test('summarises tick length and each phase', () {
    tick({'capture': 2, 'encode': 4});
    now += ms(44);
    tick({'capture': 4, 'encode': 8});

    final summary = times.summary();
    expect(summary.ticks, 2);
    expect(summary.tickAverage, ms(9));
    expect(summary.tickMax, ms(12));

    final capture = summary.phases['capture']!;
    expect(capture.total, ms(6));
    expect(capture.average, ms(3));
    expect(capture.max, ms(4));
    expect(summary.phases['encode']!.total, ms(12));
  });

  test('a phase run twice in one tick adds up', () {
    times.phase('encode', () => now += ms(3));
    times.phase('encode', () => now += ms(5));
    times.finish();

    final encode = times.summary().phases['encode']!;
    expect(encode.total, ms(8));
    expect(encode.max, ms(8));
  });

  test('a phase that takes no measurable time is still listed', () {
    times.phase('instant', () {});
    times.finish();

    expect(times.summary().phases['instant']!.max, Duration.zero);
  });

  test('returns what the body returns', () {
    expect(times.phase('x', () => 7), 7);
  });

  test('reports drift, which the first tick does not have', () {
    tick({'a': 1});
    now = ms(58);
    tick({'a': 1});

    final summary = times.summary();
    expect(summary.driftAverage, ms(8));
    expect(summary.driftMax, ms(8));
  });

  test('keeps only the last capacity ticks', () {
    for (var i = 1; i <= 5; i++) {
      tick({'a': i});
      now += ms(50 - i);
    }
    final summary = times.summary();
    expect(summary.ticks, 3);
    expect(summary.phases['a']!.total, ms(3 + 4 + 5));
  });

  test('last limits the window', () {
    for (var i = 1; i <= 3; i++) {
      tick({'a': i});
      now += ms(50 - i);
    }
    final summary = times.summary(last: 1);
    expect(summary.ticks, 1);
    expect(summary.tickMax, ms(3));
  });

  test('a tick over the period calls the hook once in five seconds', () {
    tick({'slow': 60});
    expect(overruns, hasLength(1));
    expect(overruns.single.phases['slow']!.max, ms(60));

    now += ms(40);
    tick({'slow': 60});
    expect(overruns, hasLength(1));

    now += const Duration(seconds: 5);
    tick({'slow': 60});
    expect(overruns, hasLength(2));
  });

  test('a tick inside the period does not call the hook', () {
    tick({'a': 49});
    expect(overruns, isEmpty);
  });
}
