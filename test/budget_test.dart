import 'package:orblit_net/src/budget.dart';
import 'package:test/test.dart';

void main() {
  final budget = InboundBudget(
    messagesPerTick: 3,
    bytesPerMessage: 10,
    bytesPerTick: 20,
    strikes: 2,
  );

  test('a message over bytesPerMessage is refused on its own', () {
    final meter = BudgetMeter();
    expect(meter.allows(budget, 10), isTrue);
    expect(meter.allows(budget, 11), isFalse);
  });

  test('messagesPerTick refuses the message after the last allowed', () {
    final meter = BudgetMeter();
    for (var i = 0; i < 3; i++) {
      expect(meter.allows(budget, 1), isTrue);
      meter.count(1);
    }
    expect(meter.allows(budget, 1), isFalse);
  });

  test('bytesPerTick refuses what would pass the total', () {
    final meter = BudgetMeter()
      ..count(10)
      ..count(8);
    expect(meter.allows(budget, 2), isTrue);
    expect(meter.allows(budget, 3), isFalse);
  });

  test('endTick starts a new window', () {
    final meter = BudgetMeter();
    for (var i = 0; i < 3; i++) {
      meter.count(1);
    }
    meter.endTick();
    expect(meter.allows(budget, 1), isTrue);
  });

  test('a refused tick adds a strike and a clean tick forgives one', () {
    final meter = BudgetMeter();
    meter
      ..refuse()
      ..endTick();
    expect(meter.isOver(budget), isFalse);

    meter.endTick();
    meter
      ..refuse()
      ..endTick();
    expect(meter.isOver(budget), isFalse);

    meter
      ..refuse()
      ..endTick();
    expect(meter.isOver(budget), isTrue);
  });

  test('forgiving a clean tick stops at zero', () {
    final meter = BudgetMeter();
    for (var i = 0; i < 5; i++) {
      meter.endTick();
    }
    meter
      ..refuse()
      ..endTick();
    expect(meter.isOver(budget), isFalse);
  });

  test('a limit under one is refused', () {
    expect(() => InboundBudget(messagesPerTick: 0), throwsArgumentError);
    expect(() => InboundBudget(bytesPerMessage: 0), throwsArgumentError);
    expect(() => InboundBudget(bytesPerTick: 0), throwsArgumentError);
    expect(() => InboundBudget(strikes: 0), throwsArgumentError);
  });
}
