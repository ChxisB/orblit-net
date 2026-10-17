import 'dart:typed_data';

import 'package:orblit_core/orblit_core.dart';
import 'package:orblit_net/orblit_net.dart';
import 'package:test/test.dart';

Future<void> settle() => Future<void>.delayed(Duration.zero);

class _BrokenTransport implements Transport {
  @override
  Stream<Uint8List> get inbound => const Stream.empty();

  @override
  void send(Uint8List message) => throw StateError('link is down');

  @override
  Future<void> close() async {}
}

void main() {
  late World world;
  late ComponentType networkId;
  late ComponentType position;
  late ComponentType health;
  late ReplicationSet set;
  late Duration now;
  late List<PhaseSummary> overruns;
  late NetHost host;
  final links = <LoopbackLink>[];
  final received = <String, int>{};

  const period = Duration(milliseconds: 50);

  setUp(() {
    world = World();
    networkId = world.registerComponent('NetworkId', kind: ComponentKind.int64);
    position = world.registerComponent(
      'Position',
      kind: ComponentKind.float32,
      arity: 3,
    );
    health = world.registerComponent('Health', kind: ComponentKind.int32);
    set = ReplicationSet([position, health]);
    now = Duration.zero;
    overruns = [];
    host = NetHost(
      world: world,
      set: set,
      networkId: networkId,
      clock: TickClock(period: period, now: () => now),
      onOverrun: overruns.add,
    );
  });

  tearDown(() async {
    await host.dispose();
    for (final link in links) {
      await link.close();
    }
    links.clear();
    received.clear();
    world.dispose();
  });

  void connect(String id) {
    final link = LoopbackLink();
    links.add(link);
    received[id] = 0;
    link.client.inbound.listen(
      (message) => received[id] = received[id]! + message.length,
    );
    host.addClient(id, link.host);
  }

  void spawn(int count) {
    for (var i = 0; i < count; i++) {
      final entity = world.createEntity();
      host.spawn(entity);
      world.add(entity, position, Float32List.fromList([i.toDouble(), 0, 0]));
      if (i.isEven) world.add(entity, health, Int32List.fromList([100]));
    }
  }

  test(
    '200 entities and 20 clients: every client is counted per column',
    () async {
      spawn(200);
      for (var i = 0; i < 20; i++) {
        connect('client-$i');
      }

      host.publish();
      host.publish();
      await settle();

      final bytes = host.summary().bytesSent;
      expect(bytes.keys, hasLength(20));
      for (final entry in bytes.entries) {
        expect(entry.value.columns['Position'], greaterThan(0));
        expect(entry.value.columns['Health'], greaterThan(0));
        expect(entry.value.overhead, greaterThan(0));
        expect(
          entry.value.total,
          received[entry.key],
          reason: 'columns and overhead must add up to every byte sent',
        );
      }
    },
  );

  test('a column is counted at its component size per row', () async {
    spawn(4);
    connect('a');
    host.publish();
    await settle();

    final columns = host.summary().bytesSent['a']!.columns;
    expect(columns['Position'], 4 * 12);
    expect(columns['Health'], 2 * 4);
  });

  test('a game system timed through the host sits beside the host stages', () {
    connect('a');
    host.phase('physics', () => now += const Duration(milliseconds: 3));
    host.publish();

    final phases = host.summary().phases;
    expect(phases.keys, containsAll(['physics', 'capture', 'encode', 'send']));
    expect(phases['physics']!.total, const Duration(milliseconds: 3));
  });

  test('a host with no clock still times its stages and reports no drift', () {
    final bare = NetHost(world: world, set: set, networkId: networkId);
    addTearDown(bare.dispose);
    bare.publish();

    final summary = bare.summary();
    expect(summary.ticks, 1);
    expect(summary.phases.keys, contains('capture'));
    expect(summary.driftMax, Duration.zero);
  });

  test('a slow stage past the period calls the overrun hook', () {
    host.phase('physics', () => now += const Duration(milliseconds: 60));
    host.publish();

    expect(overruns, hasLength(1));
    expect(
      overruns.single.phases['physics']!.max,
      const Duration(milliseconds: 60),
    );
  });

  test('a publish that throws still closes its tick', () {
    host.addClient('broken', _BrokenTransport());
    expect(host.publish, throwsStateError);
    expect(host.summary().ticks, 1);

    now += period;
    expect(host.publish, throwsStateError);
    expect(host.summary().ticks, 2);
  });

  test('a client that leaves is forgotten', () async {
    connect('a');
    host.publish();
    await settle();
    expect(host.summary().bytesSent, contains('a'));

    await host.removeClient('a');
    expect(host.summary().bytesSent, isNot(contains('a')));
  });
}
