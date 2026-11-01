using System.Buffers.Binary;
using System.Diagnostics;
using Orblit.Core;

namespace Orblit.Net;

/// <summary>What a <see cref="NetClient"/> is built from.</summary>
public sealed record NetClientOptions
{
    public required World World { get; init; }

    public required ReplicationSet Set { get; init; }

    public required ComponentType NetworkId { get; init; }

    public required ITransport Transport { get; init; }

    /// <summary>
    /// How far behind the authority to render.
    /// </summary>
    /// <remarks>
    /// Zero applies each snapshot the moment it lands, which is correct and looks wrong: remote
    /// entities step at the authority's tick rate rather than moving. A delay of roughly two ticks
    /// buys a state on either side of the render time to blend between, at the cost of showing the
    /// world slightly late. That is the trade every networked game makes.
    /// </remarks>
    public TimeSpan InterpolationDelay { get; init; }

    /// <summary>How many past states to keep for blending.</summary>
    public int HistoryDepth { get; init; } = 32;

    public Action<ulong, ulong>? OnSpawn { get; init; }

    public Action<ulong, ulong>? OnDespawn { get; init; }

    /// <summary>Called with the reason when the authority sends this client away.</summary>
    public Action<string>? OnGoodbye { get; init; }

    /// <summary>Called with the body of each game message the authority sends.</summary>
    public Action<byte[]>? OnMessage { get; init; }

    /// <summary>
    /// Time since this client started. A game usually should supply its own: the frame clock it
    /// already keeps is a better time base than a second stopwatch drifting alongside it.
    /// </summary>
    public Func<TimeSpan>? Clock { get; init; }
}

/// <summary>
/// The replica side: it receives what the authority did and reproduces it.
/// </summary>
/// <remarks>
/// The client's world is its own, with its own component ids. Nothing on the wire refers to those.
/// Components are identified by their position in the replication set, which is derived from their
/// names. Two builds that declare the same components therefore agree without exchanging a schema,
/// even if they registered them in a different order. Messages arrive on the transport's thread, so
/// every public member takes <see cref="Gate"/>. A game that reads the world from its frame thread
/// holds the gate while it does.
/// </remarks>
public sealed class NetClient : IDisposable
{
    private readonly object _gate = new();
    private readonly World _world;
    private readonly ComponentType _networkId;
    private readonly ITransport _transport;
    private readonly Func<TimeSpan> _now;
    private readonly Action<ulong, ulong>? _onSpawn;
    private readonly Action<ulong, ulong>? _onDespawn;
    private readonly Action<string>? _onGoodbye;
    private readonly Action<byte[]>? _onMessage;
    private readonly IDisposable _subscription;
    private readonly Dictionary<ulong, ulong> _entities = [];
    private readonly List<WorldState> _history = [];
    private long _lastAppliedTick;

    public NetClient(NetClientOptions options)
    {
        _world = options.World;
        Set = options.Set;
        _networkId = options.NetworkId;
        _transport = options.Transport;
        InterpolationDelay = options.InterpolationDelay;
        HistoryDepth = options.HistoryDepth;
        _onSpawn = options.OnSpawn;
        _onDespawn = options.OnDespawn;
        _onGoodbye = options.OnGoodbye;
        _onMessage = options.OnMessage;
        _now = options.Clock ?? StartClock();
        _subscription = options.Transport.Listen(Receive);
    }

    public ReplicationSet Set { get; }

    public TimeSpan InterpolationDelay { get; }

    public int HistoryDepth { get; }

    /// <summary>The lock every message and every public call takes. See the remarks.</summary>
    public object Gate => _gate;

    public World World => _world;

    public bool IsInterpolating => InterpolationDelay > TimeSpan.Zero;

    /// <summary>
    /// The last tick received in full. Sent back to the authority so its deltas are built against
    /// something this client has actually seen.
    /// </summary>
    public long LastAppliedTick
    {
        get
        {
            lock (_gate)
            {
                return _lastAppliedTick;
            }
        }
    }

    public int EntityCount
    {
        get
        {
            lock (_gate)
            {
                return _entities.Count;
            }
        }
    }

    public IReadOnlyList<ulong> NetworkIds
    {
        get
        {
            lock (_gate)
            {
                return [.. _entities.Keys];
            }
        }
    }

    public ulong? EntityFor(ulong networkId)
    {
        lock (_gate)
        {
            return _entities.TryGetValue(networkId, out var entity) ? entity : null;
        }
    }

    /// <summary>
    /// Tells a host that admits clients who this is. <paramref name="credentials"/> are the game's
    /// own bytes, handed to its admission function unread.
    /// </summary>
    public void Join(byte[] credentials) => _transport.Send(SessionMessage.EncodeJoin(credentials));

    /// <summary>
    /// Proposes writes to entities this client owns, as each component's bytes by network id.
    /// </summary>
    /// <remarks>
    /// The authority decides whether to honour them: only components declared owner-writable, and
    /// only on entities it agrees this client owns. Anything else is counted and dropped there
    /// rather than trusted here.
    /// </remarks>
    /// <exception cref="ArgumentException">A component is not in the replication set.</exception>
    public void SendInput(IReadOnlyDictionary<ulong, IReadOnlyDictionary<ComponentType, byte[]>> writes)
    {
        var entries = writes.Select(write => InputOf(write.Key, write.Value)).ToArray();
        long tick;
        lock (_gate)
        {
            tick = _lastAppliedTick;
        }

        _transport.Send(InputCodec.Encode(new InputMessage(tick, entries)));
    }

    /// <summary>Sends <paramref name="body"/> to the authority as a game message. Its budget applies.</summary>
    public void SendMessage(byte[] body) => _transport.Send(SessionMessage.EncodeMessage(body));

    /// <summary>
    /// Blends the world towards where the authority was, <see cref="InterpolationDelay"/> ago. Call
    /// once per frame. It does nothing when not interpolating.
    /// </summary>
    public void Advance()
    {
        lock (_gate)
        {
            if (!IsInterpolating || _history.Count == 0)
            {
                return;
            }

            var target = _now() - InterpolationDelay;

            // Behind everything held: show the oldest rather than extrapolating into a future the
            // authority has not described.
            if (target <= _history[0].ReceivedAt)
            {
                ApplyState(_history[0], null, 0);
                return;
            }

            // Ahead of everything held, the authority has gone quiet. Holding the newest state is
            // honest. Guessing would invent motion that never happened.
            if (target >= _history[^1].ReceivedAt)
            {
                ApplyState(_history[^1], null, 0);
                return;
            }

            BlendAt(target);
        }
    }

    public void Dispose() => _subscription.Dispose();

    private static Func<TimeSpan> StartClock()
    {
        var started = Stopwatch.StartNew();
        return () => started.Elapsed;
    }

    private InputEntry InputOf(ulong networkId, IReadOnlyDictionary<ComponentType, byte[]> components)
    {
        var mask = 0UL;
        foreach (var type in components.Keys)
        {
            var bit = Set.BitOf(type)
                ?? throw new ArgumentException($"{type.Name} is not a replicated component.", nameof(components));
            mask |= 1UL << bit;
        }

        var row = new byte[Set.StrideOf(mask)];
        var offset = 0;
        foreach (var bit in Set.BitsOf(mask))
        {
            var type = Set.AtBit(bit).Type;
            components[type].AsSpan(0, type.ByteSize).CopyTo(row.AsSpan(offset));
            offset += type.ByteSize;
        }

        return new InputEntry(networkId, mask, row);
    }

    private void Receive(byte[] message)
    {
        lock (_gate)
        {
            if (SessionMessage.DecodeGoodbye(message) is { } reason)
            {
                _onGoodbye?.Invoke(reason);
                return;
            }

            if (SessionMessage.DecodeMessage(message) is { } body)
            {
                _onMessage?.Invoke(body);
                return;
            }

            Apply(SnapshotCodec.Decode(message));
        }
    }

    private void Apply(DecodedSnapshot snapshot)
    {
        // A delta against a tick this client never applied would leave the world partly stale in a
        // way nothing later corrects. Acknowledging honestly instead makes the authority send a
        // full snapshot next time.
        if (snapshot.IsDelta && snapshot.BaselineTick != _lastAppliedTick)
        {
            _transport.Send(Ack.Encode(_lastAppliedTick));
            return;
        }

        var state = Merge(snapshot);
        _history.Add(state);
        while (_history.Count > HistoryDepth)
        {
            _history.RemoveAt(0);
        }

        if (!IsInterpolating)
        {
            ApplyState(state, null, 0);
        }

        _lastAppliedTick = snapshot.Tick;
        _transport.Send(Ack.Encode(_lastAppliedTick));
    }

    /// <summary>Folds a snapshot into the previous complete state.</summary>
    private WorldState Merge(DecodedSnapshot snapshot)
    {
        // A full snapshot is the whole truth, so it starts from nothing. A delta says nothing about
        // the entities it omits, so it starts from what stood.
        var entities = snapshot.IsDelta && _history.Count > 0
            ? new Dictionary<ulong, EntityState>(_history[^1].Entities)
            : [];

        foreach (var group in snapshot.Groups)
        {
            for (var i = 0; i < group.Length; i++)
            {
                entities[group.NetworkIds[i]] = new EntityState(group.Mask, group.RowAt(i).ToArray());
            }
        }

        foreach (var networkId in snapshot.Despawned)
        {
            entities.Remove(networkId);
        }

        return new WorldState(snapshot.Tick, _now(), entities);
    }

    private void BlendAt(TimeSpan target)
    {
        var index = 0;
        for (var i = 0; i < _history.Count - 1; i++)
        {
            if (_history[i + 1].ReceivedAt > target)
            {
                index = i;
                break;
            }
        }

        var from = _history[index];
        var to = _history[index + 1];
        var span = (to.ReceivedAt - from.ReceivedAt).TotalSeconds;
        var t = span <= 0 ? 1.0 : Math.Clamp((target - from.ReceivedAt).TotalSeconds / span, 0.0, 1.0);
        ApplyState(from, to, t);
    }

    private void ApplyState(WorldState state, WorldState? blendTowards, double t)
    {
        foreach (var (networkId, entity) in state.Entities)
        {
            var other = blendTowards?.Entities.GetValueOrDefault(networkId);

            // Blending only makes sense between two descriptions of the same thing. A component set
            // that changed mid-blend is a structural event, and the earlier state is the one that is
            // safe to show.
            ApplyEntity(networkId, entity, other is not null && other.Mask == entity.Mask ? other : null, t);
        }

        foreach (var networkId in _entities.Keys.ToArray())
        {
            if (!state.Entities.ContainsKey(networkId))
            {
                Despawn(networkId);
            }
        }
    }

    private void ApplyEntity(ulong networkId, EntityState state, EntityState? towards, double t)
    {
        var isNew = !_entities.TryGetValue(networkId, out var entity);
        if (isNew)
        {
            entity = _world.CreateEntity();
            _world.Add<ulong>(entity, _networkId, [networkId]);
            _entities[networkId] = entity;
        }

        // The component set is reconciled before anything is written, because adding or removing
        // one moves the entity between archetypes and invalidates every view taken before the move.
        foreach (var component in Set.Components)
        {
            var wanted = (state.Mask & (1UL << component.Bit)) != 0;
            var present = _world.Has(entity, component.Type);
            if (wanted && !present)
            {
                _world.Add(entity, component.Type);
            }
            else if (!wanted && present)
            {
                _world.Remove(entity, component.Type);
            }
        }

        WriteRow(entity, state, towards, t);
        if (isNew)
        {
            _onSpawn?.Invoke(networkId, entity);
        }
    }

    private void WriteRow(ulong entity, EntityState state, EntityState? towards, double t)
    {
        var offset = 0;
        foreach (var bit in Set.BitsOf(state.Mask))
        {
            var type = Set.AtBit(bit).Type;
            var size = type.ByteSize;
            _world.TryGetBytes(entity, type, out var target);

            if (towards is not null && type.Kind == ComponentKind.Float32)
            {
                // Positions and rotations are what motion is made of, so they blend. Anything else,
                // health, flags, ids, takes the earlier value, since a half-applied integer is not
                // a smaller error than a late one.
                Blend(state.Row.AsSpan(offset, size), towards.Row.AsSpan(offset, size), target, t);
            }
            else
            {
                state.Row.AsSpan(offset, size).CopyTo(target);
            }

            offset += size;
        }
    }

    private static void Blend(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> target, double t)
    {
        for (var e = 0; e < a.Length; e += 4)
        {
            double from = BinaryPrimitives.ReadSingleLittleEndian(a[e..]);
            double to = BinaryPrimitives.ReadSingleLittleEndian(b[e..]);
            BinaryPrimitives.WriteSingleLittleEndian(target[e..], (float)(from + ((to - from) * t)));
        }
    }

    private void Despawn(ulong networkId)
    {
        if (!_entities.Remove(networkId, out var entity))
        {
            return;
        }

        _onDespawn?.Invoke(networkId, entity);
        _world.DestroyEntity(entity);
    }

    /// <summary>One entity's replicated state, as a complete row.</summary>
    private sealed record EntityState(ulong Mask, byte[] Row);

    /// <summary>
    /// Everything the authority had said as of one tick. Deltas describe change, not state, so the
    /// client rebuilds the whole picture as messages arrive. That is what makes interpolation
    /// possible at all: you cannot blend towards a frame you only have the difference to.
    /// </summary>
    private sealed record WorldState(long Tick, TimeSpan ReceivedAt, Dictionary<ulong, EntityState> Entities);
}
