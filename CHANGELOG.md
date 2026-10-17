# Changelog

## 0.3.0

- Add `dart run orblit_net:load`. It runs a host with scripted clients on a
  real tick clock and prints where the tick went and how many bytes each
  client was sent. It exits 1 when the average tick is longer than the
  period.
- Add `package:orblit_net/load.dart` with `runLoad`, `LoadOptions` and
  `LoadReport`. A game passes its own function from tick to input, and its
  own system, which is timed as the `system` phase.
- `tool/check.sh` formats `bin` as well.

## 0.2.0

- Add `TickClock`. It keeps a fixed schedule, reports how late each tick
  started, and restarts from now when it falls more than two periods behind.
- `NetHost.publish` times `capture`, `encode`, `send`, `count` and
  `history`. `NetHost.phase` times a game's own systems beside them.
- `NetHost.summary` answers tick and drift averages and maximums, per phase
  totals, and bytes sent to each client by component column.
- `NetHost` takes `clock` and `onOverrun`. A tick longer than the clock's
  period calls the hook, at most once every five seconds.

## 0.1.0

- Snapshots and deltas, ownership, interpolation, loopback and socket
  transports.
