# Changelog

## 0.3.0

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

## 0.2.0

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

## 0.1.0

- Snapshots and deltas, ownership, interpolation, loopback and socket
  transports.
