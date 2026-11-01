using System.Buffers.Binary;
using Orblit.Core;

namespace Orblit.Net;

/// <summary>
/// Why an input, or a whole message, was not applied. Counted rather than thrown: a hostile client
/// should not be able to raise exceptions on the authority.
/// </summary>
public enum InputRejection
{
    /// <summary>No entity with that network id.</summary>
    UnknownEntity,

    /// <summary>The client does not own the entity it addressed.</summary>
    NotOwned,

    /// <summary>The client tried to write a component the authority reserves.</summary>
    NotWritable,

    /// <summary>The payload did not match the components it claimed to carry.</summary>
    Malformed,

    /// <summary>
    /// A whole message dropped unread because the client sent too much. The other reasons count
    /// entries inside an input. This one counts messages, whatever they carried.
    /// </summary>
    OverBudget,
}

/// <summary>What a <see cref="NetHost"/> is built from.</summary>
public sealed record NetHostOptions
{
    public required World World { get; init; }

    public required ReplicationSet Set { get; init; }

    public required ComponentType NetworkId { get; init; }

    /// <summary>
    /// Optional component holding the owning client's index, so ownership can replicate and a
    /// client can recognise the entities it drives.
    /// </summary>
    public ComponentType? OwnerComponent { get; init; }

    /// <summary>
    /// How many past snapshots to keep for each client, for building deltas. A client further
    /// behind than this is sent a full snapshot instead.
    /// </summary>
    public int HistoryDepth { get; init; } = 64;

    public TickClock? Clock { get; init; }

    public Action<PhaseSummary>? OnOverrun { get; init; }

    /// <summary>
    /// How clients are admitted, kept across a drop and ended. Null for a host whose game adds
    /// clients itself with <see cref="NetHost.AddClient"/>.
    /// </summary>
    public SessionPolicy? Sessions { get; init; }

    /// <summary>What each client may send between two ticks. One that stays over it is cut off.</summary>
    public InboundBudget? Budget { get; init; }

    /// <summary>Which entities each client is told about. Null tells every client about every entity.</summary>
    public IInterestRule? Interest { get; init; }

    /// <summary>
    /// Called with each game message an attached client sends, after the budget. The bytes come from
    /// a client, so the handler must not throw on them. Null drops them.
    /// </summary>
    public Action<string, byte[]>? OnMessage { get; init; }
}

/// <summary>
/// The authority: it owns the world and tells clients what happened.
/// </summary>
/// <remarks>
/// Messages arrive on the transport's thread. Every public member, and every message, takes
/// <see cref="Gate"/> first, so a game that runs its systems on another thread holds the gate while
/// it does, and inputs land between its ticks rather than inside one.
/// </remarks>
public sealed class NetHost : IDisposable
{
    private readonly object _gate = new();
    private readonly World _world;
    private readonly ReplicationSet _set;
    private readonly ComponentType _networkIdType;
    private readonly ComponentType? _ownerComponent;
    private readonly SnapshotCapture _capture;
    private readonly PhaseTimes _times;
    private readonly int _historyDepth;
    private readonly SessionPolicy? _sessions;
    private readonly InboundBudget _budget;
    private readonly IInterestRule? _interest;
    private readonly Action<string, byte[]>? _onMessage;
    private readonly ulong _ownerWritableMask;
    private readonly OrderedDictionary<string, HostClient> _clients = [];
    private readonly Dictionary<string, int> _sessionIndex = [];
    private readonly OrderedDictionary<string, long> _lingerUntil = [];
    private readonly List<Connection> _unjoined = [];
    private readonly Dictionary<ulong, ulong> _entityByNetworkId = [];
    private readonly OrderedDictionary<ulong, string> _ownerByNetworkId = [];
    private readonly Dictionary<InputRejection, int> _rejections = [];
    private int _acceptedInputs;
    private ulong _nextNetworkId = 1;
    private int _nextClientIndex = 1;
    private long _tick;

    public NetHost(NetHostOptions options)
    {
        _world = options.World;
        _set = options.Set;
        _networkIdType = options.NetworkId;
        _ownerComponent = options.OwnerComponent;
        _historyDepth = options.HistoryDepth;
        _sessions = options.Sessions;
        _budget = options.Budget ?? new InboundBudget();
        _interest = options.Interest;
        _onMessage = options.OnMessage;
        _times = new PhaseTimes(options.Clock, options.OnOverrun);
        _capture = new SnapshotCapture(options.World, options.Set, options.NetworkId);
        _ownerWritableMask = _set.Components
            .Where(component => component.OwnerWritable)
            .Aggregate(0UL, (mask, component) => mask | (1UL << component.Bit));
    }

    /// <summary>The lock every message and every public call takes. See the remarks.</summary>
    public object Gate => _gate;

    public World World => _world;

    public ReplicationSet Set => _set;

    public long Tick
    {
        get
        {
            lock (_gate)
            {
                return _tick;
            }
        }
    }

    public int ClientCount
    {
        get
        {
            lock (_gate)
            {
                return _clients.Count;
            }
        }
    }

    /// <summary>
    /// Inputs applied since start. Worth surfacing with <see cref="Rejections"/>: a client that is
    /// being refused constantly is either out of date or probing, and both are things an operator
    /// wants to see.
    /// </summary>
    public int AcceptedInputs
    {
        get
        {
            lock (_gate)
            {
                return _acceptedInputs;
            }
        }
    }

    /// <summary>Why inputs were not applied, with how many of each.</summary>
    public IReadOnlyDictionary<InputRejection, int> Rejections
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<InputRejection, int>(_rejections);
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> as a named part of the current tick and times it, so a game's
    /// own systems show up in <see cref="Summary"/> beside the host's stages.
    /// </summary>
    // F5: it returns what the body returns so a stage can wrap an expression.
    public T Phase<T>(string name, Func<T> body)
    {
        lock (_gate)
        {
            return _times.Phase(name, body);
        }
    }

    public void Phase(string name, Action body)
    {
        lock (_gate)
        {
            _times.Phase(name, body);
        }
    }

    /// <summary>Where the last <paramref name="last"/> ticks went, and what each client was sent.</summary>
    public PhaseSummary Summary(int last = 30)
    {
        lock (_gate)
        {
            return _times.Summary(last);
        }
    }

    /// <summary>
    /// Sends <paramref name="body"/> to <paramref name="clientId"/> as a game message. A client that
    /// is not attached, or is lingering, is not sent it. Messages are as reliable as the transport,
    /// which is the only thing that promises delivery or order.
    /// </summary>
    public void SendMessage(string clientId, byte[] body)
    {
        lock (_gate)
        {
            if (_clients.TryGetValue(clientId, out var client))
            {
                client.Transport.Send(SessionMessage.EncodeMessage(body));
            }
        }
    }

    /// <summary>Marks <paramref name="entity"/> as replicated and gives it a wire identity.</summary>
    // F5: the network id is the point of the call, so it comes back from it.
    public ulong Spawn(ulong entity, string? owner = null)
    {
        lock (_gate)
        {
            var networkId = _nextNetworkId++;
            _world.Add<ulong>(entity, _networkIdType, [networkId]);
            _entityByNetworkId[networkId] = entity;
            if (_ownerComponent is { } component)
            {
                _world.Add(entity, component);
            }

            if (owner is not null)
            {
                SetOwner(networkId, owner);
            }

            return networkId;
        }
    }

    /// <summary>Hands an entity to a client, or back to the authority with null.</summary>
    public void SetOwner(ulong networkId, string? clientId)
    {
        lock (_gate)
        {
            if (clientId is null)
            {
                _ownerByNetworkId.Remove(networkId);
            }
            else
            {
                _ownerByNetworkId[networkId] = clientId;
            }

            WriteOwnerIndex(networkId, clientId);
        }
    }

    public string? OwnerOf(ulong networkId)
    {
        lock (_gate)
        {
            return _ownerByNetworkId.GetValueOrDefault(networkId);
        }
    }

    /// <summary>Whether <paramref name="key"/> is attached, lingering, or not known to this host.</summary>
    public SessionState? SessionStateOf(string key)
    {
        lock (_gate)
        {
            if (_lingerUntil.ContainsKey(key))
            {
                return SessionState.Lingering;
            }

            return _sessionIndex.ContainsKey(key) ? SessionState.Live : null;
        }
    }

    /// <summary>The authority's own entity for a network id, if it still exists.</summary>
    public ulong? EntityFor(ulong networkId)
    {
        lock (_gate)
        {
            return _entityByNetworkId.TryGetValue(networkId, out var entity) ? entity : null;
        }
    }

    /// <summary>Stops replicating <paramref name="entity"/> and destroys it. Clients learn on the next publish, from its absence.</summary>
    public void Despawn(ulong entity)
    {
        lock (_gate)
        {
            _world.DestroyEntity(entity);
        }
    }

    public void AddClient(string id, ITransport transport)
    {
        lock (_gate)
        {
            if (_clients.ContainsKey(id) || _sessionIndex.ContainsKey(id))
            {
                throw new InvalidOperationException($"A client is already connected as \"{id}\".");
            }

            var client = new HostClient(id, _nextClientIndex++, transport);
            client.Subscription = transport.Listen(message => Receive(client, message));
            _clients[id] = client;
        }
    }

    public void RemoveClient(string id)
    {
        lock (_gate)
        {
            _clients.Remove(id, out var client);
            client?.Subscription.Dispose();
            if (_sessionIndex.ContainsKey(id))
            {
                if (client is not null)
                {
                    SendAway(client.Transport, GoodbyeReason.Removed);
                }

                End(id, GoneReason.Removed);
            }
            else if (client is not null)
            {
                ReleaseOwned(id);
            }
        }
    }

    /// <summary>
    /// Takes a new connection, which must say who it is before anything else counts. Needs
    /// <see cref="NetHostOptions.Sessions"/>.
    /// </summary>
    /// <remarks>
    /// The join goes to the policy's admit. A refused connection hears why and is closed, and leaves
    /// no session behind. So does one that sends no join within the join ticks. A drop after
    /// admission makes the session linger rather than ending it.
    /// </remarks>
    public void Accept(ITransport transport)
    {
        lock (_gate)
        {
            var policy = _sessions
                ?? throw new InvalidOperationException("Accepting connections needs a SessionPolicy.");
            var connection = new Connection(transport, _tick);
            connection.Subscription = transport.Listen(
                message => Hear(policy, connection, message),
                onDone: () => Lost(connection),
                onError: _ => Lost(connection));
            _unjoined.Add(connection);
        }
    }

    /// <summary>
    /// Captures the world and sends each client what it has not seen. The capture happens once no
    /// matter how many clients are connected. Only the diff is per client, and that is a comparison
    /// rather than a re-read.
    /// </summary>
    public void Publish()
    {
        lock (_gate)
        {
            _tick++;
            try
            {
                _times.Phase("sessions", () =>
                {
                    CutOffFloods();
                    Expire();
                });
                var current = _times.Phase("capture", () => _capture.Capture(_tick));
                var rule = _interest;
                if (rule is not null)
                {
                    _times.Phase("interest", () => rule.Observe(current, OwnedByClient()));
                }

                // Per-client stages add up across clients within a tick.
                foreach (var client in _clients.Values.ToArray())
                {
                    SendTo(client, current, rule);
                }

                _times.Phase("forget", () => ForgetDeadEntities(current));
            }
            finally
            {
                // A tick that throws still closes, so the next one is not merged into it.
                _times.Finish();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var client in _clients.Values)
            {
                client.Subscription.Dispose();
            }

            foreach (var connection in _unjoined)
            {
                connection.Subscription.Dispose();
            }

            _clients.Clear();
            _sessionIndex.Clear();
            _lingerUntil.Clear();
            _unjoined.Clear();
            _capture.Dispose();
        }
    }

    private void WriteOwnerIndex(ulong networkId, string? clientId)
    {
        if (!_entityByNetworkId.TryGetValue(networkId, out var entity) || _ownerComponent is not { } component)
        {
            return;
        }

        if (!_world.IsAlive(entity) || !_world.Has(entity, component))
        {
            return;
        }

        var index = clientId is null ? 0 : IndexOf(clientId);
        _world.TryGetBytes(entity, component, out var bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)index);
    }

    private int IndexOf(string clientId)
    {
        if (_sessionIndex.TryGetValue(clientId, out var index))
        {
            return index;
        }

        return _clients.TryGetValue(clientId, out var client) ? client.Index : 0;
    }

    private void Hear(SessionPolicy policy, Connection connection, byte[] message)
    {
        lock (_gate)
        {
            if (connection.Client is { } client)
            {
                Receive(client, message);
                return;
            }

            if (!connection.Meter.Allows(_budget, message.Length))
            {
                Reject(InputRejection.OverBudget);
                Dismiss(connection, GoodbyeReason.OverBudget);
                return;
            }

            connection.Meter.Count(message.Length);
            if (SessionMessage.DecodeJoin(message) is not { } credentials || connection.Deciding)
            {
                return;
            }

            Decide(policy, connection, credentials);
        }
    }

    private void Decide(SessionPolicy policy, Connection connection, byte[] credentials)
    {
        var verdict = Ask(policy, credentials);
        if (verdict.IsCompleted)
        {
            Settle(policy, connection, Answer(verdict));
            return;
        }

        connection.Deciding = true;
        _ = SettleLater(policy, connection, verdict);
    }

    private static ValueTask<Admission> Ask(SessionPolicy policy, byte[] credentials)
    {
        try
        {
            return policy.Admit(credentials);
        }
#pragma warning disable CA1031 // The bytes come from a client. Whatever the game's parser throws on them is a refusal, not a crash.
        catch (Exception)
#pragma warning restore CA1031
        {
            return new ValueTask<Admission>(new Refuse(GoodbyeReason.Refused));
        }
    }

    private static Admission Answer(ValueTask<Admission> verdict)
    {
        try
        {
            return verdict.Result;
        }
#pragma warning disable CA1031 // A failed admit refuses the join, since the bytes come from a client.
        catch (Exception)
#pragma warning restore CA1031
        {
            return new Refuse(GoodbyeReason.Refused);
        }
    }

    private async Task SettleLater(SessionPolicy policy, Connection connection, ValueTask<Admission> verdict)
    {
        Admission answer;
        try
        {
            answer = await verdict;
        }
#pragma warning disable CA1031 // A failed admit refuses the join, since the bytes come from a client.
        catch (Exception)
#pragma warning restore CA1031
        {
            answer = new Refuse(GoodbyeReason.Refused);
        }

        lock (_gate)
        {
            Settle(policy, connection, answer);
        }
    }

    /// <summary>
    /// Acts on a verdict. A connection that closed, timed out or was cut off while the game decided
    /// is no longer waiting, so the verdict is dropped.
    /// </summary>
    private void Settle(SessionPolicy policy, Connection connection, Admission verdict)
    {
        connection.Deciding = false;
        if (!_unjoined.Contains(connection))
        {
            return;
        }

        switch (verdict)
        {
            case Refuse refuse:
                Dismiss(connection, refuse.Reason);
                break;
            case Admit admit:
                Seat(policy, admit.Key, connection);
                break;
            default:
                throw new InvalidOperationException($"Unknown admission {verdict}.");
        }
    }

    /// <summary>Attaches a connection to a key's session, starting one if there is none.</summary>
    private void Seat(SessionPolicy policy, string key, Connection connection)
    {
        if (_clients.TryGetValue(key, out var attached))
        {
            // A client the game added itself has no session to take over.
            if (!policy.TakeOver || !_sessionIndex.ContainsKey(key))
            {
                Dismiss(connection, GoodbyeReason.AlreadyLive);
                return;
            }

            SendAway(attached.Transport, GoodbyeReason.Replaced);
            Drop(attached);
        }

        var resumed = _sessionIndex.TryGetValue(key, out var index);
        if (!resumed)
        {
            index = _nextClientIndex++;
        }

        var client = new HostClient(key, index, connection.Transport) { Subscription = connection.Subscription };
        connection.Client = client;
        _unjoined.Remove(connection);
        _clients[key] = client;
        _sessionIndex[key] = index;
        _lingerUntil.Remove(key);

        // After the client is attached, so a throw here leaves a session that a drop can still move
        // to lingering.
        if (!resumed)
        {
            policy.OnJoin?.Invoke(key);
        }
    }

    private void Dismiss(Connection connection, string reason)
    {
        _unjoined.Remove(connection);
        connection.Subscription.Dispose();
        SendAway(connection.Transport, reason);
    }

    private static void SendAway(ITransport transport, string reason)
    {
        transport.Send(SessionMessage.EncodeGoodbye(reason));
        _ = transport.CloseAsync();
    }

    /// <summary>A connection ended or failed. Its session, if it has one, lingers.</summary>
    private void Lost(Connection connection)
    {
        lock (_gate)
        {
            _unjoined.Remove(connection);
            if (connection.Client is { } client)
            {
                Drop(client);
            }

            _ = connection.Transport.CloseAsync();
        }
    }

    /// <summary>Detaches a client's connection and lets its session linger.</summary>
    private void Drop(HostClient client)
    {
        _clients.Remove(client.Id);
        client.Subscription.Dispose();
        _lingerUntil[client.Id] = _tick + LingerFor(client.Id);
    }

    private long LingerFor(string key)
    {
        // A client only exists on a host with a policy when it was admitted, and a client the game
        // added itself has no session to linger.
        var policy = _sessions!;
        if (policy.LingerFor is not { } custom)
        {
            return policy.LingerTicks;
        }

        try
        {
            return custom(key);
        }
#pragma warning disable CA1031 // The game's rule failed. The session still needs an end, so it gets the default.
        catch (Exception)
#pragma warning restore CA1031
        {
            return policy.LingerTicks;
        }
    }

    private void Expire()
    {
        if (_sessions is not { } policy)
        {
            return;
        }

        foreach (var connection in _unjoined.ToArray())
        {
            if (_tick - connection.AcceptedAt >= policy.JoinTicks)
            {
                Dismiss(connection, GoodbyeReason.JoinTimeout);
            }
        }

        foreach (var (key, until) in _lingerUntil.ToArray())
        {
            if (_tick >= until)
            {
                End(key, GoneReason.Disconnect);
            }
        }
    }

    /// <summary>
    /// Ends a session. The game is told first, while the key still owns what it owned, and whatever
    /// is left afterwards reverts to the authority.
    /// </summary>
    private void End(string key, GoneReason reason)
    {
        _sessionIndex.Remove(key);
        _lingerUntil.Remove(key);
        try
        {
            _sessions?.OnGone?.Invoke(key, reason);
        }
        finally
        {
            ReleaseOwned(key);
        }
    }

    /// <summary>
    /// Entities a departed client owned revert to the authority rather than staying writable by a
    /// name that could be reconnected under. The bytes counted as sent to it are dropped with it.
    /// </summary>
    private void ReleaseOwned(string id)
    {
        _times.ForgetClient(id);
        var owned = _ownerByNetworkId.Where(entry => entry.Value == id).Select(entry => entry.Key).ToArray();
        foreach (var networkId in owned)
        {
            SetOwner(networkId, null);
        }
    }

    private void Receive(HostClient client, byte[] message)
    {
        lock (_gate)
        {
            // Before any decoding, so a flood costs a length check and no more.
            if (!client.Meter.Allows(_budget, message.Length))
            {
                client.Meter.Refuse();
                Reject(InputRejection.OverBudget);
                return;
            }

            client.Meter.Count(message.Length);
            if (Ack.Decode(message) is { } acknowledged)
            {
                client.Acknowledge(acknowledged);
            }
            else if (InputCodec.Decode(message) is { } input)
            {
                ApplyInput(client, input);
            }
            else if (SessionMessage.DecodeMessage(message) is { } body)
            {
                _onMessage?.Invoke(client.Id, body);
            }
        }
    }

    /// <summary>
    /// Applies what a client asked for, to the extent it is allowed to. Every refusal here is a
    /// rule, not an inconvenience: ownership decides who may write which components, so this is the
    /// surface a hostile client probes first.
    /// </summary>
    private void ApplyInput(HostClient client, InputMessage input)
    {
        foreach (var entry in input.Entries)
        {
            if (Refusal(client, entry) is { } reason)
            {
                Reject(reason);
                continue;
            }

            WriteRow(_entityByNetworkId[entry.NetworkId], entry);
            _acceptedInputs++;
        }
    }

    private InputRejection? Refusal(HostClient client, InputEntry entry)
    {
        if (!_entityByNetworkId.TryGetValue(entry.NetworkId, out var entity) || !_world.IsAlive(entity))
        {
            return InputRejection.UnknownEntity;
        }

        if (!_ownerByNetworkId.TryGetValue(entry.NetworkId, out var owner) || owner != client.Id)
        {
            return InputRejection.NotOwned;
        }

        if ((entry.Mask & ~_ownerWritableMask) != 0)
        {
            return InputRejection.NotWritable;
        }

        return entry.Row.Length == _set.StrideOf(entry.Mask) ? null : InputRejection.Malformed;
    }

    private void WriteRow(ulong entity, InputEntry entry)
    {
        var offset = 0;
        foreach (var bit in _set.BitsOf(entry.Mask))
        {
            var type = _set.AtBit(bit).Type;

            // A client may name a component the entity does not have. That is stale rather than
            // hostile, so the rest of the entry still applies.
            if (_world.TryGetBytes(entity, type, out var target))
            {
                entry.Row.AsSpan(offset, type.ByteSize).CopyTo(target);
            }

            offset += type.ByteSize;
        }
    }

    private void Reject(InputRejection reason) =>
        _rejections[reason] = _rejections.GetValueOrDefault(reason) + 1;

    private void SendTo(HostClient client, WorldSnapshot current, IInterestRule? rule)
    {
        var seen = rule is null
            ? current
            : _times.Phase("interest", () => current.Filtered(id => CanSee(rule, client, id)));
        var message = _times.Phase("encode", () =>
            client.Shown.TryGetValue(client.AcknowledgedTick, out var baseline)
                ? SnapshotCodec.EncodeDelta(seen, baseline)
                : SnapshotCodec.EncodeFull(seen));
        _times.Phase("send", () => client.Transport.Send(message));
        _times.Phase("count", () => CountSent(client.Id, message));
        client.Show(seen, _historyDepth);
    }

    private bool CanSee(IInterestRule rule, HostClient client, ulong networkId) =>
        (_ownerByNetworkId.TryGetValue(networkId, out var owner) && owner == client.Id)
        || rule.IsRelevant(client.Id, networkId);

    /// <summary>
    /// The network ids each connected client owns, by client id. A lingering session has no
    /// connection to send to, so it has no focus.
    /// </summary>
    private OrderedDictionary<string, IReadOnlyList<ulong>> OwnedByClient()
    {
        var owned = new OrderedDictionary<string, List<ulong>>();
        foreach (var (networkId, clientId) in _ownerByNetworkId)
        {
            if (!_clients.ContainsKey(clientId))
            {
                continue;
            }

            if (!owned.TryGetValue(clientId, out var list))
            {
                owned[clientId] = list = [];
            }

            list.Add(networkId);
        }

        var result = new OrderedDictionary<string, IReadOnlyList<ulong>>();
        foreach (var (clientId, list) in owned)
        {
            result[clientId] = list;
        }

        return result;
    }

    /// <summary>
    /// Splits what one message cost by component column. Whatever is not a component's bytes is
    /// overhead.
    /// </summary>
    private void CountSent(string clientId, byte[] message)
    {
        var columns = 0;
        foreach (var group in SnapshotCodec.GroupHeaders(message))
        {
            foreach (var bit in _set.BitsOf(group.Mask))
            {
                var type = _set.AtBit(bit).Type;
                var bytes = group.Rows * type.ByteSize;
                _times.CountSent(clientId, type.Name, bytes);
                columns += bytes;
            }
        }

        _times.CountSent(clientId, null, message.Length - columns);
    }

    /// <summary>
    /// Closes every client's tick, then disconnects the ones that stayed over budget. Their session
    /// ends as <see cref="GoneReason.OverBudget"/>, so a flooder cannot resume it and the game can
    /// tell why.
    /// </summary>
    private void CutOffFloods()
    {
        foreach (var client in _clients.Values)
        {
            client.Meter.EndTick();
        }

        foreach (var client in _clients.Values.Where(client => client.Meter.IsOver(_budget)).ToArray())
        {
            CutOff(client);
        }
    }

    private void CutOff(HostClient client)
    {
        _clients.Remove(client.Id);
        client.Subscription.Dispose();
        SendAway(client.Transport, GoodbyeReason.OverBudget);
        if (_sessionIndex.ContainsKey(client.Id))
        {
            End(client.Id, GoneReason.OverBudget);
        }
        else
        {
            ReleaseOwned(client.Id);
        }
    }

    private void ForgetDeadEntities(WorldSnapshot current)
    {
        if (_entityByNetworkId.Count == current.EntityCount)
        {
            return;
        }

        foreach (var networkId in _entityByNetworkId.Keys.ToArray())
        {
            if (current.RowFor(networkId) is null)
            {
                _entityByNetworkId.Remove(networkId);
                _ownerByNetworkId.Remove(networkId);
            }
        }
    }

    /// <summary>One connected peer, from the authority's side.</summary>
    private sealed class HostClient(string id, int index, ITransport transport)
    {
        public string Id { get; } = id;

        /// <summary>
        /// A small number for this client, written into the owner component so a peer can tell which
        /// entities are its own.
        /// </summary>
        public int Index { get; } = index;

        public ITransport Transport { get; } = transport;

        /// <summary>Set before the host's lock is released, so no message sees it empty.</summary>
        public IDisposable Subscription { get; set; } = null!;

        /// <summary>
        /// The last tick this client confirmed applying. Deltas are built against it, so a client
        /// that falls silent gets larger messages rather than a world that drifts.
        /// </summary>
        public long AcknowledgedTick { get; private set; }

        /// <summary>
        /// What this client was sent, by tick: the whole world, or the part of it the interest rule
        /// let through. A delta is built against the entry for <see cref="AcknowledgedTick"/>, so
        /// what the client lacks is whatever differs from what it was shown, not from what the world
        /// held.
        /// </summary>
        public OrderedDictionary<long, WorldSnapshot> Shown { get; } = [];

        /// <summary>What this client has sent since the last tick.</summary>
        public BudgetMeter Meter { get; } = new();

        public void Acknowledge(long tick)
        {
            if (tick <= AcknowledgedTick)
            {
                return;
            }

            AcknowledgedTick = tick;
            foreach (var shownTick in Shown.Keys.Where(shown => shown < tick).ToArray())
            {
                Shown.Remove(shownTick);
            }
        }

        /// <summary>Remembers <paramref name="snapshot"/>, and forgets the oldest past <paramref name="depth"/>.</summary>
        public void Show(WorldSnapshot snapshot, int depth)
        {
            Shown[snapshot.Tick] = snapshot;
            while (Shown.Count > depth)
            {
                Shown.RemoveAt(0);
            }
        }
    }

    /// <summary>A connection from <see cref="Accept"/>. It becomes a client once it is admitted.</summary>
    private sealed class Connection(ITransport transport, long acceptedAt)
    {
        public ITransport Transport { get; } = transport;

        /// <summary>The host tick when it arrived, for the deadline to send a join.</summary>
        public long AcceptedAt { get; } = acceptedAt;

        /// <summary>Set before the host's lock is released, so no message sees it empty.</summary>
        public IDisposable Subscription { get; set; } = null!;

        /// <summary>
        /// Counts everything sent before the join. It is never reset, so a connection that has not
        /// joined has one tick's budget in total.
        /// </summary>
        public BudgetMeter Meter { get; } = new();

        /// <summary>Set when the connection is admitted.</summary>
        public HostClient? Client { get; set; }

        /// <summary>
        /// True while an admit that returned an unfinished task has not answered. A second join in
        /// that time is ignored.
        /// </summary>
        public bool Deciding { get; set; }
    }
}
