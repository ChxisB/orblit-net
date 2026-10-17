import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:orblit_core/orblit_core.dart';
import 'package:orblit_net/orblit_net.dart';
import 'package:test/test.dart';

import 'support/loopback.dart';

/// A world with a position column the owner may write.
final class Side {
  Side() {
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

/// One client: its own world, a loopback link, and what the host told it.
final class Player {
  Player() : link = LoopbackLink() {
    client = NetClient(
      world: side.world,
      set: side.set,
      networkId: side.networkId,
      transport: link.client,
      onGoodbye: reasons.add,
    );
  }

  final Side side = Side();
  final LoopbackLink link;
  final List<String> reasons = [];
  late final NetClient client;
  late final int networkId;

  /// One input that moves the player's own entity.
  Uint8List move() => encodeInput(
    InputMessage(
      tick: 1,
      entries: [
        InputEntry(
          networkId: networkId,
          mask: 1,
          row: Float32List.fromList([1, 2, 3]).buffer.asUint8List(),
        ),
      ],
    ),
  );

  void flood(int messages) {
    final input = move();
    for (var i = 0; i < messages; i++) {
      link.client.send(input);
    }
  }
}

void main() {
  late Side authority;
  late NetHost host;
  final players = <Player>[];

  NetHost build({InboundBudget? budget, SessionPolicy? sessions}) {
    return NetHost(
      world: authority.world,
      set: authority.set,
      networkId: authority.networkId,
      budget: budget,
      sessions: sessions,
    );
  }

  int spawnFor(String key) {
    final entity = authority.world.createEntity();
    authority.world.add(entity, authority.position);
    return host.spawn(entity, owner: key);
  }

  Player addPlayer(String key) {
    final player = Player();
    players.add(player);
    addTearDown(player.link.close);
    host.addClient(key, player.link.host);
    player.networkId = spawnFor(key);
    return player;
  }

  Future<void> publish(int times) async {
    for (var i = 0; i < times; i++) {
      host.publish();
    }
    await settle();
  }

  setUp(() {
    authority = Side();
  });

  tearDown(() async {
    await host.dispose();
    authority.world.dispose();
    for (final player in players) {
      player.side.world.dispose();
    }
    players.clear();
  });

  test('a thousand messages in a tick let only the budget through', () async {
    host = build(budget: InboundBudget(messagesPerTick: 10));
    final ann = addPlayer('ann');
    ann.flood(1000);
    await settle();

    expect(host.acceptedInputs, 10);
    expect(host.rejections[InputRejection.overBudget], 990);
  });

  test('the budget starts again each tick', () async {
    host = build(budget: InboundBudget(messagesPerTick: 10));
    final ann = addPlayer('ann');
    ann.flood(15);
    await settle();
    await publish(1);
    ann.flood(15);
    await settle();

    // The second tick has the ack for the snapshot in it, so nine inputs fit.
    expect(host.acceptedInputs, 19);
  });

  test('an input under bytesPerMessage is applied', () async {
    host = build();
    final ann = addPlayer('ann');
    ann.link.client.send(ann.move());
    await settle();

    expect(host.acceptedInputs, 1);
  });

  test('an input over bytesPerMessage is not applied', () async {
    host = build(budget: InboundBudget(bytesPerMessage: 40));
    final ann = addPlayer('ann');
    expect(ann.move().length, greaterThan(40));
    ann.link.client.send(ann.move());
    await settle();

    expect(host.acceptedInputs, 0);
    expect(host.rejections[InputRejection.overBudget], 1);
  });

  test('a burst shorter than strikes does not disconnect', () async {
    host = build(budget: InboundBudget(messagesPerTick: 5, strikes: 3));
    final ann = addPlayer('ann');

    for (final flooding in [true, true, false, false, true, true]) {
      if (flooding) ann.flood(20);
      await settle();
      await publish(1);
    }

    expect(host.clientCount, 1);
    expect(ann.reasons, isEmpty);
  });

  test('a flooder who pauses now and then is still cut off', () async {
    host = build(budget: InboundBudget(messagesPerTick: 5, strikes: 3));
    final ann = addPlayer('ann');

    for (final flooding in [true, true, false, true, true]) {
      if (flooding) ann.flood(20);
      await settle();
      await publish(1);
    }

    expect(ann.reasons, [GoodbyeReason.overBudget]);
    expect(host.clientCount, 0);
  });

  test('a sustained breach disconnects and frees what it owned', () async {
    host = build(budget: InboundBudget(messagesPerTick: 5, strikes: 3));
    final ann = addPlayer('ann');
    final bob = addPlayer('bob');

    for (var round = 0; round < 3; round++) {
      ann.flood(20);
      await settle();
      await publish(1);
    }

    expect(ann.reasons, [GoodbyeReason.overBudget]);
    expect(host.clientCount, 1);
    expect(host.ownerOf(ann.networkId), isNull);
    expect(bob.reasons, isEmpty);
    expect(bob.client.entityCount, 2);
  });

  test('a flooding session ends as overBudget and cannot resume', () async {
    final gone = <(String, GoneReason)>[];
    host = build(
      budget: InboundBudget(messagesPerTick: 5, strikes: 2),
      sessions: SessionPolicy(
        admit: (bytes) => Admit(utf8.decode(bytes)),
        onGone: (key, reason) => gone.add((key, reason)),
      ),
    );
    final ann = Player();
    players.add(ann);
    addTearDown(ann.link.close);
    host.accept(ann.link.host);
    ann.client.join(Uint8List.fromList(utf8.encode('ann')));
    await settle();
    ann.networkId = spawnFor('ann');

    for (var round = 0; round < 2; round++) {
      ann.flood(20);
      await settle();
      await publish(1);
    }

    expect(ann.reasons, [GoodbyeReason.overBudget]);
    expect(gone, [('ann', GoneReason.overBudget)]);
    expect(host.sessionState('ann'), isNull);
  });

  group('before the join', () {
    late List<String> asked;

    Player connect(InboundBudget budget) {
      asked = [];
      host = build(
        budget: budget,
        sessions: SessionPolicy(
          admit: (bytes) {
            asked.add('asked');
            return Admit(utf8.decode(bytes));
          },
        ),
      );
      final stranger = Player();
      players.add(stranger);
      addTearDown(stranger.link.close);
      host.accept(stranger.link.host);
      return stranger;
    }

    test('a join over bytesPerMessage never reaches admit', () async {
      final stranger = connect(InboundBudget(bytesPerMessage: 20));
      stranger.client.join(Uint8List(100));
      await settle();

      expect(stranger.reasons, [GoodbyeReason.overBudget]);
      expect(asked, isEmpty);
      expect(host.rejections[InputRejection.overBudget], 1);
    });

    test('a stream of junk is closed once it passes the budget', () async {
      final stranger = connect(InboundBudget(messagesPerTick: 10));
      for (var i = 0; i < 1000; i++) {
        stranger.link.client.send(Uint8List(16));
      }
      await settle();

      expect(stranger.reasons, [GoodbyeReason.overBudget]);
      expect(host.rejections[InputRejection.overBudget], 1);
    });
  });
}
