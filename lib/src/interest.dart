import 'dart:typed_data';

import 'package:orblit_core/orblit_core.dart';

import 'host.dart';
import 'replication.dart';
import 'snapshot.dart';

/// One square of a [Grid], by how many cells it is from the origin.
typedef Cell = ({int x, int z});

/// Square cells on the ground plane. Interest and active cells share one, so
/// the cell size lives in one place.
final class Grid {
  Grid(this.cellSize) {
    if (!cellSize.isFinite || cellSize <= 0) {
      throw ArgumentError.value(cellSize, 'cellSize', 'not above zero');
    }
  }

  final double cellSize;

  /// The cell holding ([x], [z]), or null when either is not a finite number.
  Cell? cellOf(double x, double z) {
    if (!x.isFinite || !z.isFinite) return null;
    return (x: (x / cellSize).floor(), z: (z / cellSize).floor());
  }
}

/// Tells a client about the entities within [ring] cells of its own.
///
/// An entity's place is its [position] component, read from the capture: the
/// first and third floats of a vector of three, or the first two of a vector
/// of two. A client's own entities are its focus, and it sees around each of
/// them. An entity with no finite position is seen only by its owner.
final class GridInterest implements InterestRule {
  GridInterest({
    required this.set,
    required this.position,
    required this.grid,
    this.ring = 1,
  }) : _bit = set.bitOf(position) ?? -1 {
    if (_bit < 0) {
      throw ArgumentError.value(
        position.name,
        'position',
        'does not replicate',
      );
    }
    if (position.kind != ComponentKind.float32 || position.arity < 2) {
      throw ArgumentError.value(
        position.name,
        'position',
        'wants two or more float32s',
      );
    }
    if (ring < 0) throw ArgumentError.value(ring, 'ring', 'below zero');
  }

  final ReplicationSet set;
  final ComponentType position;
  final Grid grid;

  /// How many cells out from a focus cell count as near. Zero is the focus
  /// cell alone.
  final int ring;

  final int _bit;
  final Map<int, Cell> _cells = {};
  final Map<String, List<Cell>> _focus = {};

  /// The cells clients stood in at the last [observe], each once.
  Iterable<Cell> get focusCells => {for (final c in _focus.values) ...c};

  @override
  void observe(WorldSnapshot world, Map<String, List<int>> focus) {
    _cells.clear();
    for (final group in world.groups) {
      if (group.mask & (1 << _bit) != 0) _readCells(group);
    }
    _focus.clear();
    for (final entry in focus.entries) {
      _focus[entry.key] = [
        for (final networkId in entry.value)
          if (_cells[networkId] case final cell?) cell,
      ];
    }
  }

  @override
  bool isRelevant(String clientId, int networkId) {
    final cell = _cells[networkId];
    final near = _focus[clientId];
    if (cell == null || near == null) return false;
    for (final focus in near) {
      if ((focus.x - cell.x).abs() <= ring &&
          (focus.z - cell.z).abs() <= ring) {
        return true;
      }
    }
    return false;
  }

  void _readCells(SnapshotGroup group) {
    final before = set.strideOf(group.mask & ((1 << _bit) - 1));
    final second = position.arity == 2 ? 4 : 8;
    final data = ByteData.sublistView(group.rows);
    for (var i = 0; i < group.length; i++) {
      final start = i * group.stride + before;
      final cell = grid.cellOf(
        data.getFloat32(start, Endian.little),
        data.getFloat32(start + second, Endian.little),
      );
      if (cell != null) _cells[group.networkIds[i]] = cell;
    }
  }
}
