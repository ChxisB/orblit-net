import 'dart:typed_data';

import 'package:orblit_core/orblit_core.dart';
import 'package:orblit_net/orblit_net.dart';
import 'package:test/test.dart';

Cell cell(int x, int z) => (x: x, z: z);

void main() {
  group('ActiveCells', () {
    late Duration now;
    late ActiveCells active;

    ActiveCells build({int radius = 1, int hold = 5}) => ActiveCells(
      grid: Grid(10),
      radius: radius,
      hold: Duration(seconds: hold),
      clock: () => now,
    );

    void at(int seconds, List<Cell> focus) {
      now = Duration(seconds: seconds);
      active.update(focus);
    }

    setUp(() {
      now = Duration.zero;
      active = build();
    });

    test('a cell turns active when a client arrives, and so do its ring', () {
      expect(active.isActive(cell(0, 0)), isFalse);

      at(0, [cell(0, 0)]);

      expect(active.isActive(cell(0, 0)), isTrue);
      expect(active.isActive(cell(1, -1)), isTrue);
      expect(active.isActive(cell(2, 0)), isFalse);
      expect(active.cells, hasLength(9));
    });

    test('it stays active through hold after the client leaves', () {
      at(0, [cell(0, 0)]);

      at(4, []);
      expect(active.isActive(cell(0, 0)), isTrue);
      at(5, []);
      expect(active.isActive(cell(0, 0)), isTrue);
      at(6, []);
      expect(active.isActive(cell(0, 0)), isFalse);
      expect(active.cells, isEmpty);
    });

    test('two clients keep a cell active between them until both are gone', () {
      at(0, [cell(0, 0), cell(2, 0)]);
      expect(active.isActive(cell(1, 0)), isTrue);

      at(10, [cell(2, 0)]);
      expect(active.isActive(cell(1, 0)), isTrue);
      expect(active.isActive(cell(-1, 0)), isFalse);

      at(15, []);
      expect(active.isActive(cell(1, 0)), isTrue);
      at(16, []);
      expect(active.isActive(cell(1, 0)), isFalse);
    });

    test('a focus stepping back and forth does not stop the cell', () {
      at(0, [cell(0, 0)]);
      at(3, [cell(5, 5)]);
      at(4, [cell(0, 0)]);
      at(8, [cell(5, 5)]);

      expect(active.isActive(cell(0, 0)), isTrue);
      at(9, [cell(5, 5)]);
      expect(active.isActive(cell(0, 0)), isTrue);
      at(10, [cell(5, 5)]);
      expect(active.isActive(cell(0, 0)), isFalse);
    });

    test('a radius of zero is the focus cell alone', () {
      active = build(radius: 0);
      at(0, [cell(3, 4)]);

      expect(active.cells.toList(), [cell(3, 4)]);
    });

    test('asking by place finds the cell, and a bad place is not active', () {
      at(0, [cell(0, 0)]);

      expect(active.isActiveAt(5, 5), isTrue);
      expect(active.isActiveAt(-5, 15), isTrue);
      expect(active.isActiveAt(25, 5), isFalse);
      expect(active.isActiveAt(double.nan, 5), isFalse);
    });

    test('a radius below zero or a hold below zero is refused', () {
      expect(
        () => ActiveCells(grid: Grid(10), radius: -1, hold: Duration.zero),
        throwsArgumentError,
      );
      expect(
        () => ActiveCells(
          grid: Grid(10),
          radius: 1,
          hold: const Duration(seconds: -1),
        ),
        throwsArgumentError,
      );
    });
  });

  test('an AI system steps only the entities in active cells', () async {
    final world = World();
    final networkId = world.registerComponent(
      'NetworkId',
      kind: ComponentKind.int64,
    );
    final position = world.registerComponent(
      'Position',
      kind: ComponentKind.float32,
      arity: 3,
    );
    final set = ReplicationSet([position]);
    final grid = Grid(10);
    final interest = GridInterest(set: set, position: position, grid: grid);
    final host = NetHost(
      world: world,
      set: set,
      networkId: networkId,
      interest: interest,
    );
    var now = Duration.zero;
    final active = ActiveCells(
      grid: grid,
      radius: 1,
      hold: const Duration(seconds: 5),
      clock: () => now,
    );

    int spawn(double x, {String? owner}) {
      final entity = world.createEntity();
      world.add(entity, position, Float32List.fromList([x, 0, 0]));
      return host.spawn(entity, owner: owner);
    }

    final link = LoopbackLink();
    host.addClient('a', link.host);
    spawn(1, owner: 'a');
    final near = spawn(15);
    final far = spawn(95);

    // The AI system: wander each guard once, if its cell is active.
    final stepped = <int>[];
    void ai() {
      for (final id in [near, far]) {
        final bytes = world.bytesOf(host.entityFor(id)!, position)!;
        final x = ByteData.sublistView(bytes).getFloat32(0, Endian.little);
        if (active.isActiveAt(x, 0)) stepped.add(id);
      }
    }

    // The focus is as of the last publish, so the first tick publishes
    // before the system looks.
    host.publish();
    active.update(interest.focusCells);
    ai();
    expect(stepped, [near]);

    // The client's avatar walks away. The old cells hold for five seconds.
    world
        .bytesOf(host.entityFor(1)!, position)!
        .buffer
        .asByteData()
        .setFloat32(0, 90, Endian.little);
    host.publish();
    now = const Duration(seconds: 3);
    active.update(interest.focusCells);
    stepped.clear();
    ai();
    expect(stepped, [near, far]);

    now = const Duration(seconds: 9);
    active.update(interest.focusCells);
    stepped.clear();
    ai();
    expect(stepped, [far]);

    await host.dispose();
    await link.close();
    world.dispose();
  });
}
