import 'package:orblit_net/orblit_net.dart';
import 'package:test/test.dart';

void main() {
  const period = Duration(milliseconds: 50);
  late Duration now;
  late TickClock clock;

  setUp(() {
    now = Duration.zero;
    clock = TickClock(period: period, now: () => now);
  });

  test('the first tick sets the schedule and measures nothing', () {
    expect(clock.untilNext(), Duration.zero);
    clock.begin();
    expect(clock.drift, isNull);
    expect(clock.untilNext(), period);
  });

  test('a late tick reports its drift', () {
    clock.begin();
    now = const Duration(milliseconds: 58);
    expect(clock.untilNext(), Duration.zero);
    clock.begin();
    expect(clock.drift, const Duration(milliseconds: 8));
  });

  test('the next tick keeps its phase after a late one', () {
    clock.begin();
    now = const Duration(milliseconds: 58);
    clock.begin();
    expect(clock.untilNext(), const Duration(milliseconds: 42));
  });

  test('an early call waits for what is left', () {
    clock.begin();
    now = const Duration(milliseconds: 20);
    expect(clock.untilNext(), const Duration(milliseconds: 30));
  });

  test('a stall of five periods runs one tick, not five', () {
    clock.begin();
    now = period * 6;
    clock.begin();
    expect(clock.drift, period * 5);
    expect(clock.untilNext(), period);
  });

  test('two periods behind still catches up in place', () {
    clock.begin();
    now = period * 3;
    clock.begin();
    expect(clock.drift, period * 2);
    expect(clock.untilNext(), Duration.zero);
  });
}
