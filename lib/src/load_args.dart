import 'dart:io';

import 'load.dart';

const loadUsage = '''
Usage: dart run orblit_net:load [options]
  --clients N    scripted clients (default 50)
  --seconds S    length of the run (default 10)
  --hz N         ticks per second (default 20)
  --script NAME  still, walk or random (default walk)
  --sockets      WebSockets on localhost instead of loopback links
  --slow-ms N    a system that takes N ms each tick (default 0)
  --help         this text

The tick is judged over the last 300 ticks. Exits 1 when the host's average
tick is longer than the period, and 2 when the host refused any input.''';

const _valueFlags = {'clients', 'seconds', 'hz', 'script', 'slow-ms'};

/// Reads `--name value` and `--name=value`. A bare `--sockets` is a switch.
Map<String, String> _flags(List<String> args) {
  final flags = <String, String>{};
  for (var i = 0; i < args.length; i++) {
    final eq = args[i].indexOf('=');
    final name = eq < 0 ? args[i] : args[i].substring(0, eq);
    if (name == '--sockets' && eq < 0) {
      flags['sockets'] = '';
    } else if (!name.startsWith('--') ||
        !_valueFlags.contains(name.substring(2))) {
      throw FormatException('Unknown option: ${args[i]}');
    } else if (eq >= 0) {
      flags[name.substring(2)] = args[i].substring(eq + 1);
    } else if (i + 1 < args.length) {
      flags[name.substring(2)] = args[++i];
    } else {
      throw FormatException('$name needs a value.');
    }
  }
  return flags;
}

int _number(
  Map<String, String> flags,
  String name,
  int fallback, {
  int min = 1,
}) {
  final value = int.tryParse(flags[name] ?? '$fallback');
  if (value == null || value < min) {
    throw FormatException('--$name wants a whole number of $min or more.');
  }
  return value;
}

/// Turns command line arguments into a run. Throws [FormatException] with a
/// message fit to show, or [ArgumentError] from [LoadOptions].
({LoadOptions options, String script}) parseLoadArgs(List<String> args) {
  final flags = _flags(args);
  final script = flags['script'] ?? 'walk';
  final scripted =
      loadScripts[script] ??
      (throw FormatException(
        '--script is one of ${loadScripts.keys.join(', ')}.',
      ));
  final hz = _number(flags, 'hz', 20);
  final slow = _number(flags, 'slow-ms', 0, min: 0);

  final options = LoadOptions(
    clients: _number(flags, 'clients', 50),
    ticks: _number(flags, 'seconds', 10) * hz,
    script: scripted,
    period: Duration(microseconds: Duration.microsecondsPerSecond ~/ hz),
    sockets: flags.containsKey('sockets'),
    system: slow > 0 ? (_) => sleep(Duration(milliseconds: slow)) : null,
  );
  return (options: options, script: script);
}
