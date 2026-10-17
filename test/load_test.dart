import 'dart:io';

import 'package:orblit_net/load.dart';
import 'package:test/test.dart';

LoadOptions options({
  int clients = 8,
  int ticks = 10,
  Duration period = const Duration(milliseconds: 20),
  String script = 'walk',
  bool sockets = false,
  void Function(int tick)? system,
}) => LoadOptions(
  clients: clients,
  ticks: ticks,
  period: period,
  script: loadScripts[script]!,
  sockets: sockets,
  system: system,
);

void main() {
  test(
    'every client is sent bytes in the Position column under walk',
    () async {
      final report = await runLoad(options());

      final sent = report.summary.bytesSent;
      expect(sent, hasLength(8));
      for (final bytes in sent.values) {
        expect(bytes.columns['Position'], greaterThan(0));
      }
    },
  );

  test('inputs from every client are applied and none refused', () async {
    final report = await runLoad(options());

    expect(report.accepted, greaterThan(0));
    expect(report.rejected, 0);
  });

  test('over real sockets every client is sent bytes and heard', () async {
    final report = await runLoad(options(clients: 4, sockets: true));

    expect(report.summary.bytesSent, hasLength(4));
    for (final bytes in report.summary.bytesSent.values) {
      expect(bytes.columns['Position'], greaterThan(0));
    }
    expect(report.accepted, greaterThan(0));
    expect(report.rejected, 0);
  });

  test('still sends less than walk', () async {
    final walk = await runLoad(options());
    final still = await runLoad(options(script: 'still'));

    int total(LoadReport report) =>
        report.summary.bytesSent.values.fold(0, (sum, b) => sum + b.total);
    expect(total(still), lessThan(total(walk)));
  });

  test('a slow system is the phase with the largest average', () async {
    final report = await runLoad(
      options(system: (_) => sleep(const Duration(milliseconds: 5))),
    );

    final phases = report.summary.phases;
    final worst = phases.entries.reduce(
      (a, b) => a.value.average >= b.value.average ? a : b,
    );
    expect(worst.key, 'system');
  });

  test('a period the host cannot keep is reported as an overrun', () async {
    final slow = await runLoad(
      options(
        period: const Duration(milliseconds: 2),
        system: (_) => sleep(const Duration(milliseconds: 6)),
      ),
    );
    final easy = await runLoad(
      options(period: const Duration(milliseconds: 100), ticks: 4),
    );

    expect(slow.overran, isTrue);
    expect(easy.overran, isFalse);
  });

  test('describe names the worst phase and the bytes per client', () async {
    final report = await runLoad(
      options(system: (_) => sleep(const Duration(milliseconds: 3))),
    );

    final text = report.describe();
    expect(text, contains('system'));
    expect(text, contains('Position'));
    expect(text, contains('8 clients'));
  });
}
