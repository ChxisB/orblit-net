# Changelog

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
