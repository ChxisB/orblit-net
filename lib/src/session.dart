import 'dart:async';
import 'dart:typed_data';

/// What the game decides about a join.
sealed class Admission {
  const Admission();
}

/// The join is welcome. [key] names the player for as long as the session
/// lives, usually an account id, and is the client id the host uses.
final class Admit extends Admission {
  const Admit(this.key);

  final String key;
}

/// The join is turned away. [reason] is sent to the client as text.
final class Refuse extends Admission {
  const Refuse(this.reason);

  final String reason;
}

enum SessionState {
  /// A connection is attached.
  live,

  /// The connection is gone. The entities stay for [SessionPolicy.lingerTicks]
  /// ticks, and a join with the same key takes them back.
  lingering,
}

/// Why a session ended.
enum GoneReason {
  /// It lingered for [SessionPolicy.lingerTicks] ticks and nobody came back.
  disconnect,

  /// The game called `NetHost.removeClient`.
  removed,

  /// The host cut the client off for staying over its `InboundBudget`.
  overBudget,
}

/// The reasons a host puts in a goodbye. A game's own refusals may say
/// anything else.
abstract final class GoodbyeReason {
  /// A second join for a key that is attached, on a host without `takeOver`.
  static const String alreadyLive = 'alreadyLive';

  /// The same key joined again on a host with `takeOver`.
  static const String replaced = 'replaced';

  /// The game called `removeClient`.
  static const String removed = 'removed';

  /// The game's `admit` threw.
  static const String refused = 'refused';

  /// No join arrived within `joinTicks`.
  static const String joinTimeout = 'joinTimeout';

  /// The client stayed over its `InboundBudget`.
  static const String overBudget = 'overBudget';
}

/// How a host treats the clients it admits.
///
/// Orblit checks nothing about who someone is. [admit] is given the bytes the
/// client sent and answers for the game. A plain answer is acted on at once.
/// A future may take longer, for a password hash or a database, and the host
/// keeps ticking while it waits. The connection stays unjoined, so
/// [joinTicks] still counts down, and a second join from it is ignored.
final class SessionPolicy {
  SessionPolicy({
    required this.admit,
    this.lingerTicks = 600,
    this.joinTicks = 300,
    this.takeOver = false,
    this.onJoin,
    this.onGone,
  }) {
    if (lingerTicks < 0) {
      throw ArgumentError.value(
        lingerTicks,
        'lingerTicks',
        'Must be 0 or more',
      );
    }
    if (joinTicks < 1) {
      throw ArgumentError.value(joinTicks, 'joinTicks', 'Must be 1 or more');
    }
  }

  /// Turns what a client sent at join into a key or a refusal. A throw, or a
  /// future that fails, refuses the join, since the bytes come from a client.
  final FutureOr<Admission> Function(Uint8List credentials) admit;

  /// Host ticks a dropped session keeps its entities.
  final int lingerTicks;

  /// Host ticks a connection may stay silent before it sends a join. After
  /// that it hears `joinTimeout` and is closed.
  final int joinTicks;

  /// What a second join for a live key does: false refuses it with
  /// `alreadyLive`, true sends the old connection away with `replaced` and
  /// resumes the session on the new one.
  final bool takeOver;

  /// A key joined for the first time. The game creates its entities here.
  final void Function(String key)? onJoin;

  /// A session ended, once. The key still owns its entities during the call,
  /// so the game can find them, save them and despawn them. A throw leaves
  /// the session ended and its entities released, and propagates out of
  /// `publish`; the next `publish` carries on.
  final void Function(String key, GoneReason reason)? onGone;
}
