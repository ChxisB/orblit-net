import 'dart:async';
import 'dart:typed_data';

import 'package:orblit_core/orblit_core.dart';

import 'ack.dart';
import 'budget.dart';
import 'codec.dart';
import 'input.dart';
import 'phase_times.dart';
import 'replication.dart';
import 'session.dart';
import 'session_message.dart';
import 'snapshot.dart';
import 'tick_clock.dart';
import 'transport.dart';

/// One connected peer, from the authority's side.
class _Client {
  _Client(this.id, this.index, this.transport, this.subscription);

  final String id;

  /// A small number for this client, written into the owner component so a
  /// peer can tell which entities are its own.
  final int index;

  final Transport transport;
  final StreamSubscription<Uint8List> subscription;

  /// The last tick this client confirmed applying. Deltas are built against it,
  /// so a client that falls silent gets larger messages rather than a world
  /// that drifts.
  int acknowledgedTick = 0;

  /// What this client has sent since the last tick.
  final BudgetMeter meter = BudgetMeter();
}

/// A connection from [NetHost.accept]. It becomes a client once it is
/// admitted.
final class _Connection {
  _Connection(this.transport, this.acceptedAt);

  final Transport transport;

  /// The host tick when it arrived, for the deadline to send a join.
  final int acceptedAt;

  late final StreamSubscription<Uint8List> subscription;

  /// Counts everything sent before the join. It is never reset, so a
  /// connection that has not joined has one tick's budget in total.
  final BudgetMeter meter = BudgetMeter();

  /// Set when the connection is admitted.
  _Client? client;
}

/// Why an input, or a whole message, was not applied. Counted rather than thrown: a hostile client
/// should not be able to raise exceptions on the authority.
enum InputRejection {
  /// No entity with that network id.
  unknownEntity,

  /// The client does not own the entity it addressed.
  notOwned,

  /// The client tried to write a component the authority reserves.
  notWritable,

  /// The payload did not match the components it claimed to carry.
  malformed,

  /// A whole message dropped unread because the client sent too much. The
  /// other reasons count entries inside an input. This one counts messages,
  /// whatever they carried.
  overBudget,
}

/// The authority: it owns the world and tells clients what happened.
class NetHost {
  NetHost({
    required World world,
    required this.set,
    required ComponentType networkId,
    ComponentType? ownerComponent,
    this.historyDepth = 64,
    TickClock? clock,
    void Function(PhaseSummary summary)? onOverrun,
    this.sessions,
    InboundBudget? budget,
  }) : budget = budget ?? InboundBudget(),
       _world = world,
       _networkId = networkId,
       _ownerComponent = ownerComponent,
       _times = PhaseTimes(clock: clock, onOverrun: onOverrun),
       _capture = SnapshotCapture(
         world: world,
         set: set,
         networkId: networkId,
       ) {
    var writable = 0;
    for (final component in set.components) {
      if (component.ownerWritable) writable |= 1 << component.bit;
    }
    _ownerWritableMask = writable;
  }

  final World _world;
  final ReplicationSet set;
  final ComponentType _networkId;

  /// Optional component holding the owning client's index, so ownership can
  /// replicate and a client can recognise the entities it drives.
  final ComponentType? _ownerComponent;

  final SnapshotCapture _capture;
  final SnapshotCodec _codec = const SnapshotCodec();
  final PhaseTimes _times;

  /// How many past snapshots to keep for building deltas. A client further
  /// behind than this is sent a full snapshot instead.
  final int historyDepth;

  /// How clients are admitted, kept across a drop and ended. Null for a host
  /// whose game adds clients itself with [addClient].
  final SessionPolicy? sessions;

  /// What each client may send between two ticks. One that stays over it is
  /// cut off.
  final InboundBudget budget;

  late final int _ownerWritableMask;

  final Map<String, _Client> _clients = {};

  /// The client index of every live or lingering session, by key.
  final Map<String, int> _sessionIndex = {};

  /// The tick each lingering session lost its connection, by key.
  final Map<String, int> _lingeringSince = {};

  final Set<_Connection> _unjoined = {};
  final Map<int, WorldSnapshot> _history = {};
  final List<int> _historyOrder = [];
  final Map<int, int> _entityByNetworkId = {};
  final Map<int, String> _ownerByNetworkId = {};

  final Map<InputRejection, int> _rejections = {};
  int _acceptedInputs = 0;

  int _nextNetworkId = 1;
  int _nextClientIndex = 1;
  int _tick = 0;

  World get world => _world;
  int get tick => _tick;
  int get clientCount => _clients.length;

  /// Inputs applied since start, and why others were not. Worth surfacing:
  /// a client that is being refused constantly is either out of date or
  /// probing, and both are things an operator wants to see.
  int get acceptedInputs => _acceptedInputs;
  Map<InputRejection, int> get rejections => Map.unmodifiable(_rejections);

  /// Runs [body] as a named part of the current tick and times it, so a
  /// game's own systems show up in [summary] beside the host's stages.
  // F5: it returns what [body] returns so a stage can wrap an expression.
  T phase<T>(String name, T Function() body) => _times.phase(name, body);

  /// Where the last [last] ticks went, and what each client was sent.
  PhaseSummary summary({int last = 30}) => _times.summary(last: last);

  /// Marks [entity] as replicated and gives it a wire identity.
  int spawn(int entity, {String? owner}) {
    final networkId = _nextNetworkId++;
    _world.add(entity, _networkId, Uint64List(1)..[0] = networkId);
    _entityByNetworkId[networkId] = entity;
    if (_ownerComponent != null) _world.add(entity, _ownerComponent);
    if (owner != null) setOwner(networkId, owner);
    return networkId;
  }

  /// Hands an entity to a client, or back to the authority with null.
  void setOwner(int networkId, String? clientId) {
    if (clientId == null) {
      _ownerByNetworkId.remove(networkId);
    } else {
      _ownerByNetworkId[networkId] = clientId;
    }

    final entity = _entityByNetworkId[networkId];
    final component = _ownerComponent;
    if (entity == null || component == null) return;
    if (!_world.isAlive(entity) || !_world.has(entity, component)) return;

    final index = clientId == null
        ? 0
        : (_sessionIndex[clientId] ?? _clients[clientId]?.index ?? 0);
    final bytes = _world.bytesOf(entity, component)!;
    ByteData.sublistView(bytes).setUint32(0, index, Endian.little);
  }

  String? ownerOf(int networkId) => _ownerByNetworkId[networkId];

  /// Whether [key] is attached, lingering, or not known to this host.
  SessionState? sessionState(String key) {
    if (_lingeringSince.containsKey(key)) return SessionState.lingering;
    return _sessionIndex.containsKey(key) ? SessionState.live : null;
  }

  /// The authority's own entity for a network id, if it still exists.
  int? entityFor(int networkId) => _entityByNetworkId[networkId];

  /// Stops replicating [entity] and destroys it. Clients learn on the next
  /// publish, from its absence.
  void despawn(int entity) => _world.destroyEntity(entity);

  void addClient(String id, Transport transport) {
    if (_clients.containsKey(id) || _sessionIndex.containsKey(id)) {
      throw StateError('A client is already connected as "$id".');
    }
    late final _Client client;
    final subscription = transport.inbound.listen(
      (message) => _receive(client, message),
    );
    client = _Client(id, _nextClientIndex++, transport, subscription);
    _clients[id] = client;
  }

  Future<void> removeClient(String id) async {
    final client = _clients.remove(id);
    await client?.subscription.cancel();
    if (_sessionIndex.containsKey(id)) {
      if (client != null) _turnAway(client.transport, GoodbyeReason.removed);
      _end(id, GoneReason.removed);
    } else if (client != null) {
      _releaseOwned(id);
    }
  }

  /// Takes a new connection, which must say who it is before anything else
  /// counts. Needs [sessions].
  ///
  /// The join goes to the policy's `admit`. A refused connection hears why and
  /// is closed, and leaves no session behind. So does one that sends no join
  /// within `joinTicks`. A drop after admission makes the session linger
  /// rather than ending it.
  void accept(Transport transport) {
    final policy = sessions;
    if (policy == null) {
      throw StateError('Accepting connections needs a SessionPolicy.');
    }

    final connection = _Connection(transport, _tick);
    connection.subscription = transport.inbound.listen(
      (message) => _hear(policy, connection, message),
      onDone: () => _lost(connection),
      onError: (Object _) => _lost(connection),
    );
    _unjoined.add(connection);
  }

  void _hear(SessionPolicy policy, _Connection connection, Uint8List message) {
    final client = connection.client;
    if (client != null) {
      _receive(client, message);
      return;
    }
    if (!connection.meter.allows(budget, message.length)) {
      _reject(InputRejection.overBudget);
      _refuse(connection, GoodbyeReason.overBudget);
      return;
    }
    connection.meter.count(message.length);
    final credentials = decodeJoin(message);
    if (credentials == null) return;

    switch (_verdict(policy, credentials)) {
      case Refuse(:final reason):
        _refuse(connection, reason);
      case Admit(:final key):
        _seat(policy, key, connection);
    }
  }

  Admission _verdict(SessionPolicy policy, Uint8List credentials) {
    try {
      return policy.admit(credentials);
    } catch (_) {
      // The bytes come from a client. Whatever the game's parser throws on
      // them is a refusal, not a crash.
      return const Refuse(GoodbyeReason.refused);
    }
  }

  /// Attaches a connection to [key]'s session, starting one if there is none.
  void _seat(SessionPolicy policy, String key, _Connection connection) {
    final attached = _clients[key];
    if (attached != null) {
      // A client the game added itself has no session to take over.
      if (!policy.takeOver || !_sessionIndex.containsKey(key)) {
        _refuse(connection, GoodbyeReason.alreadyLive);
        return;
      }
      _turnAway(attached.transport, GoodbyeReason.replaced);
      _drop(attached);
    }

    final resumed = _sessionIndex.containsKey(key);
    final index = _sessionIndex[key] ?? _nextClientIndex++;
    final client = _Client(
      key,
      index,
      connection.transport,
      connection.subscription,
    );
    connection.client = client;
    _unjoined.remove(connection);
    _clients[key] = client;
    _sessionIndex[key] = index;
    _lingeringSince.remove(key);
    // After the client is attached, so a throw here leaves a session that a
    // drop can still move to lingering.
    if (!resumed) policy.onJoin?.call(key);
  }

  void _refuse(_Connection connection, String reason) {
    _unjoined.remove(connection);
    unawaited(connection.subscription.cancel());
    _turnAway(connection.transport, reason);
  }

  void _turnAway(Transport transport, String reason) {
    transport.send(encodeGoodbye(reason));
    unawaited(transport.close());
  }

  /// A connection ended or failed. Its session, if it has one, lingers.
  void _lost(_Connection connection) {
    _unjoined.remove(connection);
    final client = connection.client;
    if (client != null) _drop(client);
    unawaited(connection.transport.close());
  }

  /// Detaches a client's connection and lets its session linger.
  void _drop(_Client client) {
    _clients.remove(client.id);
    unawaited(client.subscription.cancel());
    _lingeringSince[client.id] = _tick;
  }

  void _expire() {
    final policy = sessions;
    if (policy == null) return;
    for (final connection in _unjoined.toList()) {
      if (_tick - connection.acceptedAt >= policy.joinTicks) {
        _refuse(connection, GoodbyeReason.joinTimeout);
      }
    }
    for (final entry in _lingeringSince.entries.toList()) {
      if (_tick - entry.value >= policy.lingerTicks) {
        _end(entry.key, GoneReason.disconnect);
      }
    }
  }

  /// Ends a session. The game is told first, while the key still owns what it
  /// owned, and whatever is left afterwards reverts to the authority.
  void _end(String key, GoneReason reason) {
    _sessionIndex.remove(key);
    _lingeringSince.remove(key);
    try {
      sessions?.onGone?.call(key, reason);
    } finally {
      _releaseOwned(key);
    }
  }

  /// Entities a departed client owned revert to the authority rather than
  /// staying writable by a name that could be reconnected under. The bytes
  /// counted as sent to it are dropped with it.
  void _releaseOwned(String id) {
    _times.forgetClient(id);
    for (final networkId
        in _ownerByNetworkId.entries
            .where((entry) => entry.value == id)
            .map((entry) => entry.key)
            .toList()) {
      setOwner(networkId, null);
    }
  }

  void _receive(_Client client, Uint8List message) {
    // Before any decoding, so a flood costs a length check and no more.
    if (!client.meter.allows(budget, message.length)) {
      client.meter.refuse();
      _reject(InputRejection.overBudget);
      return;
    }
    client.meter.count(message.length);

    final acknowledged = decodeAck(message);
    if (acknowledged != null) {
      if (acknowledged > client.acknowledgedTick) {
        client.acknowledgedTick = acknowledged;
      }
      return;
    }

    final input = decodeInput(message);
    if (input != null) _applyInput(client, input);
  }

  /// Applies what a client asked for, to the extent it is allowed to.
  ///
  /// Every refusal here is a rule, not an inconvenience: ownership decides who
  /// may write which components, so this is the surface a hostile client
  /// probes first.
  void _applyInput(_Client client, InputMessage input) {
    for (final entry in input.entries) {
      final entity = _entityByNetworkId[entry.networkId];
      if (entity == null || !_world.isAlive(entity)) {
        _reject(InputRejection.unknownEntity);
        continue;
      }
      if (_ownerByNetworkId[entry.networkId] != client.id) {
        _reject(InputRejection.notOwned);
        continue;
      }
      if (entry.mask & ~_ownerWritableMask != 0) {
        _reject(InputRejection.notWritable);
        continue;
      }
      if (entry.row.length != set.strideOf(entry.mask)) {
        _reject(InputRejection.malformed);
        continue;
      }

      var offset = 0;
      for (final bit in set.bitsOf(entry.mask)) {
        final type = set.atBit(bit).type;
        final target = _world.bytesOf(entity, type);
        // A client may name a component the entity does not have; that is
        // stale rather than hostile, so the rest of the entry still applies.
        target?.setRange(0, type.byteSize, entry.row, offset);
        offset += type.byteSize;
      }
      _acceptedInputs++;
    }
  }

  void _reject(InputRejection reason) {
    _rejections[reason] = (_rejections[reason] ?? 0) + 1;
  }

  /// Captures the world and sends each client what it has not seen.
  ///
  /// The capture happens once no matter how many clients are connected; only
  /// the diff is per client, and that is a comparison rather than a re-read.
  void publish() {
    _tick++;
    try {
      _times.phase('sessions', () {
        _cutOffFloods();
        _expire();
      });
      final current = _times.phase('capture', () => _capture.capture(_tick));

      // Per-client stages add up across clients within a tick.
      for (final client in _clients.values) {
        final baseline = _history[client.acknowledgedTick];
        final message = _times.phase(
          'encode',
          () => baseline == null
              ? _codec.encodeFull(current)
              : _codec.encodeDelta(current, baseline),
        );
        _times.phase('send', () => client.transport.send(message));
        _times.phase('count', () => _countSent(client.id, message));
      }

      _times.phase('history', () {
        _remember(current);
        _forgetDeadEntities(current);
      });
    } finally {
      // A tick that throws still closes, so the next one is not merged into it.
      _times.finish();
    }
  }

  /// Splits what one message cost by component column. Whatever is not a
  /// component's bytes is overhead.
  void _countSent(String clientId, Uint8List message) {
    var columns = 0;
    for (final group in _codec.groupHeaders(message)) {
      for (final bit in set.bitsOf(group.mask)) {
        final type = set.atBit(bit).type;
        final bytes = group.rows * type.byteSize;
        _times.countSent(clientId, type.name, bytes);
        columns += bytes;
      }
    }
    _times.countSent(clientId, null, message.length - columns);
  }

  /// Closes every client's tick, then disconnects the ones that stayed over
  /// budget. Their session ends as [GoneReason.overBudget], so a flooder
  /// cannot resume it and the game can tell why.
  void _cutOffFloods() {
    for (final client in _clients.values) {
      client.meter.endTick();
    }
    final flooders = _clients.values
        .where((client) => client.meter.isOver(budget))
        .toList();
    for (final client in flooders) {
      _cutOff(client);
    }
  }

  void _cutOff(_Client client) {
    _clients.remove(client.id);
    unawaited(client.subscription.cancel());
    _turnAway(client.transport, GoodbyeReason.overBudget);
    if (_sessionIndex.containsKey(client.id)) {
      _end(client.id, GoneReason.overBudget);
    } else {
      _releaseOwned(client.id);
    }
  }

  void _forgetDeadEntities(WorldSnapshot current) {
    if (_entityByNetworkId.length == current.entityCount) return;
    for (final networkId in _entityByNetworkId.keys.toList()) {
      if (current.rowFor(networkId) == null) {
        _entityByNetworkId.remove(networkId);
        _ownerByNetworkId.remove(networkId);
      }
    }
  }

  void _remember(WorldSnapshot snapshot) {
    _history[snapshot.tick] = snapshot;
    _historyOrder.add(snapshot.tick);
    while (_historyOrder.length > historyDepth) {
      _history.remove(_historyOrder.removeAt(0));
    }
  }

  Future<void> dispose() async {
    // Copies, because a connection that closes while this awaits is dropped.
    for (final client in _clients.values.toList()) {
      await client.subscription.cancel();
    }
    for (final connection in _unjoined.toList()) {
      await connection.subscription.cancel();
    }
    _clients.clear();
    _sessionIndex.clear();
    _lingeringSince.clear();
    _unjoined.clear();
    _capture.dispose();
  }
}
