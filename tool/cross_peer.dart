// The Dart end of the cross-join tests in dotnet/Orblit.Net.Tests.
//
//   host           serves one client, plays the scene for 100 ticks, prints
//                  `done <json>` and keeps publishing until stdin closes
//   client <url> <ticks>
//                  joins a host, waits for a snapshot at or past <ticks>,
//                  and prints the world it holds as one line of json
//
// The line before the json on `host` is `orblit-net <url>`, so a test can
// find the port.
import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:orblit_core/orblit_core.dart';
import 'package:orblit_net/orblit_net.dart';

const _ticks = 100;
const _patience = Duration(seconds: 60);

Future<void> main(List<String> args) async {
  switch (args) {
    case ['host']:
      await _host();
    case ['client', final url, final ticks]:
      await _client(Uri.parse(url), int.parse(ticks));
    default:
      stderr.writeln('usage: cross_peer.dart host | client <url> <ticks>');
      exit(64);
  }
  await stdout.flush();
  // A socket close leaves a timer that holds the process for a few seconds.
  exit(0);
}

/// The components both ends declare. The network ids are 1, 2, 3 and then 4,
/// in the order the host spawns the entities.
final class _Scene {
  _Scene() {
    networkId = world.registerComponent('NetworkId', kind: ComponentKind.int64);
    position = world.registerComponent(
      'Position',
      kind: ComponentKind.float32,
      arity: 3,
    );
    velocity = world.registerComponent(
      'Velocity',
      kind: ComponentKind.float32,
      arity: 3,
    );
    health = world.registerComponent('Health', kind: ComponentKind.int32);
    set = ReplicationSet([position, velocity, health]);
  }

  final World world = World();
  late final ComponentType networkId;
  late final ComponentType position;
  late final ComponentType velocity;
  late final ComponentType health;
  late final ReplicationSet set;

  /// The world as one line of json, for the entities with network ids 1 to 4.
  String describe(int? Function(int networkId) entityFor, int tick) {
    final entities = <Map<String, Object?>>[];
    for (var id = 1; id <= 4; id++) {
      final entity = entityFor(id);
      if (entity == null) continue;
      final bytes = world.bytesOf(entity, health);
      entities.add({
        'id': id,
        'position': world.float32Of(entity, position)!.toList(),
        'health': bytes == null
            ? null
            : ByteData.sublistView(bytes).getInt32(0, Endian.little),
      });
    }
    return jsonEncode({'tick': tick, 'entities': entities});
  }
}

/// A host that plays the scene: three entities that move every tick, one that
/// gains health at tick 10, one spawned at tick 30 and one removed at tick 60.
final class _Playing {
  _Playing(this.scene)
    : host = NetHost(
        world: scene.world,
        set: scene.set,
        networkId: scene.networkId,
      );

  final _Scene scene;
  final NetHost host;
  final Map<int, int> _live = {};

  void _spawn() {
    final entity = scene.world.createEntity();
    final id = host.spawn(entity);
    scene.world.add(entity, scene.position, Float32List(3));
    _live[id] = entity;
  }

  void start() {
    for (var i = 0; i < 3; i++) {
      _spawn();
    }
  }

  void step(int tick) {
    final world = scene.world;
    if (tick == 10) {
      world.add(_live[1]!, scene.health, Int32List.fromList([100]));
    }
    if (tick == 30) _spawn();
    if (tick == 60) {
      host.despawn(_live.remove(2)!);
    }
    for (final MapEntry(key: id, value: entity) in _live.entries) {
      world.float32Of(entity, scene.position)!
        ..[0] = id + tick * 0.5
        ..[1] = (tick % 7).toDouble()
        ..[2] = -id.toDouble();
    }
    if (tick > 10) {
      final bytes = world.bytesOf(_live[1]!, scene.health)!;
      ByteData.sublistView(bytes).setInt32(0, 100 - tick, Endian.little);
    }
    host.publish();
  }

  String describe() => scene.describe((id) => _live[id], host.tick);
}

Future<void> _host() async {
  final server = await SocketServer.bind();
  final arrived = server.connections.first;
  stdout.writeln('orblit-net ${server.url}');
  await stdout.flush();
  final transport = await arrived.timeout(_patience);

  final scene = _Scene();
  final playing = _Playing(scene)..start();
  playing.host.addClient('a', transport);

  var open = true;
  unawaited(stdin.drain<void>().then((_) => open = false));

  for (var tick = 1; tick <= _ticks; tick++) {
    playing.step(tick);
    await Future<void>.delayed(const Duration(milliseconds: 5));
  }
  stdout.writeln('done ${playing.describe()}');
  await stdout.flush();

  // A delta that does not follow what the client holds is dropped, and the
  // host sends the next tick as a fresh baseline. Idle ticks let the last
  // change arrive.
  while (open) {
    playing.host.publish();
    await Future<void>.delayed(const Duration(milliseconds: 20));
  }
  await playing.host.dispose();
  await transport.close();
  await server.close();
  scene.world.dispose();
}

Future<void> _client(Uri url, int ticks) async {
  final transport = await SocketTransport.connect(url);
  final scene = _Scene();
  final client = NetClient(
    world: scene.world,
    set: scene.set,
    networkId: scene.networkId,
    transport: transport,
  );

  final limit = Stopwatch()..start();
  while (client.lastAppliedTick < ticks) {
    if (limit.elapsed > _patience) {
      stderr.writeln('stopped at tick ${client.lastAppliedTick}');
      exit(1);
    }
    await Future<void>.delayed(const Duration(milliseconds: 10));
  }
  stdout.writeln(scene.describe(client.entityFor, client.lastAppliedTick));
  await stdout.flush();

  await client.dispose();
  await transport.close();
  scene.world.dispose();
}
