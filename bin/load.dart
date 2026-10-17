import 'dart:io';

import 'package:orblit_net/load.dart';

Future<void> main(List<String> args) async {
  if (args.contains('--help')) {
    stdout.writeln(loadUsage);
    return;
  }

  final ({LoadOptions options, String script}) run;
  try {
    run = parseLoadArgs(args);
  } on FormatException catch (e) {
    stderr.writeln('${e.message}\n\n$loadUsage');
    exit(64);
  } on ArgumentError catch (e) {
    stderr.writeln('$e\n\n$loadUsage');
    exit(64);
  }

  stdout.writeln(
    'script ${run.script} over '
    '${run.options.sockets ? 'sockets' : 'loopback'}',
  );
  final report = await runLoad(run.options);
  stdout.write(report.describe());
  await stdout.flush();
  // A socket close leaves a timer that holds the process for about five
  // seconds. The report is out, so there is nothing left to wait for.
  exit(report.exitCode);
}
