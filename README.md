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
- **Interest.** A rule picks the entities each client is told about, so bytes
  grow with what a client can see and not with the size of the world.
- **Ownership.** A client proposes and the authority decides. Only components
  declared owner-writable, and only on entities it agrees that client owns.
  Every refusal is counted rather than thrown away.
- **Interpolation.** Renders a little behind, so remote entities move rather
  than jump. Floats blend, and integers take the earlier value.
- **Transports.** A loopback link, so a single-player build runs the real
  networked path, and a socket transport for actual play.
- **Timing.** A `TickClock` keeps the tick rate and reports drift. After each
  `publish`, `host.summary()` says where the tick went, phase by phase, and
  how many bytes each client was sent for each component.

```dart
final host = NetHost(
  world: world,
  set: set,
  networkId: networkId,
  clock: TickClock(period: const Duration(milliseconds: 50)),
  onOverrun: (summary) => print(summary.tickMax),
);
host.phase('physics', stepPhysics);
host.publish();
print(host.summary().phases['encode']?.average);
```

## Load testing

```sh
dart run orblit_net:load --clients 200 --seconds 60 --script walk
```

This runs a host with 200 scripted clients, each moving one entity, and
prints the tick, the drift, the time in each phase and the bytes each client
was sent per second. Scripts are `still`, `walk`, `random` and `scatter`. `--sockets`
uses WebSockets on localhost. `--slow-ms 20` adds a system that takes 20 ms,
to see a slow phase show up. `--help` lists the rest.

The command exits 1 when the host's average tick is longer than the period,
and 2 when the host refused any input, since those figures are not a load.
Only the last 300 ticks are judged.

The clients share the host's thread. Input handling and the clients' own work
run between ticks, outside every phase, so they show up as drift. The phase
table is only the host's publish. When drift is large the report says the run
fell behind, and the clients are the likely cause. The byte rates include each
client's first full snapshot.

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

## Budgets

A host limits what each client may send. The limits are per tick, so the
clock stays yours. Divide by your tick rate to think in seconds.

```dart
final host = NetHost(
  world: world,
  set: set,
  networkId: networkId,
  budget: InboundBudget(
    messagesPerTick: 100,
    bytesPerMessage: 64 * 1024,
    bytesPerTick: 256 * 1024,
    strikes: 10,
  ),
);
```

- A message over any limit is dropped before it is decoded. The host counts
  it in `rejections[InputRejection.overBudget]`. That counts messages, where
  the other reasons count entries inside an input.
- Everything counts: acknowledgements, inputs and the join. Leave room for
  one acknowledgement per tick, and keep `bytesPerMessage` above the size of
  your credentials.
- A tick with something dropped adds a strike to the client. A tick with
  nothing dropped takes one off. At `strikes` the client hears
  `GoodbyeReason.overBudget` and is disconnected. A short burst does no harm,
  and a client cannot dodge the limit with the odd quiet tick.
- A session ends with `GoneReason.overBudget`, so the game can tell a flood
  from `removeClient` and can refuse the key in `admit`. A client you added
  with `addClient` is closed and its entities revert to the host. A host
  with no session policy has no callback, so watch `clientCount`.
- A connection that has not joined has one tick's budget in total, and is
  closed when it passes it.
- Every host has a default budget. The values above suit a host that ticks
  10 to 60 times a second. A slower host raises the per-tick limits.
- The transport has already received a message by the time the host
  drops it. The budget stops the host decoding and applying it.

## Interest

A host tells every client about every entity unless it has an interest rule.
`GridInterest` tells a client about the entities within `ring` cells of its
own.

```dart
final host = NetHost(
  world: world,
  set: set,
  networkId: networkId,
  interest: GridInterest(
    set: set,
    position: position,
    grid: Grid(50),
    ring: 1,
  ),
);
```

- The entities a client owns are its focus, and it always sees them. A
  client that owns two far apart sees around both.
- An entity that comes into view is sent whole, even if it has not changed
  since the tick the client acknowledged. One that goes out of view is a
  removal. A client that misses a message still converges, because each
  delta is built against what that client was shown.
- Position is the first and third floats of a vector of three, or the first
  two of a vector of two. An entity with no finite position is seen only by
  its owner.
- Any class that implements `InterestRule` works in its place. `observe`
  runs once a tick. `isRelevant` runs once per entity per client, and not
  for entities the client owns.
- `host.summary()` has an `interest` phase when there is a rule.
- With `--cell 50`, the load command tells each client only about its own
  cell. `--script scatter` spreads the clients over a map 1000 units across.

## Active cells

`ActiveCells` says which cells of the world are worth simulating, so a game
can skip work where nobody is near. It skips nothing itself. A system asks
and decides.

```dart
final grid = Grid(50);
final interest = GridInterest(set: set, position: position, grid: grid);
final active = ActiveCells(
  grid: grid,
  radius: 2,
  hold: const Duration(seconds: 30),
);

// Once a tick, before the systems run.
active.update(interest.focusCells);
if (active.isActiveAt(x, z)) stepGuard(guard);
```

- A cell is active when it is within `radius` cells of a client's entity. It
  stays active through `hold` after that stops being true, so a player who
  steps back and forth over a border does not stop and start a region.
- The answer is as of the last `update`, so every system in a tick sees the
  same cells. `focusCells` is as of the last `publish`.
- `cells` lists the active cells, and `isActive` takes a cell.
- `clock` defaults to a monotonic clock. A test passes its own.
- Use one `Grid` for both, so the cell size lives in one place.

## Game messages

A game can send its own bytes in either direction, beside the replicated
columns. Chat, a login result and an inventory change are not world state, so
they go this way.

```dart
final host = NetHost(
  world: world,
  set: set,
  networkId: networkId,
  onMessage: (clientId, body) => handle(clientId, body),
);
host.sendMessage('ana', encodeNotice('Welcome'));

final client = NetClient(
  // ...
  onMessage: (body) => show(decodeNotice(body)),
);
client.sendMessage(encodeChat('hello'));
```

- The engine carries the bytes and reads nothing from them. The game decides
  what a body means.
- A message from a client counts against its `InboundBudget` before it is
  passed on. A message over a limit is dropped and counted.
- The bytes come from a client, so `onMessage` must not throw on them.
- `sendMessage` to a client that is not attached, or is lingering, sends
  nothing.
- A message is as reliable as the transport. Both transports that ship keep
  order and lose nothing. A transport that can lose messages loses these too.

## Licence

MPL-2.0, © 2026 Chris Beckett. That is the Mozilla Public License, and it is
open source. Use it, fork it and ship games with it, including commercial ones.
Your game stays yours, and the licence does not reach into it. What it asks is
that changes to this repository's own files ship under the same licence, so
engine work stays in the open.

Nothing third-party ships inside this one. See [LICENSE](LICENSE).
