import 'interest.dart';
import 'tick_clock.dart';

/// Which cells of a [Grid] are worth simulating: those within [radius] cells
/// of a focus, and for [hold] after they stop being.
///
/// It skips nothing itself. A system asks and decides, since stepping means
/// different things to physics, to AI and to growth. The answer is as of the
/// last [update], so every system in a tick sees the same cells.
final class ActiveCells {
  ActiveCells({
    required this.grid,
    required this.radius,
    required this.hold,
    Duration Function()? clock,
  }) : _now = clock ?? monotonicNow() {
    if (radius < 0) throw ArgumentError.value(radius, 'radius', 'below zero');
    if (hold.isNegative) throw ArgumentError.value(hold, 'hold', 'negative');
  }

  final Grid grid;

  /// How many cells out from a focus cell are active. Zero is the focus cell
  /// alone.
  final int radius;

  /// How long a cell stays active once nothing is near it. A focus that
  /// steps back and forth over a border does not stop and start a cell.
  final Duration hold;

  final Duration Function() _now;
  final Map<Cell, Duration> _activeUntil = {};

  /// The cells that are active as of the last [update]. Do not call [update]
  /// while iterating it.
  Iterable<Cell> get cells => _activeUntil.keys;

  bool isActive(Cell cell) => _activeUntil.containsKey(cell);

  /// Whether the cell holding ([x], [z]) is active. A place that is not a
  /// finite number is in no cell, so it is not active.
  bool isActiveAt(double x, double z) {
    final cell = grid.cellOf(x, z);
    return cell != null && isActive(cell);
  }

  /// Takes the cells the focus entities stand in, once a tick. A cell near
  /// any of them is active through [hold] from now. A cell is dropped once
  /// that has passed.
  void update(Iterable<Cell> focus) {
    final now = _now();
    _activeUntil.removeWhere((_, until) => until < now);
    for (final centre in focus) {
      for (var dx = -radius; dx <= radius; dx++) {
        for (var dz = -radius; dz <= radius; dz++) {
          _activeUntil[(x: centre.x + dx, z: centre.z + dz)] = now + hold;
        }
      }
    }
  }
}
