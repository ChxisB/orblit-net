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

  test('every input a client sends before the last tick is applied', () async {
    final report = await runLoad(options());

    // Inputs sent on the last tick are delivered after the report is built.
    expect(report.accepted, 8 * 9);
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

  test(
    'describe lists the worst phase first and the bytes per client',
    () async {
      final report = await runLoad(
        options(system: (_) => sleep(const Duration(milliseconds: 3))),
      );

      final lines = report.describe().split('\n');
      final first = lines[lines.indexOf('phase (per tick)') + 1];
      expect(first.trim(), startsWith('system'));
      expect(lines, contains(startsWith('  Position')));
      expect(lines.first, startsWith('8 clients'));
    },
  );

  test(
    'a run that finishes in time exits 0, an overrun 1, a refusal 2',
    () async {
      final easy = await runLoad(
        options(period: const Duration(milliseconds: 100), ticks: 3),
      );
      final refused = LoadReport(
        clients: easy.clients,
        ticks: easy.ticks,
        period: easy.period,
        elapsed: easy.elapsed,
        summary: easy.summary,
        accepted: easy.accepted,
        rejected: 1,
      );
      final overran = await runLoad(
        options(
          period: const Duration(milliseconds: 2),
          ticks: 4,
          system: (_) => sleep(const Duration(milliseconds: 6)),
        ),
      );

      expect(easy.exitCode, 0);
      expect(overran.exitCode, 1);
      expect(refused.exitCode, 2);
    },
  );

  test('a run that starts ticks late says so', () async {
    final report = await runLoad(
      options(
        period: const Duration(milliseconds: 2),
        system: (_) => sleep(const Duration(milliseconds: 8)),
      ),
    );

    expect(report.behind, isTrue);
    expect(report.describe(), contains('behind schedule'));
  });

  test('options that cannot run are refused', () {
    final walk = loadScripts['walk']!;
    expect(
      () => LoadOptions(clients: 0, ticks: 1, script: walk),
      throwsArgumentError,
    );
    expect(
      () => LoadOptions(clients: 1, ticks: 0, script: walk),
      throwsArgumentError,
    );
    expect(
      () => LoadOptions(
        clients: 1,
        ticks: 1,
        script: walk,
        period: Duration.zero,
      ),
      throwsArgumentError,
    );
  });

  test(
    'scripts: still sends nothing, random repeats and differs by client',
    () {
      expect(loadScripts['still']!(0, 0), isNull);

      final random = loadScripts['random']!;
      expect(random(3, 7), random(3, 7));
      expect(random(3, 7), isNot(random(4, 7)));
      expect(random(0, 100003), isNot(random(1, 0)));
    },
  );
}
