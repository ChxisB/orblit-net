import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:orblit_core/orblit_core.dart';
import 'package:orblit_net/orblit_net.dart';
import 'package:orblit_net/src/session_message.dart';
import 'package:test/test.dart';

Future<void> settle() => Future<void>.delayed(Duration.zero);

Uint8List credentials(String name) => Uint8List.fromList(utf8.encode(name));

/// Accepts every name but "banned".
Admission admitByName(Uint8List bytes) {
  final name = utf8.decode(bytes);
  return name == 'banned' ? const Refuse('banned') : Admit(name);
}

/// A transport that records what the host did to it.
final class Probe implements Transport {
  final StreamController<Uint8List> _heard = StreamController.broadcast();
  final List<String> goodbyes = [];
  var closed = false;

  @override
  Stream<Uint8List> get inbound => _heard.stream;

  void hear(Uint8List message) => _heard.add(message);

  /// The peer going away.
  Future<void> hangUp() => _heard.close();

  @override
  void send(Uint8List message) {
    final reason = decodeGoodbye(message);
    if (reason != null) goodbyes.add(reason);
  }

  @override
  Future<void> close() async => closed = true;
}

/// A replicated world with a position column, and the same on a client.
final class Side {
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
final class Connection {
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
  var failJoin = false;
  var failGone = false;
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
    int joinTicks = 300,
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
        joinTicks: joinTicks,
        takeOver: takeOver,
        onJoin: (String key) {
          if (failJoin) throw StateError('onJoin failed');
          final entity = authority.world.createEntity();
          authority.world.add(entity, authority.position);
          spawned.add(host.spawn(entity, owner: key));
          joined.add(key);
        },
        onGone: (String key, GoneReason reason) {
          // The entities are still the key's when the game is told.
          expect(host.ownerOf(spawned.first), key);
          gone.add((key, reason));
          if (failGone) throw StateError('onGone failed');
        },
      ),
    );
  }

  setUp(() {
    authority = Side();
    joined = [];
    gone = [];
    spawned = [];
    failJoin = false;
    failGone = false;
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

    test('an admission that throws an Error refuses the join', () async {
      await host.dispose();
      host = build(admit: (_) => throw StateError('garbled'));
      final stranger = connect(as: 'anyone');
      await settle();

      expect(stranger.reasons, [GoodbyeReason.refused]);
      expect(host.clientCount, 0);
    });

    test('a connection that never joins is closed after joinTicks', () async {
      await host.dispose();
      host = build(joinTicks: 2);
      final silent = Probe();
      host.accept(silent);

      await publish(1);
      expect(silent.goodbyes, isEmpty);
      expect(silent.closed, isFalse);

      await publish(1);
      expect(silent.goodbyes, [GoodbyeReason.joinTimeout]);
      expect(silent.closed, isTrue);
    });

    test('a connection that drops before joining leaves nothing', () async {
      await host.dispose();
      host = build(joinTicks: 2);
      final early = connect();
      await settle();
      await drop(early);
      await publish(5);

      expect(early.reasons, isEmpty);
      expect(host.clientCount, 0);
    });

    test('the host closes a transport whose peer hung up', () async {
      final gone = Probe();
      host.accept(gone);
      await gone.hangUp();
      await settle();
      expect(gone.closed, isTrue);

      final ann = Probe();
      host.accept(ann);
      ann.hear(encodeJoin(credentials('ann')));
      await settle();
      await ann.hangUp();
      await settle();
      expect(ann.closed, isTrue);
      expect(host.sessionState('ann'), SessionState.lingering);
    });

    test('a join that arrives after the deadline is not admitted', () async {
      await host.dispose();
      host = build(joinTicks: 1);
      final late = connect();
      await settle();
      await publish(1);
      late.client.join(credentials('ann'));
      await settle();

      expect(host.sessionState('ann'), isNull);
      expect(joined, isEmpty);
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

    test('setOwner for a lingering key writes its index', () async {
      final ann = connect(as: 'ann');
      await settle();
      final index = indexOf(spawned.first);
      await drop(ann);

      final entity = authority.world.createEntity();
      authority.world.add(entity, authority.position);
      final extra = host.spawn(entity);
      host.setOwner(extra, 'ann');

      expect(indexOf(extra), index);
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

    test('a game-added client cannot be taken over', () async {
      await host.dispose();
      host = build(takeOver: true);
      final owned = Connection(Side());
      peers.add(owned.side);
      addTearDown(owned.link.close);
      host.addClient('ann', owned.link.host);

      final second = connect(as: 'ann');
      await settle();

      expect(second.reasons, [GoodbyeReason.alreadyLive]);
      expect(owned.reasons, isEmpty);
      expect(host.clientCount, 1);
      expect(host.sessionState('ann'), isNull);
    });

    test('addClient refuses a key that has a session', () async {
      final ann = connect(as: 'ann');
      await settle();
      await drop(ann);

      expect(
        () => host.addClient('ann', LoopbackLink().host),
        throwsStateError,
      );
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

    test('removeClient tells a live client and closes it', () async {
      final ann = Probe();
      host.accept(ann);
      ann.hear(encodeJoin(credentials('ann')));
      await settle();
      await host.removeClient('ann');

      expect(ann.goodbyes, [GoodbyeReason.removed]);
      expect(ann.closed, isTrue);
    });

    test('a throw in onJoin leaves a session that a drop can linger', () async {
      failJoin = true;
      final errors = <Object>[];
      late Connection ann;
      // The throw is the game's bug, so it surfaces rather than being
      // swallowed. The host stays usable.
      await runZonedGuarded(() async {
        ann = connect(as: 'ann');
        await settle();
      }, (error, _) => errors.add(error));
      await drop(ann);

      expect(errors, hasLength(1));

      expect(host.sessionState('ann'), SessionState.lingering);
      expect(host.clientCount, 0);
    });

    test('a throw in onGone still ends the session', () async {
      failGone = true;
      final ann = connect(as: 'ann');
      await settle();
      await drop(ann);
      await publish(2);

      expect(host.publish, throwsStateError);
      expect(gone, [('ann', GoneReason.disconnect)]);
      expect(host.sessionState('ann'), isNull);
      expect(host.ownerOf(spawned.first), isNull);
      expect(host.publish, returnsNormally);
      expect(gone, hasLength(1));
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

  group('policy', () {
    test('negative lingerTicks is refused', () {
      expect(
        () => SessionPolicy(admit: admitByName, lingerTicks: -1),
        throwsArgumentError,
      );
    });

    test('joinTicks under one is refused', () {
      expect(
        () => SessionPolicy(admit: admitByName, joinTicks: 0),
        throwsArgumentError,
      );
    });
  });
}
