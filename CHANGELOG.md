# Changelog

## 0.8.0

- Add game messages. `NetClient.sendMessage` and `NetHost.sendMessage` carry
  bytes the game defines. `NetHost.onMessage` and `NetClient.onMessage`
  receive them. A message from a client counts against its `InboundBudget`.

## 0.7.0

- Add `ActiveCells`. It takes the cells clients stand in once a tick, as
  `GridInterest.focusCells` gives them, and says which cells are within
  `radius` of one. A cell stays active through `hold` after nothing is near
  it. A game asks `isActive`, `isActiveAt` or `cells`, and decides what to
  skip. The clock is a parameter.

## 0.6.0

- Add `InterestRule` and `NetHost.interest`. With a rule, each client is
  told about the entities the rule lets through, and about the entities it
  owns. One that comes into view is sent whole. One that goes out of view is
  a removal.
- Add `GridInterest`, `Grid` and `Cell`. A client sees the entities within
  `ring` cells of its own entities.
- A host now keeps what each client was sent, and builds a delta against
  that. `historyDepth` counts those per client. A host with no rule behaves
  as before.
- The `history` phase is now `forget`, and there is an `interest` phase when
  a rule is set.
- Add `WorldSnapshot.filtered` and `SnapshotGroup.pick`.
- `dart run orblit_net:load` takes `--cell N` and has a `scatter` script.
  `LoadOptions` takes `cellSize`.

## 0.5.0

- Add `InboundBudget` and `NetHost.budget`. A host drops a message over a
  client's budget before decoding it and counts it as
  `InputRejection.overBudget`. A tick with something dropped adds a strike
  and a clean tick takes one off. At `strikes` the client hears
  `GoodbyeReason.overBudget` and is disconnected.
- Every `NetHost` now has a default budget, loose enough for 10 to 60 ticks
  a second. A host that ticks slower, or whose clients send more than 100
  messages a tick, passes its own `InboundBudget`.
- Add `InputRejection.overBudget`. A `switch` over `InputRejection` needs a
  new case.
- Add `GoneReason.overBudget`. A session ends with it when its client is cut
  off.
- A connection that has not joined has one tick's budget in total. A join
  over `bytesPerMessage` is refused before `admit` is called.

## 0.4.0

- Add `SessionPolicy`, `Admission` (`Admit` and `Refuse`), `SessionState`,
  `GoneReason` and `GoodbyeReason`. A host with a policy admits clients
  through the game's function, keeps a dropped client's entities for
  `lingerTicks` ticks, and resumes the session when the same key joins
  again.
- Add `NetHost.accept`, `NetHost.sessionState` and `NetClient.join`. A client
  that is turned away gets the reason in `NetClient.onGoodbye`.
- `NetHost.removeClient` ends a session as `GoneReason.removed` and tells
  the client.
- A connection that sends no join within `joinTicks` is closed.

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
