import 'dart:async';
import 'dart:math';
import 'dart:typed_data';

import 'package:orblit_core/orblit_core.dart';

import '../orblit_net.dart';

/// Where a scripted client puts its own entity on a tick, or null to send
/// nothing that tick.
typedef LoadScript = Float32List? Function(int client, int tick);

Float32List? _still(int _, int _) => null;

Float32List? _walk(int client, int tick) =>
    Float32List.fromList([client + tick * 0.1, 0, 0]);

// Seeded from the client and tick, so a run repeats.
Float32List? _random(int client, int tick) {
  final random = Random((client << 32) | tick);
  return Float32List.fromList([
    random.nextDouble() * 100,
    0,
    random.nextDouble() * 100,
  ]);
}

const loadScripts = <String, LoadScript>{
  'still': _still,
  'walk': _walk,
  'random': _random,
};

final class LoadOptions {
  LoadOptions({
    required this.clients,
    required this.ticks,
    required this.script,
    this.period = const Duration(milliseconds: 50),
    this.sockets = false,
    this.system,
  }) {
    if (clients < 1) throw ArgumentError.value(clients, 'clients', 'below 1');
    if (ticks < 1) throw ArgumentError.value(ticks, 'ticks', 'below 1');
    if (period <= Duration.zero) {
      throw ArgumentError.value(period, 'period', 'not positive');
    }
  }

  final int clients;
  final int ticks;
  final LoadScript script;
  final Duration period;

  /// Use WebSockets on localhost instead of loopback links.
  final bool sockets;

  /// Stands in for the server's own work in a tick, such as physics. It runs
  /// inside `host.phase('system')`, so it shows in the summary beside the
  /// host's stages.
  final void Function(int tick)? system;
}

final class LoadReport {
  const LoadReport({
    required this.clients,
    required this.ticks,
    required this.period,
    required this.elapsed,
    required this.summary,
    required this.accepted,
    required this.rejected,
  });

  final int clients;
  final int ticks;
  final Duration period;
  final Duration elapsed;
  final PhaseSummary summary;

  /// Inputs the host applied, and inputs it refused. A run that refuses any
  /// is mis-wired, and its figures are not a load.
  final int accepted;
  final int rejected;

  /// The host's own tick, averaged over the measured window, is longer than
  /// the period.
  bool get overran => summary.tickAverage > period;

  /// Ticks started late on average. With the clients on the host's thread,
  /// this can be the harness, not the host.
  bool get behind => summary.driftAverage > period;

  /// 2 when the host refused inputs, since the figures are not a load. 1 when
  /// the host's tick overran. Otherwise 0.
  int get exitCode => rejected > 0
      ? 2
      : overran
      ? 1
      : 0;

  String describe() {
    final out = StringBuffer()
      ..writeln(
        '$clients clients, $ticks ticks of ${_ms(period)} '
        '(last ${summary.ticks} measured)',
      )
      ..writeln(
        'tick   avg ${_ms(summary.tickAverage)}  '
        'max ${_ms(summary.tickMax)}',
      )
      ..writeln(
        'drift  avg ${_ms(summary.driftAverage)}  '
        'max ${_ms(summary.driftMax)}',
      );
    _writePhases(out);
    _writeBytes(out);
    out.writeln('inputs applied $accepted, refused $rejected');
    if (behind) {
      out.writeln(
        'behind schedule: ticks started ${_ms(summary.driftAverage)} late on '
        "average. The clients share the host's thread.",
      );
    }
    return out.toString();
  }

  void _writePhases(StringBuffer out) {
    final phases = summary.phases.entries.toList()
      ..sort((a, b) => b.value.average.compareTo(a.value.average));
    out.writeln('phase (per tick)');
    for (final phase in phases) {
      out.writeln(
        '  ${phase.key.padRight(10)} avg ${_ms(phase.value.average)}  '
        'max ${_ms(phase.value.max)}',
      );
    }
  }

  void _writeBytes(StringBuffer out) {
    final columns = <String, int>{};
    var overhead = 0;
    for (final sent in summary.bytesSent.values) {
      sent.columns.forEach(
        (name, n) => columns[name] = (columns[name] ?? 0) + n,
      );
      overhead += sent.overhead;
    }
    final seconds = elapsed.inMicroseconds / Duration.microsecondsPerSecond;
    String rate(int total) => (total / clients / seconds).toStringAsFixed(0);

    out.writeln('bytes per client per second (first full snapshot included)');
    for (final column in columns.entries) {
      out.writeln('  ${column.key.padRight(10)} ${rate(column.value)}');
    }
    out.writeln('  ${'overhead'.padRight(10)} ${rate(overhead)}');
  }

  static String _ms(Duration d) =>
      '${(d.inMicroseconds / 1000).toStringAsFixed(2)} ms';
}

/// The components both sides declare. Each client has a world of its own.
final class _Schema {
  _Schema() {
    networkId = world.registerComponent('NetworkId', kind: ComponentKind.int64);
    position = world.registerComponent(
      'Position',
      kind: ComponentKind.float32,
      arity: 3,
    );
    set = ReplicationSet([position], ownerWritable: {'Position'});
  }

  final World world = World();
  late final ComponentType networkId;
  late final ComponentType position;
  late final ReplicationSet set;
}

final class _Peer {
  _Peer(Transport transport, this.entityId) {
    client = NetClient(
      world: schema.world,
      set: schema.set,
      networkId: schema.networkId,
      transport: transport,
    );
  }

  final _Schema schema = _Schema();
  final int entityId;
  late final NetClient client;

  void sendInput(Float32List position) => client.sendInput({
    entityId: {schema.position: position},
  });

  Future<void> dispose() async {
    await client.dispose();
    schema.world.dispose();
  }
}

typedef _Pair = ({Transport host, Transport client});

Future<_Pair> _loopbackPair() async {
  final link = LoopbackLink();
  return (host: link.host, client: link.client);
}

/// Connects one client. The server's next connection is the one this client
/// made, because clients join one at a time and the host's ids follow that
/// order.
Future<_Pair> _socketPair(SocketServer server) async {
  final (client, accepted) = await (
    SocketTransport.connect(server.url),
    server.connections.first.timeout(_joinTimeout),
  ).wait;
  return (host: accepted, client: client);
}

const _joinTimeout = Duration(seconds: 10);

// PhaseTimes keeps 300 ticks, so a longer window would return the same.
const _measured = 300;

final class _Run {
  _Run(this.options, this._server)
    : _clock = TickClock(period: options.period),
      _pair = _server == null ? _loopbackPair : (() => _socketPair(_server)) {
    host = NetHost(
      world: _schema.world,
      set: _schema.set,
      networkId: _schema.networkId,
      clock: _clock,
    );
  }

  final LoadOptions options;
  final SocketServer? _server;
  final TickClock _clock;
  final Future<_Pair> Function() _pair;
  final _Schema _schema = _Schema();
  late final NetHost host;
  final List<Transport> _open = [];
  final List<_Peer> _peers = [];

  Future<void> join(int index) async {
    final id = 'c$index';
    final entity = _schema.world.createEntity();
    final entityId = host.spawn(entity, owner: id);
    _schema.world.add(entity, _schema.position, Float32List(3));

    final pair = await _pair();
    _open.addAll([pair.host, pair.client]);
    host.addClient(id, pair.host);
    _peers.add(_Peer(pair.client, entityId));
  }

  Future<void> tick(int number) async {
    await Future<void>.delayed(_clock.untilNext());
    for (var i = 0; i < _peers.length; i++) {
      final position = options.script(i, number);
      if (position != null) _peers[i].sendInput(position);
    }
    final system = options.system;
    if (system != null) host.phase('system', () => system(number));
    host.publish();
  }

  LoadReport report(Duration elapsed) => LoadReport(
    clients: options.clients,
    ticks: options.ticks,
    period: options.period,
    elapsed: elapsed,
    summary: host.summary(last: _measured),
    accepted: host.acceptedInputs,
    rejected: host.rejections.values.fold(0, (sum, n) => sum + n),
  );

  Future<void> dispose() async {
    await host.dispose();
    for (final peer in _peers) {
      await peer.dispose();
    }
    for (final transport in _open) {
      await transport.close();
    }
    await _server?.close();
    _schema.world.dispose();
  }
}

/// Runs [options.ticks] ticks of a host with [options.clients] scripted
/// clients, one entity each, and reports where the time went.
Future<LoadReport> runLoad(LoadOptions options) async {
  final server = options.sockets ? await SocketServer.bind() : null;
  final run = _Run(options, server);
  try {
    for (var i = 0; i < options.clients; i++) {
      await run.join(i);
    }
    final watch = Stopwatch()..start();
    for (var tick = 0; tick < options.ticks; tick++) {
      await run.tick(tick);
    }
    return run.report(watch.elapsed);
  } finally {
    await run.dispose();
  }
}
