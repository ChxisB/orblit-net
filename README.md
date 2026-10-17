# orblit-net

Multiplayer for [Orblit](https://github.com/ChxisB/orblit).

Replication reads component *columns* straight out of the engine, rather than
walking entities and asking each one what changed. So capturing five hundred
transforms is a handful of bulk copies rather than five hundred lookups. That
is the engine's storage decision paying for itself a second time.

```sh
git clone https://github.com/ChxisB/orblit.git       # beside this one
./tool/link_local.sh                                   # point at that checkout
./tool/check.sh
```

## What it does

- **Snapshots and deltas.** Deltas are built against the tick a client
  acknowledged, not the last thing sent. So a dropped message costs one larger
  snapshot, rather than a world that quietly drifts apart.
- **Ownership.** A client proposes and the authority decides. Only components
  declared owner-writable, and only on entities it agrees that client owns.
  Every refusal is counted rather than thrown away.
- **Interpolation.** Renders a little behind, so remote entities move rather
  than jump. Floats blend, and integers take the earlier value.
- **Transports.** A loopback link, so a single-player build runs the real
  networked path, and a socket transport for actual play.

## Sessions

A host with a `SessionPolicy` admits clients through your function and keeps
their entities when a connection drops.

```dart
final host = NetHost(
  world: world,
  set: set,
  networkId: networkId,
  sessions: SessionPolicy(
    admit: (credentials) => accounts.check(credentials), // Admit or Refuse
    lingerTicks: 600,
    onJoin: (key) => spawnPlayer(key),
    onGone: (key, reason) => savePlayer(key),
  ),
);

server.connections.listen(host.accept);
client.join(credentials);
```

- `admit` gets the bytes the client sent and answers `Admit(key)` or
  `Refuse(reason)`. A throw refuses the join. Orblit checks nothing about who
  someone is. It answers at once, so check a ticket the game already holds.
  `SocketServer` speaks plain `ws://`. Put a TLS proxy in front of it when
  the bytes must stay private.
- A refused client gets the reason in `onGoodbye` and the connection closes.
  The engine's own reasons are the constants in `GoodbyeReason`.
- A connection that sends no join within `joinTicks` hears `joinTimeout` and
  is closed.
- The key is the client id, so `spawn(entity, owner: key)` ties an entity to
  the player.
- When a connection drops, the session lingers. Its entities stay and no
  input arrives. `host.sessionState(key)` answers `live`, `lingering` or null.
- A join with the same key during the linger resumes the session from a full
  snapshot. `onJoin` does not run again.
- A second join for a live key is refused with `alreadyLive`. With
  `takeOver: true` the old connection hears `replaced` and the new one
  resumes the session.
- After `lingerTicks` ticks, `onGone(key, GoneReason.disconnect)` runs once.
  The key still owns its entities during the call, so the game can save and
  despawn them. Whatever is left then reverts to the authority.
- `removeClient(key)` ends a session at once with `GoneReason.removed` and
  tells the client `removed`.
- A throw in `onGone` ends the session anyway, and propagates out of
  `publish`. The next `publish` carries on.
- `dispose` ends no session and calls no `onGone`. Save before it.

## Licence

MPL-2.0, © 2026 Chris Beckett. That is the Mozilla Public License, and it is
open source. Use it, fork it and ship games with it, including commercial ones.
Your game stays yours, and the licence does not reach into it. What it asks is
that changes to this repository's own files ship under the same licence, so
engine work stays in the open.

Nothing third-party ships inside this one. See [LICENSE](LICENSE).
