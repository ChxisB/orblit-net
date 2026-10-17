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
was sent per second. Scripts are `still`, `walk` and `random`. `--sockets`
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

## Licence

MPL-2.0, © 2026 Chris Beckett. That is the Mozilla Public License, and it is
open source. Use it, fork it and ship games with it, including commercial ones.
Your game stays yours, and the licence does not reach into it. What it asks is
that changes to this repository's own files ship under the same licence, so
engine work stays in the open.

Nothing third-party ships inside this one. See [LICENSE](LICENSE).
