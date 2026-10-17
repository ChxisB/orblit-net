/// A load harness: a host and many scripted clients on one real tick clock.
///
/// The clients run on the same thread as the host. Their decoding eats time
/// between ticks, so it shows up as drift. The phase table is the host's own
/// cost.
library;

import 'dart:async';
import 'dart:math';
import 'dart:typed_data';

import 'package:orblit_core/orblit_core.dart';

import 'orblit_net.dart';

/// Where a scripted client puts its own entity on a tick, or null to send
/// nothing that tick.
typedef LoadScript = Float32List? Function(int client, int tick);

Float32List? _still(int _, int _) => null;

Float32List? _walk(int client, int tick) =>
    Float32List.fromList([client + tick * 0.1, 0, 0]);

// Seeded from the client and tick, so a run repeats.
Float32List? _random(int client, int tick) {
  final random = Random(client * 100003 + tick);
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

  /// The game's own work for a tick. It runs inside `host.phase('system')`,
  /// so it shows in the summary beside the host's stages.
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

  bool get overran => summary.tickAverage > period;

  String describe() {
    final out = StringBuffer()
      ..writeln(
        '$clients clients, $ticks ticks of ${period.inMilliseconds} ms '
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

    out.writeln('bytes per client per second');
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

/// Joins one client to [host] and returns the client's end. Every transport
/// opened lands in [open], to be closed when the run ends.
///
/// Sockets connect one at a time, so the host's ids follow connection order.
Future<Transport> _join(
  NetHost host,
  String id,
  SocketServer? server,
  List<Transport> open,
) async {
  if (server == null) {
    final link = LoopbackLink();
    open.addAll([link.host, link.client]);
    host.addClient(id, link.host);
    return link.client;
  }
  final arrived = server.connections.first;
  final client = await SocketTransport.connect(server.url);
  final accepted = await arrived;
  open.addAll([accepted, client]);
  host.addClient(id, accepted);
  return client;
}

/// Runs [options.ticks] ticks of a host with [options.clients] scripted
/// clients, one entity each, and reports where the time went.
Future<LoadReport> runLoad(LoadOptions options) async {
  final clock = TickClock(period: options.period);
  final schema = _Schema();
  final host = NetHost(
    world: schema.world,
    set: schema.set,
    networkId: schema.networkId,
    clock: clock,
  );
  final server = options.sockets ? await SocketServer.bind() : null;
  final open = <Transport>[];
  final peers = <_Peer>[];
  try {
    for (var i = 0; i < options.clients; i++) {
      final entity = schema.world.createEntity();
      final entityId = host.spawn(entity, owner: 'c$i');
      schema.world.add(entity, schema.position, Float32List(3));
      peers.add(_Peer(await _join(host, 'c$i', server, open), entityId));
    }

    final watch = Stopwatch()..start();
    for (var tick = 0; tick < options.ticks; tick++) {
      await Future<void>.delayed(clock.untilNext());
      for (var i = 0; i < peers.length; i++) {
        final position = options.script(i, tick);
        if (position != null) peers[i].sendInput(position);
      }
      final system = options.system;
      if (system != null) host.phase('system', () => system(tick));
      host.publish();
    }
    watch.stop();

    return LoadReport(
      clients: options.clients,
      ticks: options.ticks,
      period: options.period,
      elapsed: watch.elapsed,
      summary: host.summary(last: 300),
      accepted: host.acceptedInputs,
      rejected: host.rejections.values.fold(0, (sum, n) => sum + n),
    );
  } finally {
    await host.dispose();
    for (final peer in peers) {
      await peer.dispose();
    }
    for (final transport in open) {
      await transport.close();
    }
    await server?.close();
    schema.world.dispose();
  }
}
