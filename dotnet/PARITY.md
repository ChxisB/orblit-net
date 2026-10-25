# Parity

One row per package that has been ported. A row is added when a part is built.

Declared counts the test declarations in the source. Run counts the tests that
execute, because a loop or a theory runs one declaration many times. A package
is done when the two Run counts match.

The rules are the engine's `dotnet/PORTING.md`, in the `orblit` repository. The
engine packages come from a folder. The engine's `tool/pack_local.sh` packs
them, into `~/.orblit/feed` unless `ORBLIT_FEED` names another.
`Directory.Packages.props` pins the versions.

| Package | Dart declared | Dart run | C# declared | C# run | Fixtures | Added in C# | Status |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `orblit_net` | 178 | 178 | 187 | 187 | none, because the tests build both sides and compare worlds. `CrossJoinTests` start the Dart package live from `tool/cross_peer.dart` | `CrossJoinTests` (2): a Dart host with a C# client, and a C# host with a Dart client, over a socket for 100 ticks, with the client world equal to the host world. `LoadCliTests` (3): help prints the usage, a bad argument exits 64 with the reason, and a run names the machine, the client count and the p95. `LoadPaceTests` (1): ticks are never early at a period that timers round up. `PhaseTimesTests` (2): the p95 is the nearest rank and not the max, and an empty window has none. `LoopbackLinkTests` (1): messages sent with a latency arrive in the order they were sent | done. The browser transport is not ported (decision 71). The differences are in `CHANGELOG.md`. A cross-join test skips when there is no Dart SDK or the Dart package is not resolved. The load harness is `LoadCli` in `Orblit.Net`. Its tool project waits on decision 87. Built and run on macOS arm64 only |
