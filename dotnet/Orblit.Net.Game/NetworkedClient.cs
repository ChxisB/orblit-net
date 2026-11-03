using Orblit.Core;
using Orblit.Game;
using Orblit.Scene;

namespace Orblit.Net.Game;

/// <summary>
/// The client end of a networked scene. It shows what the server shares and sends what its player owns.
/// </summary>
/// <remarks>
/// The game host calls it on each side of the game's update. Before the update it makes the entities
/// the server has spawned, removes the ones it has despawned and moves the rest to where the server
/// says they are. After the update it sends where the entities this client owns have gone.
/// <para>
/// An entity the client's scene already holds under the same id is adopted rather than made again.
/// That is the case for an entity authored in the scene file, which every end loads. A client keeps
/// its own copy of such an entity until the server removes it. The server's removal reaches only a
/// client that was connected when the entity existed there.
/// </para>
/// <para>
/// While this client owns an entity with <see cref="NetAuthority.Owner"/>, the client's own scene
/// is the truth for it and the server's state is ignored. The server's state is a few frames old, and
/// putting it back would drag the player behind their own input.
/// </para>
/// <para>
/// A variable is shown the way the entity's place is. The owner of an entity with owner authority may
/// set the variables in its <c>sync</c> list, and the server and the other clients read them.
/// </para>
/// <para>
/// A call from the server is queued when it arrives and runs at the start of the next update, after
/// the server's state is in the scene. See <see cref="NetCalls"/>.
/// </para>
/// </remarks>
public sealed class NetworkedClient : IGameNet, IVarHost, ICallHost, IDisposable
{
    private readonly World _world = new();
    private readonly NetLayout _layout;
    private readonly VarStore _vars;
    private readonly NetClient _client;
    private readonly string _clientId;

    private readonly Dictionary<ulong, SceneEntity> _spawns = [];
    private readonly Dictionary<ulong, string> _owners = [];
    private readonly Dictionary<ulong, Shown> _shown = [];
    private readonly Dictionary<string, ulong> _networkIds = [];
    private readonly Dictionary<ulong, float[]> _sent = [];
    private readonly Dictionary<ulong, Dictionary<string, byte[]>> _sentVars = [];
    private readonly List<ulong> _arrived = [];
    private readonly List<ulong> _departed = [];
    private readonly CallRouter _router = new();
    private GameScene? _scene;

    public NetworkedClient(NetworkedClientOptions options)
    {
        _clientId = options.ClientId;
        _vars = new VarStore(options.Vars ?? NetVarSchema.Empty);
        _layout = new NetLayout(_world, _vars.Schema);
        _client = new NetClient(new NetClientOptions
        {
            World = _world,
            Set = _layout.Set,
            NetworkId = _layout.NetworkId,
            Transport = options.Transport,
            InterpolationDelay = options.InterpolationDelay,
            Clock = options.Clock,
            OnSpawn = (networkId, _) => _arrived.Add(networkId),
            OnDespawn = (networkId, _) => _departed.Add(networkId),
            OnMessage = Receive,
        });
    }

    public GameRole Role => GameRole.Client;

    public string? LocalClient => _clientId;

    /// <summary>
    /// How many server messages could not be used. They are unreadable, or a call naming an entity the
    /// scene lacks, or a call that does not fit its handler. They are dropped, and the game goes on.
    /// </summary>
    public int Malformed => _router.Malformed;

    /// <summary>How many calls named a handler this client does not hold.</summary>
    public int UnhandledCalls => _router.Unhandled;

    /// <summary>How many calls made a handler throw. The game went on.</summary>
    public int FaultedCalls => _router.Faulted;

    /// <summary>What the handler of the last faulted call threw, or null when none has.</summary>
    public Exception? LastCallFault => _router.LastFault;

    VarStore IVarHost.Vars => _vars;

    SceneDocument IVarHost.Document => Scene.Document;

    CallRouter ICallHost.Calls => _router;

    private GameScene Scene => _scene ?? throw new InvalidOperationException("Give this net to a GameHost before using it.");

    public string? OwnerOf(string entityId)
    {
        lock (_client.Gate)
        {
            return _networkIds.TryGetValue(entityId, out var networkId) ? _owners.GetValueOrDefault(networkId) : null;
        }
    }

    public void Attach(GameScene scene) => _scene = scene;

    bool IVarHost.MayWrite(string entityId) => OwnerOf(entityId) == _clientId;

    void ICallHost.Call(Outgoing call) => _client.SendMessage(call.Body);

    public void BeforeUpdate()
    {
        _client.Advance();
        Scene.Apply(TakeStructure());
        Scene.Apply(TakePlaces());
        TakeVars();
        _router.Dispatch(Scene.Document);
    }

    public void AfterUpdate()
    {
        var writes = TakeOwnedChanges();
        if (writes.Count > 0)
        {
            _client.SendInput(writes);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _world.Dispose();
    }

    /// <summary>
    /// Sends a write for an entity as if this client owned it, whatever the server thinks. It exists
    /// so a test can play a client that cheats.
    /// </summary>
    internal void Forge(string entityId, float[] floats) => ForgeColumn(entityId, _layout.Transform, NetLayout.Bytes(floats));

    /// <summary>Like <see cref="Forge"/>, for a variable.</summary>
    internal void ForgeVar(string entityId, string name, byte[] bytes) => ForgeColumn(entityId, _layout.Columns[name], bytes);

    /// <summary>Sends a body as a game message, whatever it holds. It exists so a test can play a client that sends rubbish.</summary>
    internal void ForgeMessage(byte[] body) => _client.SendMessage(body);

    private void ForgeColumn(string entityId, ComponentType type, byte[] bytes)
    {
        ulong networkId;
        lock (_client.Gate)
        {
            networkId = _networkIds[entityId];
        }

        _client.SendInput(new Dictionary<ulong, IReadOnlyDictionary<ComponentType, byte[]>>
        {
            [networkId] = new Dictionary<ComponentType, byte[]> { [type] = bytes },
        });
    }

    // Runs on the transport's thread with the client's lock held.
    private void Receive(byte[] body)
    {
        switch (NetWire.Read(body))
        {
            case SpawnMessage spawn:
                _spawns[spawn.NetworkId] = spawn.Entity;
                break;
            case OwnerMessage owner:
                Hand(owner);
                break;
            case CallMessage call:
                _router.Enqueue(new NetCaller(null), call);
                break;
            default:
                _router.Reject();
                break;
        }
    }

    // What was last sent belongs to the last owner. The next one starts again from the server's state.
    private void Hand(OwnerMessage owner)
    {
        _sent.Remove(owner.NetworkId);
        _sentVars.Remove(owner.NetworkId);
        if (owner.ClientId is { } clientId)
        {
            _owners[owner.NetworkId] = clientId;
        }
        else
        {
            _owners.Remove(owner.NetworkId);
        }
    }

    // Departures go first. An entity that came and went between two frames was never seen, so it
    // is dropped rather than made and removed in the same diff.
    private SceneDiff TakeStructure()
    {
        var ops = new List<SceneOp>();
        lock (_client.Gate)
        {
            foreach (var networkId in _departed)
            {
                ops.AddRange(Forget(networkId));
            }

            foreach (var networkId in _arrived)
            {
                ops.AddRange(Show(networkId));
            }

            _departed.Clear();
            _arrived.Clear();
        }

        return new SceneDiff(ops);
    }

    private IEnumerable<SceneOp> Forget(ulong networkId)
    {
        _spawns.Remove(networkId);
        _owners.Remove(networkId);
        _sent.Remove(networkId);
        _sentVars.Remove(networkId);
        if (!_shown.Remove(networkId, out var shown))
        {
            return [];
        }

        _networkIds.Remove(shown.EntityId);
        _vars.Forget(shown.EntityId);
        return Scene.Document[shown.EntityId] is { } entity ? [new RemoveEntity(entity.ToJson())] : [];
    }

    private IEnumerable<SceneOp> Show(ulong networkId)
    {
        if (!_spawns.Remove(networkId, out var entity))
        {
            return [];
        }

        var tag = entity[SceneComponents.Net] as NetComponent ?? new NetComponent();
        _shown[networkId] = new Shown(
            entity.Id,
            tag.Authority,
            tag.Sync.Contains(NetComponent.TransformState),
            _vars.Schema.SharedBy(tag));
        _networkIds[entity.Id] = networkId;
        return Scene.Document.Contains(entity.Id) ? [] : [new AddEntity(entity.ToJson())];
    }

    private SceneDiff TakePlaces()
    {
        var ops = new List<SceneOp>();
        lock (_client.Gate)
        {
            foreach (var (networkId, shown) in _shown)
            {
                if (shown.SyncsTransform && PlaceFromServer(networkId, shown) is { } place)
                {
                    ops.Add(place);
                }
            }
        }

        return new SceneDiff(ops);
    }

    private SetComponent? PlaceFromServer(ulong networkId, Shown shown)
    {
        if (_client.EntityFor(networkId) is not { } ecs || Scene.Document[shown.EntityId] is not { } entity)
        {
            return null;
        }

        var floats = _layout.ReadColumn(ecs);
        if (!IsMine(networkId, shown))
        {
            return NetLayout.Placing(entity, floats);
        }

        // The first state after the entity is handed over is the last one the server holds. Later
        // changes are the player's, and are told apart from it by comparing with what was last sent.
        return _sent.TryAdd(networkId, floats) ? NetLayout.Placing(entity, floats) : null;
    }

    // Reads each variable the server sent into the game's cell. An owner is told once and then
    // trusts its own cell, as it does for its place.
    private void TakeVars()
    {
        lock (_client.Gate)
        {
            foreach (var (networkId, shown) in _shown)
            {
                if (_client.EntityFor(networkId) is { } ecs)
                {
                    TakeVarsOf(networkId, shown, ecs);
                }
            }
        }
    }

    private void TakeVarsOf(ulong networkId, Shown shown, ulong ecs)
    {
        var mine = IsMine(networkId, shown);
        foreach (var name in shown.Vars)
        {
            if (_layout.ReadVar(ecs, name) is { } bytes && (!mine || SentVarsOf(networkId).TryAdd(name, bytes)))
            {
                _vars.Write(shown.EntityId, name, bytes);
            }
        }
    }

    private Dictionary<ulong, IReadOnlyDictionary<ComponentType, byte[]>> TakeOwnedChanges()
    {
        var writes = new Dictionary<ulong, IReadOnlyDictionary<ComponentType, byte[]>>();
        lock (_client.Gate)
        {
            foreach (var (networkId, shown) in _shown)
            {
                if (IsMine(networkId, shown) && ChangedColumns(networkId, shown) is { Count: > 0 } columns)
                {
                    writes[networkId] = columns;
                }
            }
        }

        return writes;
    }

    private Dictionary<ComponentType, byte[]> ChangedColumns(ulong networkId, Shown shown)
    {
        var columns = new Dictionary<ComponentType, byte[]>();
        if (shown.SyncsTransform && TakeMove(networkId, shown) is { } floats)
        {
            columns[_layout.Transform] = NetLayout.Bytes(floats);
        }

        foreach (var name in shown.Vars)
        {
            if (TakeVarChange(networkId, shown.EntityId, name) is { } bytes)
            {
                columns[_layout.Columns[name]] = bytes;
            }
        }

        return columns;
    }

    private byte[]? TakeVarChange(ulong networkId, string entityId, string name)
    {
        var sent = SentVarsOf(networkId);
        if (!sent.TryGetValue(name, out var last) || _vars.Find(entityId, name) is not { } cell || cell.Bytes.AsSpan().SequenceEqual(last))
        {
            return null;
        }

        sent[name] = cell.Bytes;
        return cell.Bytes;
    }

    private Dictionary<string, byte[]> SentVarsOf(ulong networkId)
    {
        if (!_sentVars.TryGetValue(networkId, out var sent))
        {
            sent = [];
            _sentVars[networkId] = sent;
        }

        return sent;
    }

    private float[]? TakeMove(ulong networkId, Shown shown)
    {
        if (!_sent.TryGetValue(networkId, out var last) || Scene.Document[shown.EntityId] is not { } entity)
        {
            return null;
        }

        var floats = NetLayout.PlaceOf(entity);
        if (floats.AsSpan().SequenceEqual(last))
        {
            return null;
        }

        _sent[networkId] = floats;
        return floats;
    }

    private bool IsMine(ulong networkId, Shown shown) =>
        shown.Authority == NetAuthority.Owner && _owners.TryGetValue(networkId, out var owner) && owner == _clientId;

    private sealed record Shown(string EntityId, NetAuthority Authority, bool SyncsTransform, string[] Vars);
}
