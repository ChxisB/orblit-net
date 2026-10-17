import 'dart:typed_data';

import 'package:orblit_core/orblit_core.dart';
import 'package:orblit_net/orblit_net.dart';
import 'package:test/test.dart';

import 'support/loopback.dart';

final class Side {
  Side() {
    networkId = world.registerComponent('NetworkId', kind: ComponentKind.int64);
    position = world.registerComponent(
      'Position',
      kind: ComponentKind.float32,
      arity: 3,
    );
    set = ReplicationSet([position]);
  }

  final World world = World();
  late final ComponentType networkId;
  late final ComponentType position;
  late final ReplicationSet set;
}

/// Lets the test lose what the host sends.
final class Lossy implements Transport {
  Lossy(this._inner);

  final Transport _inner;
  bool dropping = false;

  @override
  Stream<Uint8List> get inbound => _inner.inbound;

  @override
  void send(Uint8List message) {
    if (!dropping) _inner.send(message);
  }

  @override
  Future<void> close() => _inner.close();
}

final class Player {
  Player() : link = LoopbackLink() {
    lossy = Lossy(link.host);
    client = NetClient(
      world: side.world,
      set: side.set,
      networkId: side.networkId,
      transport: link.client,
      onDespawn: (id, _) => despawned.add(id),
    );
  }

  final Side side = Side();
  final LoopbackLink link;
  final List<int> despawned = [];
  late final Lossy lossy;
  late final NetClient client;

  bool sees(int networkId) => client.entityFor(networkId) != null;

  double xOf(int networkId) {
    final bytes = side.world.bytesOf(
      client.entityFor(networkId)!,
      side.position,
    )!;
    return ByteData.sublistView(bytes).getFloat32(0, Endian.little);
  }
}

/// Says no to everything, so only ownership can put an entity in an update.
final class Nothing implements InterestRule {
  @override
  void observe(WorldSnapshot world, Map<String, List<int>> focus) {}

  @override
  bool isRelevant(String clientId, int networkId) => false;
}

/// Says yes to everything and counts the questions.
final class Counting implements InterestRule {
  int questions = 0;

  @override
  void observe(WorldSnapshot world, Map<String, List<int>> focus) {}

  @override
  bool isRelevant(String clientId, int networkId) {
    questions++;
    return true;
  }
}

void main() {
  late Side authority;
  late NetHost host;
  final players = <Player>[];

  void build(InterestRule rule) {
    host = NetHost(
      world: authority.world,
      set: authority.set,
      networkId: authority.networkId,
      interest: rule,
    );
  }

  GridInterest grid({int ring = 1}) => GridInterest(
    set: authority.set,
    position: authority.position,
    grid: Grid(10),
    ring: ring,
  );

  void place(int networkId, double x, [double z = 0]) {
    final bytes = authority.world.bytesOf(
      host.entityFor(networkId)!,
      authority.position,
    )!;
    ByteData.sublistView(bytes)
      ..setFloat32(0, x, Endian.little)
      ..setFloat32(8, z, Endian.little);
  }

  int spawnAt(double x, {String? owner, double z = 0}) {
    final entity = authority.world.createEntity();
    authority.world.add(entity, authority.position);
    final networkId = host.spawn(entity, owner: owner);
    place(networkId, x, z);
    return networkId;
  }

  Player join(String key) {
    final player = Player();
    players.add(player);
    addTearDown(player.link.close);
    host.addClient(key, player.lossy);
    return player;
  }

  Future<void> tick() async {
    host.publish();
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

  group('GridInterest', () {
    test(
      'an entity crossing from cell 0 to cell 5 leaves one, joins other',
      () async {
        build(grid());
        final a = join('a');
        final b = join('b');
        spawnAt(1, owner: 'a');
        spawnAt(55, owner: 'b');
        final walker = spawnAt(2);

        await tick();
        expect(a.sees(walker), isTrue);
        expect(b.sees(walker), isFalse);

        place(walker, 52);
        await tick();

        expect(a.sees(walker), isFalse);
        expect(a.despawned, [walker]);
        expect(b.sees(walker), isTrue);
        expect(b.xOf(walker), 52);
      },
    );

    test(
      'an entity entering the area is sent whole though it is unchanged',
      () async {
        build(grid());
        final a = join('a');
        final avatar = spawnAt(1, owner: 'a');
        final statue = spawnAt(55);

        await tick();
        expect(a.sees(statue), isFalse);

        place(avatar, 52);
        await tick();

        expect(a.sees(statue), isTrue);
        expect(a.xOf(statue), 55);
      },
    );

    test('a ring of two reaches two cells away and no further', () async {
      build(grid(ring: 2));
      final a = join('a');
      spawnAt(5, owner: 'a');
      final near = spawnAt(25);
      final far = spawnAt(35);

      await tick();

      expect(a.sees(near), isTrue);
      expect(a.sees(far), isFalse);
    });

    test('cells reach across zero and across the other axis', () async {
      build(grid());
      final a = join('a');
      spawnAt(1, owner: 'a', z: 1);
      final left = spawnAt(-1, z: -1);
      final beyond = spawnAt(-11, z: 1);
      final north = spawnAt(1, z: 19);
      final further = spawnAt(1, z: 21);

      await tick();

      expect(a.sees(left), isTrue);
      expect(a.sees(beyond), isFalse);
      expect(a.sees(north), isTrue);
      expect(a.sees(further), isFalse);
    });

    test(
      'an entity with no position is seen by nobody but its owner',
      () async {
        build(grid());
        final a = join('a');
        spawnAt(1, owner: 'a');
        final entity = authority.world.createEntity();
        authority.world.add(entity, authority.position);
        final nowhere = host.spawn(entity);
        final bytes = authority.world.bytesOf(entity, authority.position)!;
        ByteData.sublistView(bytes).setFloat32(0, double.nan, Endian.little);

        await tick();

        expect(a.sees(nowhere), isFalse);
      },
    );

    test('a client owning two far apart entities sees around both', () async {
      build(grid());
      final a = join('a');
      spawnAt(1, owner: 'a');
      spawnAt(101, owner: 'a');
      final first = spawnAt(3);
      final second = spawnAt(103);
      final between = spawnAt(51);

      await tick();

      expect(a.sees(first), isTrue);
      expect(a.sees(second), isTrue);
      expect(a.sees(between), isFalse);
    });

    test('lists the cells its clients stand in once each', () async {
      final rule = grid();
      build(rule);
      join('a');
      join('b');
      join('c');
      spawnAt(1, owner: 'a');
      spawnAt(2, owner: 'b');
      spawnAt(55, owner: 'c');

      await tick();

      expect(rule.focusCells.toSet(), {(x: 0, z: 0), (x: 5, z: 0)});
      expect(rule.focusCells, hasLength(2));
    });

    test('rejects a rule that cannot read positions', () {
      final other = Side();
      addTearDown(other.world.dispose);
      final unreplicated = other.world.registerComponent(
        'Velocity',
        kind: ComponentKind.float32,
        arity: 3,
      );
      final flat = other.world.registerComponent(
        'Health',
        kind: ComponentKind.int32,
      );
      expect(
        () => GridInterest(
          set: other.set,
          position: unreplicated,
          grid: Grid(10),
        ),
        throwsArgumentError,
      );
      expect(
        () => GridInterest(
          set: ReplicationSet([flat]),
          position: flat,
          grid: Grid(10),
        ),
        throwsArgumentError,
      );
      expect(
        () => GridInterest(
          set: other.set,
          position: other.position,
          grid: Grid(10),
          ring: -1,
        ),
        throwsArgumentError,
      );
    });

    test('a grid needs a cell size above zero', () {
      expect(() => Grid(0), throwsArgumentError);
      expect(() => Grid(-1), throwsArgumentError);
      expect(() => Grid(double.nan), throwsArgumentError);
      expect(() => Grid(double.infinity), throwsArgumentError);
    });
  });

  group('the host with a rule', () {
    test('an owner sees its own entity whatever the rule says', () async {
      build(Nothing());
      final a = join('a');
      final avatar = spawnAt(1, owner: 'a');
      final stranger = spawnAt(2);

      await tick();

      expect(a.sees(avatar), isTrue);
      expect(a.sees(stranger), isFalse);
    });

    test('asks once per entity per client and not about owned ones', () async {
      final rule = Counting();
      build(rule);
      join('a');
      join('b');
      spawnAt(1, owner: 'a');
      spawnAt(2, owner: 'b');
      spawnAt(3);
      spawnAt(4);
      spawnAt(5);

      await tick();

      // Five entities, each client owns one of them.
      expect(rule.questions, 2 * 4);
    });

    test('a client that misses a delta still converges', () async {
      build(grid());
      final a = join('a');
      spawnAt(1, owner: 'a');
      final walker = spawnAt(55);
      await tick();
      expect(a.sees(walker), isFalse);

      a.lossy.dropping = true;
      place(walker, 4);
      await tick();
      expect(a.sees(walker), isFalse);

      a.lossy.dropping = false;
      await tick();
      expect(a.sees(walker), isTrue);
      expect(a.xOf(walker), 4);

      a.lossy.dropping = true;
      place(walker, 85);
      await tick();
      expect(a.sees(walker), isTrue);

      a.lossy.dropping = false;
      await tick();
      expect(a.sees(walker), isFalse);
      expect(a.despawned, [walker]);
    });

    test(
      'a first message is a full snapshot with only what the client sees',
      () async {
        build(grid());
        spawnAt(1, owner: 'a');
        spawnAt(55, owner: 'b');
        final nearA = spawnAt(3);
        final nearB = spawnAt(57);
        final a = join('a');
        await tick();
        await tick();

        final b = join('b');
        await tick();

        expect(b.sees(nearB), isTrue);
        expect(b.sees(nearA), isFalse);
        expect(a.sees(nearA), isTrue);
        expect(b.client.entityCount, 2);
      },
    );

    test('a client far behind is sent a filtered full snapshot', () async {
      host = NetHost(
        world: authority.world,
        set: authority.set,
        networkId: authority.networkId,
        interest: grid(),
        historyDepth: 3,
      );
      final a = join('a');
      spawnAt(1, owner: 'a');
      final near = spawnAt(3);
      final far = spawnAt(55);
      await tick();

      a.lossy.dropping = true;
      for (var i = 0; i < 6; i++) {
        await tick();
      }
      a.lossy.dropping = false;
      place(near, 4);
      await tick();

      expect(a.xOf(near), 4);
      expect(a.sees(far), isFalse);
    });

    test(
      'an entity that dies is a removal for the client that saw it',
      () async {
        build(grid());
        final a = join('a');
        spawnAt(1, owner: 'a');
        final mortal = spawnAt(2);
        await tick();
        expect(a.sees(mortal), isTrue);

        host.despawn(host.entityFor(mortal)!);
        await tick();

        expect(a.sees(mortal), isFalse);
        expect(a.despawned, [mortal]);
      },
    );
  });
}
