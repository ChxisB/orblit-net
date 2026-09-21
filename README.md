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

## Licence

MPL-2.0, © 2026 Chris Beckett. That is the Mozilla Public License, and it is
open source. Use it, fork it and ship games with it, including commercial ones.
Your game stays yours, and the licence does not reach into it. What it asks is
that changes to this repository's own files ship under the same licence, so
engine work stays in the open.

Nothing third-party ships inside this one. See [LICENSE](LICENSE).
