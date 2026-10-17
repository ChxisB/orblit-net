# Changelog

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
