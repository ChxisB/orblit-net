# Changelog

## 0.3.0

- Add `dart run orblit_net:load`. It runs a host with scripted clients on a
  real tick clock and prints where the tick went and how many bytes each
  client was sent. It exits 1 when the host's average tick is longer than the
  period, and 2 when the host refused any input.
- Add `package:orblit_net/load.dart` with `runLoad`, `LoadOptions`,
  `LoadReport` and `parseLoadArgs`. A game can pass its own movement function,
  and a function that stands in for its server systems, which is timed as the
  `system` phase.
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
