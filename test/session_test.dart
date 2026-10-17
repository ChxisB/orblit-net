import 'dart:convert';
import 'dart:typed_data';

import 'package:orblit_core/orblit_core.dart';
import 'package:orblit_net/orblit_net.dart';
import 'package:test/test.dart';

Future<void> settle() => Future<void>.delayed(Duration.zero);

Uint8List credentials(String name) => Uint8List.fromList(utf8.encode(name));

/// Accepts every name but "banned".
Admission admitByName(Uint8List bytes) {
  final name = utf8.decode(bytes);
  return name == 'banned' ? const Refuse('banned') : Admit(name);
}

/// A replicated world with a position column, and the same on a client.
class Side {
  Side() {
    networkId = world.registerComponent('NetworkId', kind: ComponentKind.int64);
    position = world.registerComponent(
      'Position',
      kind: ComponentKind.float32,
      arity: 3,
    );
    owner = world.registerComponent('Owner', kind: ComponentKind.uint32);
    set = ReplicationSet([position], ownerWritable: {'Position'});
  }

  final World world = World();
  late final ComponentType networkId;
  late final ComponentType position;
  late final ComponentType owner;
  late final ReplicationSet set;
}

/// One connection: a loopback link and the client at its far end.
class Connection {
  Connection(this.side) : link = LoopbackLink() {
    client = NetClient(
      world: side.world,
      set: side.set,
      networkId: side.networkId,
      transport: link.client,
      onGoodbye: reasons.add,
    );
  }

  final Side side;
  final LoopbackLink link;
  final List<String> reasons = [];
  late final NetClient client;
}

void main() {
  late Side authority;
  late NetHost host;
  late List<String> joined;
  late List<(String, GoneReason)> gone;
  late List<int> spawned;
  final peers = <Side>[];

  Connection connect({String? as}) {
    final side = Side();
    peers.add(side);
    final connection = Connection(side);
    addTearDown(connection.link.close);
    host.accept(connection.link.host);
    if (as != null) connection.client.join(credentials(as));
    return connection;
  }

  NetHost build({
    bool takeOver = false,
    Admission Function(Uint8List) admit = admitByName,
  }) {
    return NetHost(
      world: authority.world,
      set: authority.set,
      networkId: authority.networkId,
      ownerComponent: authority.owner,
      sessions: SessionPolicy(
        admit: admit,
        lingerTicks: 3,
        takeOver: takeOver,
        onJoin: (String key) {
          final entity = authority.world.createEntity();
          authority.world.add(entity, authority.position);
          spawned.add(host.spawn(entity, owner: key));
          joined.add(key);
        },
        onGone: (String key, GoneReason reason) {
          // The entities are still the key's when the game is told.
          expect(host.ownerOf(spawned.first), key);
          gone.add((key, reason));
        },
      ),
    );
  }

  setUp(() {
    authority = Side();
    joined = [];
    gone = [];
    spawned = [];
    host = build();
  });

  tearDown(() async {
    await host.dispose();
    authority.world.dispose();
    for (final side in peers) {
      side.world.dispose();
    }
    peers.clear();
  });

  group('admission', () {
    test('a refused join gets the reason and leaves nothing behind', () async {
      final entities = authority.world.entityCount;
      final stranger = connect(as: 'banned');
      await settle();

      expect(stranger.reasons, ['banned']);
      expect(host.clientCount, 0);
      expect(host.sessionState('banned'), isNull);
      expect(joined, isEmpty);
      expect(authority.world.entityCount, entities);
    });

    test('an admitted join starts a live session once', () async {
      connect(as: 'ann');
      await settle();

      expect(host.sessionState('ann'), SessionState.live);
      expect(host.clientCount, 1);
      expect(joined, ['ann']);
    });

    test('a message before the join applies nothing', () async {
      final early = connect();
      await settle();
      early.link.client.send(
        encodeInput(
          InputMessage(
            tick: 1,
            entries: [InputEntry(networkId: 1, mask: 1, row: Uint8List(12))],
          ),
        ),
      );
      await settle();

      expect(host.acceptedInputs, 0);
      expect(host.rejections, isEmpty);
      expect(host.clientCount, 0);
    });

    test('a host with no policy cannot accept', () {
      final plain = NetHost(
        world: authority.world,
        set: authority.set,
        networkId: authority.networkId,
      );
      expect(() => plain.accept(LoopbackLink().host), throwsStateError);
    });
  });

  /// The client index the host wrote into the entity's owner component.
  int indexOf(int networkId) {
    final entity = host.entityFor(networkId)!;
    final bytes = authority.world.bytesOf(entity, authority.owner)!;
    return ByteData.sublistView(bytes).getUint32(0, Endian.little);
  }

  Future<void> drop(Connection connection) async {
    await connection.link.close();
    await settle();
  }

  Future<void> publish(int times) async {
    for (var i = 0; i < times; i++) {
      host.publish();
    }
    await settle();
  }

  group('linger and resume', () {
    test('a drop keeps the session and its entity', () async {
      final ann = connect(as: 'ann');
      await settle();
      await drop(ann);

      expect(host.sessionState('ann'), SessionState.lingering);
      expect(host.clientCount, 0);
      expect(host.ownerOf(spawned.first), 'ann');
      expect(host.entityFor(spawned.first), isNotNull);
    });

    test('a join with the same key resumes the same entity', () async {
      final first = connect(as: 'ann');
      await settle();
      final before = indexOf(spawned.first);
      await drop(first);
      await publish(1);

      final second = connect(as: 'ann');
      await settle();
      await publish(1);

      expect(host.sessionState('ann'), SessionState.live);
      expect(host.clientCount, 1);
      expect(joined, ['ann']);
      expect(gone, isEmpty);
      expect(host.ownerOf(spawned.first), 'ann');
      expect(indexOf(spawned.first), before);
      expect(second.client.entityFor(spawned.first), isNotNull);
    });

    test('a resumed client is told the whole world, not a delta', () async {
      final first = connect(as: 'ann');
      await settle();
      await publish(2);
      await drop(first);
      await publish(2);

      final second = connect(as: 'ann');
      await settle();
      await publish(1);

      expect(second.client.entityCount, 1);
    });

    test('another key gets its own index', () async {
      connect(as: 'ann');
      connect(as: 'bob');
      await settle();

      expect(spawned, hasLength(2));
      expect(indexOf(spawned[0]), isNot(indexOf(spawned[1])));
    });
  });

  group('one live session per key', () {
    test('a second join is refused and the first carries on', () async {
      final first = connect(as: 'ann');
      await settle();
      final second = connect(as: 'ann');
      await settle();
      await publish(1);

      expect(second.reasons, ['alreadyLive']);
      expect(first.reasons, isEmpty);
      expect(host.clientCount, 1);
      expect(joined, ['ann']);
      expect(first.client.entityCount, 1);
    });

    test('takeOver moves the session to the new connection', () async {
      await host.dispose();
      host = build(takeOver: true);
      final first = connect(as: 'ann');
      await settle();
      final second = connect(as: 'ann');
      await settle();
      await publish(1);

      expect(first.reasons, ['replaced']);
      expect(second.reasons, isEmpty);
      expect(host.sessionState('ann'), SessionState.live);
      expect(host.clientCount, 1);
      expect(joined, ['ann']);
      expect(gone, isEmpty);
      expect(second.client.entityCount, 1);
    });
  });

  group('ending', () {
    test('a session that lingers long enough ends once', () async {
      final ann = connect(as: 'ann');
      await settle();
      await drop(ann);

      await publish(2);
      expect(host.sessionState('ann'), SessionState.lingering);
      expect(gone, isEmpty);

      await publish(1);
      expect(gone, [('ann', GoneReason.disconnect)]);
      expect(host.sessionState('ann'), isNull);
      expect(host.ownerOf(spawned.first), isNull);

      await publish(5);
      expect(gone, hasLength(1));
    });

    test('a resume in time means it never ends', () async {
      final first = connect(as: 'ann');
      await settle();
      await drop(first);
      await publish(2);

      connect(as: 'ann');
      await settle();
      await publish(10);

      expect(gone, isEmpty);
      expect(host.sessionState('ann'), SessionState.live);
    });

    test('removeClient ends a live session as removed', () async {
      connect(as: 'ann');
      await settle();
      await host.removeClient('ann');

      expect(gone, [('ann', GoneReason.removed)]);
      expect(host.sessionState('ann'), isNull);
      expect(host.ownerOf(spawned.first), isNull);
    });

    test('removeClient ends a lingering session once', () async {
      final ann = connect(as: 'ann');
      await settle();
      await drop(ann);
      await host.removeClient('ann');
      await publish(10);

      expect(gone, [('ann', GoneReason.removed)]);
    });

    test('an admission that throws refuses the join', () async {
      await host.dispose();
      host = build(admit: (_) => throw const FormatException('garbled'));
      final stranger = connect(as: 'anyone');
      await settle();

      expect(stranger.reasons, ['refused']);
      expect(host.clientCount, 0);
      expect(joined, isEmpty);
    });
  });
}
