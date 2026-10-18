/// Multiplayer for Orblit.
///
/// Replication reads component columns rather than walking entities, so
/// capturing a world costs a handful of contiguous copies instead of a lookup
/// per entity.
///
/// Two transports ship with it. A loopback link, which is not only for tests —
/// a single-player build and a listen server can run the real replication path
/// over it, so the networked code path is the only code path and does not rot
/// between multiplayer sessions. And a socket transport for actual play, which
/// has a browser version in `transport.dart`. [Transport] is a small
/// interface, so a third is an afternoon.
library;

export 'src/ack.dart' show decodeAck, encodeAck;
export 'src/active_cells.dart' show ActiveCells;
export 'src/budget.dart' show InboundBudget;
export 'src/client.dart' show NetClient;
export 'src/codec.dart'
    show DecodedSnapshot, SnapshotCodec, SnapshotFormatError;
export 'src/host.dart' show InputRejection, InterestRule, NetHost;
export 'src/interest.dart' show Cell, Grid, GridInterest;
export 'src/input.dart' show InputEntry, InputMessage, decodeInput, encodeInput;
export 'src/phase_times.dart' show ClientBytes, PhaseStats, PhaseSummary;
export 'src/replication.dart' show ReplicatedComponent, ReplicationSet;
export 'src/session.dart'
    show
        Admission,
        Admit,
        GoneReason,
        GoodbyeReason,
        Refuse,
        SessionPolicy,
        SessionState;
export 'src/snapshot.dart'
    show SnapshotCapture, SnapshotGroup, SnapshotRow, WorldSnapshot;
export 'src/tick_clock.dart' show TickClock;
export 'transport.dart';
