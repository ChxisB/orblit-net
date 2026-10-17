import 'package:orblit_net/load.dart';
import 'package:test/test.dart';

void main() {
  test('no arguments give the defaults', () {
    final run = parseLoadArgs([]);

    expect(run.script, 'walk');
    expect(run.options.clients, 50);
    expect(run.options.ticks, 200);
    expect(run.options.period, const Duration(milliseconds: 50));
    expect(run.options.sockets, isFalse);
    expect(run.options.system, isNull);
  });

  test('a value follows a flag or sits after an equals sign', () {
    final spaced = parseLoadArgs(['--clients', '8', '--seconds', '2']);
    final joined = parseLoadArgs(['--clients=8', '--seconds=2']);

    expect(spaced.options.clients, 8);
    expect(spaced.options.ticks, 40);
    expect(joined.options.clients, 8);
    expect(joined.options.ticks, 40);
  });

  test('hz sets the period and the tick count', () {
    final run = parseLoadArgs(['--hz', '10', '--seconds', '3']);

    expect(run.options.ticks, 30);
    expect(run.options.period, const Duration(milliseconds: 100));
  });

  test('sockets and a slow system switch on', () {
    final run = parseLoadArgs(['--sockets', '--slow-ms', '5']);

    expect(run.options.sockets, isTrue);
    expect(run.options.system, isNotNull);
  });

  test('a cell size turns interest on and none leaves it off', () {
    expect(parseLoadArgs([]).options.cellSize, isNull);
    expect(parseLoadArgs(['--cell', '25']).options.cellSize, 25);
    expect(() => parseLoadArgs(['--cell', '0']), throwsFormatException);
  });

  test('a slow system of zero is none', () {
    expect(parseLoadArgs(['--slow-ms', '0']).options.system, isNull);
  });

  test('what cannot run is refused with a message', () {
    for (final args in [
      ['--hz', '0'],
      ['--hz', '-5'],
      ['--clients', '0'],
      ['--clients', 'many'],
      ['--slow-ms', '-5'],
      ['--script', 'jump'],
      ['--nope'],
      ['--clients'],
      ['--sockets=yes'],
    ]) {
      expect(
        () => parseLoadArgs(args),
        throwsA(isA<FormatException>()),
        reason: args.join(' '),
      );
    }
  });

  test('a period under a microsecond is refused by the options', () {
    expect(() => parseLoadArgs(['--hz', '2000000']), throwsArgumentError);
  });
}
