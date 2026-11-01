# orblit-net

Multiplayer for [Orblit](https://github.com/ChxisB/orblit).

Replication reads component *columns* straight out of the engine, rather than
walking entities and asking each one what changed. So capturing five hundred
transforms is a handful of bulk copies rather than five hundred lookups. That
is the engine's storage decision paying for itself a second time.

```sh
git clone https://github.com/ChxisB/orblit.git       # beside this one
orblit/tool/pack_local.sh                              # packs the engine into ~/.orblit/feed
./tool/check_dotnet.sh
```

It needs the .NET 10 SDK. `ORBLIT_FEED` names another feed folder.

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
  `Publish`, `host.Summary()` says where the tick went, phase by phase, and
  how many bytes each client was sent for each component.

```csharp
var host = new NetHost(new NetHostOptions
{
    World = world,
    Set = set,
    NetworkId = networkId,
    Clock = new TickClock(TimeSpan.FromMilliseconds(50)),
    OnOverrun = summary => Console.WriteLine(summary.TickMax),
});
host.Phase("physics", StepPhysics);
host.Publish();
Console.WriteLine(host.Summary().Phases["encode"].Average);
```

## Load testing

```sh
dotnet run --project dotnet/Orblit.Load -c Release -- --clients 200 --seconds 60 --script walk
```

This runs a host with 200 scripted clients, each moving one entity, and
prints the tick, the drift, the time in each phase and the bytes each client
was sent per second. Scripts are `still`, `walk`, `random` and `scatter`.
`--sockets` uses WebSockets on localhost. `--slow-ms 20` adds a system that
takes 20 ms, to see a slow phase show up. `--help` lists the rest.

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

```csharp
var host = new NetHost(new NetHostOptions
{
    World = world,
    Set = set,
    NetworkId = networkId,
    Sessions = new SessionPolicy
    {
        Admit = async credentials => await accounts.CheckAsync(credentials), // Admit or Refuse
        LingerTicks = 600,
        OnJoin = key => SpawnPlayer(key),
        OnGone = (key, reason) => SavePlayer(key),
    },
});

server.Listen(host.Accept);
client.Join(credentials);
```

- `Admit` gets the bytes the client sent and answers `Admit(key)` or
  `Refuse(reason)`, as a `ValueTask`. A throw or a failed task refuses the
  join. Orblit checks nothing about who someone is. A task that waits suits a
  password hash or a database lookup. The host keeps ticking while it waits,
  `JoinTicks` still counts down, and a second join from the same connection
  is ignored. An answer for a connection that has since closed is dropped.
  `SocketServer` speaks plain `ws://`. Put a TLS proxy in front of it when
  the bytes must stay private.
- A refused client gets the reason in `OnGoodbye` and the connection closes.
  The engine's own reasons are the constants in `GoodbyeReason`.
- A connection that sends no join within `JoinTicks` hears `JoinTimeout` and
  is closed.
- The key is the client id, so `host.Spawn(entity, owner: key)` ties an
  entity to the player.
- When a connection drops, the session lingers. Its entities stay and no
  input arrives. `host.SessionStateOf(key)` answers `Live`, `Lingering` or
  null.
- A join with the same key during the linger resumes the session from a full
  snapshot. `OnJoin` does not run again.
- A second join for a live key is refused with `AlreadyLive`. With
  `TakeOver = true` the old connection hears `Replaced` and the new one
  resumes the session.
- After `LingerTicks` ticks, `OnGone(key, GoneReason.Disconnect)` runs once.
  `LingerFor(key)` picks the wait for one key when its connection drops.
  The key still owns its entities during the call, so the game can save and
  despawn them. Whatever is left then reverts to the authority.
- `RemoveClient(key)` ends a session at once with `GoneReason.Removed` and
  tells the client `Removed`.
- A throw in `OnGone` ends the session anyway, and propagates out of
  `Publish`. The next `Publish` carries on.
- `Dispose` ends no session and calls no `OnGone`. Save before it.

## Budgets

A host limits what each client may send. The limits are per tick, so the
clock stays yours. Divide by your tick rate to think in seconds.

```csharp
var host = new NetHost(new NetHostOptions
{
    World = world,
    Set = set,
    NetworkId = networkId,
    Budget = new InboundBudget(
        messagesPerTick: 100,
        bytesPerMessage: 64 * 1024,
        bytesPerTick: 256 * 1024,
        strikes: 10),
});
```

- A message over any limit is dropped before it is decoded. The host counts
  it in `Rejections[InputRejection.OverBudget]`. That counts messages, where
  the other reasons count entries inside an input.
- Everything counts: acknowledgements, inputs and the join. Leave room for
  one acknowledgement per tick, and keep `bytesPerMessage` above the size of
  your credentials.
- A tick with something dropped adds a strike to the client. A tick with
  nothing dropped takes one off. At `strikes` the client hears
  `GoodbyeReason.OverBudget` and is disconnected. A short burst does no harm,
  and a client cannot dodge the limit with the odd quiet tick.
- A session ends with `GoneReason.OverBudget`, so the game can tell a flood
  from `RemoveClient` and can refuse the key in `Admit`. A client you added
  with `AddClient` is closed and its entities revert to the host. A host
  with no session policy has no callback, so watch `ClientCount`.
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

```csharp
var host = new NetHost(new NetHostOptions
{
    World = world,
    Set = set,
    NetworkId = networkId,
    Interest = new GridInterest(set, position, new Grid(50), ring: 1),
});
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
- Any class that implements `IInterestRule` works in its place. `Observe`
  runs once a tick. `IsRelevant` runs once per entity per client, and not
  for entities the client owns.
- `host.Summary()` has an `interest` phase when there is a rule.
- With `--cell 50`, the load command tells each client only about its own
  cell. `--script scatter` spreads the clients over a map 1000 units across.

## Active cells

`ActiveCells` says which cells of the world are worth simulating, so a game
can skip work where nobody is near. It skips nothing itself. A system asks
and decides.

```csharp
var grid = new Grid(50);
var interest = new GridInterest(set, position, grid);
var active = new ActiveCells(grid, radius: 2, hold: TimeSpan.FromSeconds(30));

// Once a tick, before the systems run.
active.Update(interest.FocusCells);
if (active.IsActiveAt(x, z)) StepGuard(guard);
```

- A cell is active when it is within `radius` cells of a client's entity. It
  stays active through `hold` after that stops being true, so a player who
  steps back and forth over a border does not stop and start a region.
- The answer is as of the last `Update`, so every system in a tick sees the
  same cells. `FocusCells` is as of the last `Publish`.
- `Cells` lists the active cells, and `IsActive` takes a cell.
- `clock` defaults to a monotonic clock. A test passes its own.
- Use one `Grid` for both, so the cell size lives in one place.

## Game messages

A game can send its own bytes in either direction, beside the replicated
columns. Chat, a login result and an inventory change are not world state, so
they go this way.

```csharp
var host = new NetHost(new NetHostOptions
{
    World = world,
    Set = set,
    NetworkId = networkId,
    OnMessage = (clientId, body) => Handle(clientId, body),
});
host.SendMessage("ana", EncodeNotice("Welcome"));

var client = new NetClient(new NetClientOptions
{
    // ...
    OnMessage = body => Show(DecodeNotice(body)),
});
client.SendMessage(EncodeChat("hello"));
```

- The engine carries the bytes and reads nothing from them. The game decides
  what a body means.
- A message from a client counts against its `InboundBudget` before it is
  passed on. A message over a limit is dropped and counted.
- The bytes come from a client, so `OnMessage` must not throw on them.
- `SendMessage` to a client that is not attached, or is lingering, sends
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
