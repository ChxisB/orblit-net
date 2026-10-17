import 'dart:io';

import 'package:orblit_net/load.dart';

const _usage = '''
Usage: dart run orblit_net:load [options]
  --clients N    scripted clients (default 50)
  --seconds S    length of the run (default 10)
  --hz N         ticks per second (default 20)
  --script NAME  still, walk or random (default walk)
  --sockets      WebSockets on localhost instead of loopback links
  --slow-ms N    a system that takes N ms each tick

Exits 1 when the average tick is longer than the period.''';

const _valueFlags = {'clients', 'seconds', 'hz', 'script', 'slow-ms'};

Map<String, String> _parse(List<String> args) {
  final flags = <String, String>{};
  for (var i = 0; i < args.length; i++) {
    final arg = args[i];
    if (arg == '--sockets') {
      flags['sockets'] = '';
    } else if (arg.startsWith('--') && _valueFlags.contains(arg.substring(2))) {
      if (i + 1 >= args.length) throw FormatException('$arg needs a value.');
      flags[arg.substring(2)] = args[++i];
    } else {
      throw FormatException('Unknown option: $arg');
    }
  }
  return flags;
}

LoadOptions _options(Map<String, String> flags) {
  int number(String name, int fallback) =>
      int.tryParse(flags[name] ?? '$fallback') ??
      (throw FormatException('--$name wants a whole number.'));

  final script = flags['script'] ?? 'walk';
  final scripted = loadScripts[script];
  if (scripted == null) {
    throw FormatException('--script is one of ${loadScripts.keys.join(', ')}.');
  }
  final hz = number('hz', 20);
  final slow = number('slow-ms', 0);
  return LoadOptions(
    clients: number('clients', 50),
    ticks: number('seconds', 10) * hz,
    script: scripted,
    period: Duration(microseconds: Duration.microsecondsPerSecond ~/ hz),
    sockets: flags.containsKey('sockets'),
    system: slow > 0 ? (_) => sleep(Duration(milliseconds: slow)) : null,
  );
}

Future<void> main(List<String> args) async {
  final Map<String, String> flags;
  final LoadOptions options;
  try {
    flags = _parse(args);
    options = _options(flags);
  } on FormatException catch (e) {
    stderr.writeln('${e.message}\n\n$_usage');
    exitCode = 64;
    return;
  } on ArgumentError catch (e) {
    stderr.writeln('$e\n\n$_usage');
    exitCode = 64;
    return;
  }

  stdout.writeln(
    'script ${flags['script'] ?? 'walk'} over '
    '${options.sockets ? 'sockets' : 'loopback'}',
  );
  final report = await runLoad(options);
  stdout.write(report.describe());
  if (report.overran) exitCode = 1;
}
