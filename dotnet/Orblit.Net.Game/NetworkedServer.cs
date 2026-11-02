using Orblit.Core;
using Orblit.Game;
using Orblit.Scene;

namespace Orblit.Net.Game;

/// <summary>
/// The server end of a networked scene. It shares each entity that carries a <c>net</c> component,
/// and sends everything else nowhere.
/// </summary>
/// <remarks>
/// The game host calls it on each side of the game's update. Before the update it takes what the
/// owning clients moved into the scene, so the game reads one world. After the update it spawns what
/// is new, removes what is gone, writes where the server's entities are and publishes a tick.
/// <para>
/// An entity with <see cref="NetAuthority.Server"/> is moved by the game and nobody else. One with
/// <see cref="NetAuthority.Owner"/> is moved by its owner once <see cref="Assign"/> has named one, and
/// by the game before that. A write from any other client is refused by the host and counted in
/// <see cref="NetHost.Rejections"/>. While an owner holds an entity, a move by the server's game is
/// undone by the owner's place at the start of the next update. The same goes for its variables.
/// </para>
/// <para>
/// A variable the game sets through <see cref="NetVars.Var{T}"/> is written into its column after the
/// game's update, for each entity whose <c>sync</c> list names it.
/// </para>
/// <para>
/// A call from a client is queued when it arrives and runs at the start of the next update, after the
/// owners' moves are in the scene. A call to a client is held until the end of the update that made
/// it, and goes out after the tick, in the order the calls were made. See <see cref="NetCalls"/>.
/// </para>
/// <para>
/// What is shared is read from the scene once for each new document the game makes, and the read
/// takes time in step with the number of entities. A game that changes the scene every frame reads it
/// every frame. The <c>net</c> component is read when the entity first appears. Changing it on a live
/// entity does nothing until the entity is removed and added again.
/// </para>
/// <para>
/// <see cref="AddClient"/>, <see cref="RemoveClient"/> and <see cref="Assign"/> belong to the game's
/// thread, like the calls a game makes. The host's own members are for the transport's thread.
/// </para>
/// </remarks>
public sealed class NetworkedServer : IGameNet, IVarHost, IServerCalls, IDisposable
{
    private readonly World _world = new();
    private readonly NetLayout _layout;
    private readonly VarStore _vars;
    private readonly Dictionary<string, Shared> _shared = [];
    private readonly List<string> _clients = [];
    private readonly CallRouter _router = new();
    private readonly List<(string? ClientId, byte[] Body)> _calls = [];
    private GameScene? _scene;
    private SceneDocument? _synced;

    /// <param name="vars">The variables of the game. Every client is built from the same schema.</param>
    public NetworkedServer(NetVarSchema? vars = null)
    {
        _vars = new VarStore(vars ?? NetVarSchema.Empty);
        _layout = new NetLayout(_world, _vars.Schema);
        Host = new NetHost(new NetHostOptions
        {
            World = _world,
            Set = _layout.Set,
            NetworkId = _layout.NetworkId,
            OnMessage = Receive,
        });
    }

    /// <summary>The host that sends the ticks. Its counts and its lock are for whoever runs the server.</summary>
    public NetHost Host { get; }

    /// <summary>
    /// How many client messages could not be used as a call: unreadable, naming an entity the scene
    /// lacks, or not fitting the handler. They are dropped, and the game goes on.
    /// </summary>
    public int Malformed => _router.Malformed;

    /// <summary>How many calls named a handler this server does not hold.</summary>
    public int UnhandledCalls => _router.Unhandled;

    /// <summary>How many calls made a handler throw. The game went on.</summary>
    public int FaultedCalls => _router.Faulted;

    /// <summary>What the handler of the last faulted call threw, or null when none has.</summary>
    public Exception? LastCallFault => _router.LastFault;

    public GameRole Role => GameRole.Server;

    public string? LocalClient => null;

    VarStore IVarHost.Vars => _vars;

    SceneDocument IVarHost.Document => Scene.Document;

    CallRouter ICallHost.Calls => _router;

    private GameScene Scene => _scene ?? throw new InvalidOperationException("Give this net to a GameHost before using it.");

    public string? OwnerOf(string entityId) =>
        _shared.TryGetValue(entityId, out var shared) ? Host.OwnerOf(shared.NetworkId) : null;

    public void Attach(GameScene scene) => _scene = scene;

    /// <summary>
    /// Starts sending to a client. It is told about every entity that is live now, and then sees
    /// each later one as it appears.
    /// </summary>
    public void AddClient(string clientId, ITransport transport)
    {
        SyncChanges();
        Host.AddClient(clientId, transport);
        _clients.Add(clientId);
        foreach (var shared in _shared.Values)
        {
            Tell(clientId, shared);
        }
    }

    /// <summary>
    /// Stops sending to a client. What it owned goes back to the game, and the other clients are told.
    /// </summary>
    public void RemoveClient(string clientId)
    {
        var owned = _shared.Values.Where(shared => Host.OwnerOf(shared.NetworkId) == clientId).ToArray();
        Host.RemoveClient(clientId);
        _clients.Remove(clientId);
        foreach (var shared in owned)
        {
            Broadcast(NetWire.Owner(shared.NetworkId, null));
        }
    }

    /// <summary>
    /// Gives an entity with owner authority to a client, or takes it back when <paramref name="clientId"/>
    /// is null.
    /// </summary>
    /// <exception cref="InvalidOperationException">The entity is not shared, or the server holds its authority.</exception>
    /// <exception cref="ArgumentException"><paramref name="clientId"/> is not a client of this server.</exception>
    public void Assign(string entityId, string? clientId)
    {
        if (clientId is not null && !_clients.Contains(clientId))
        {
            throw new ArgumentException($"\"{clientId}\" is not a client of this server.", nameof(clientId));
        }

        SyncChanges();
        if (!_shared.TryGetValue(entityId, out var shared))
        {
            throw new InvalidOperationException($"\"{entityId}\" is not a shared entity.");
        }

        if (shared.Authority != NetAuthority.Owner)
        {
            throw new InvalidOperationException($"\"{entityId}\" has server authority, so no client can own it.");
        }

        Host.SetOwner(shared.NetworkId, clientId);
        Broadcast(NetWire.Owner(shared.NetworkId, clientId));
    }

    bool IVarHost.MayWrite(string entityId) => true;

    void ICallHost.Call(Outgoing call) => _router.Run(new NetCaller(null), call.Call, Scene.Document);

    // A client makes an entity when a tick carries it. A call that names one the game made in this
    // update would reach the client first and be dropped, so calls wait until the tick is out.
    void IServerCalls.CallClient(string clientId, Outgoing call) => _calls.Add((clientId, call.Body));

    void IServerCalls.CallAll(Outgoing call) => _calls.Add((null, call.Body));

    public void BeforeUpdate()
    {
        TakeOwnerVars();
        var moves = new List<SceneOp>();
        foreach (var (entityId, floats) in OwnerMoves())
        {
            if (Scene.Document[entityId] is { } entity && NetLayout.Placing(entity, floats) is { } move)
            {
                moves.Add(move);
            }
        }

        Scene.Apply(new SceneDiff(moves));
        _router.Dispatch(Scene.Document);
    }

    public void AfterUpdate()
    {
        SyncChanges();
        WriteVars();
        Host.Publish();
        SendCalls();
    }

    public void Dispose()
    {
        Host.Dispose();
        _world.Dispose();
    }

    // Runs on the transport's thread with the host's lock held. A client sends calls and nothing else.
    private void Receive(string clientId, byte[] body)
    {
        if (NetWire.ReadCall(body) is { } call)
        {
            _router.Enqueue(new NetCaller(clientId), call);
        }
        else
        {
            _router.Reject();
        }
    }

    private void SyncChanges()
    {
        var document = Scene.Document;
        if (ReferenceEquals(document, _synced))
        {
            return;
        }

        var live = new HashSet<string>();
        foreach (var entity in document.Entities)
        {
            if (entity[SceneComponents.Net] is NetComponent tag)
            {
                live.Add(entity.Id);
                Share(entity, tag);
            }
        }

        foreach (var gone in _shared.Keys.Where(id => !live.Contains(id)).ToArray())
        {
            Host.Despawn(_shared[gone].Entity);
            _shared.Remove(gone);
            _vars.Forget(gone);
        }

        _synced = document;
    }

    private void Share(SceneEntity entity, NetComponent tag)
    {
        if (!_shared.TryGetValue(entity.Id, out var shared))
        {
            Spawn(entity, tag);
        }
        else if (!ReferenceEquals(shared.Seen, entity))
        {
            shared.Seen = entity;
            WriteTransform(shared, entity);
        }
    }

    private void Spawn(SceneEntity entity, NetComponent tag)
    {
        Shared shared;
        lock (Host.Gate)
        {
            var ecs = _world.CreateEntity();
            _world.Add(ecs, _layout.Transform);
            _layout.WriteColumn(ecs, NetLayout.PlaceOf(entity));
            shared = new Shared(entity.Id, Host.Spawn(ecs), ecs, tag, _vars.Schema.SharedBy(tag)) { Seen = entity };
            AddVars(shared);
        }

        _shared[entity.Id] = shared;
        Broadcast(NetWire.Spawn(shared.NetworkId, entity));
    }

    // The owner's place is the truth while it has one. Writing the scene's over it would undo the
    // owner's last move.
    private void WriteTransform(Shared shared, SceneEntity entity)
    {
        if (shared.SyncsTransform && !IsOwned(shared))
        {
            lock (Host.Gate)
            {
                _layout.WriteColumn(shared.Entity, NetLayout.PlaceOf(entity));
            }
        }
    }

    private bool IsOwned(Shared shared) => Host.OwnerOf(shared.NetworkId) is not null;

    private bool IsHeld(Shared shared) => shared.Authority == NetAuthority.Owner && IsOwned(shared);

    // Called with the host's lock held. The columns start at zero because WriteVars fills them
    // before the first tick goes out.
    private void AddVars(Shared shared)
    {
        foreach (var name in shared.Vars)
        {
            _layout.AddVar(shared.Entity, name);
        }
    }

    // The owner's variables are the truth while it holds the entity, as its place is.
    private void TakeOwnerVars()
    {
        lock (Host.Gate)
        {
            foreach (var shared in _shared.Values.Where(IsHeld))
            {
                foreach (var name in shared.Vars)
                {
                    if (_layout.ReadVar(shared.Entity, name) is { } bytes)
                    {
                        _vars.Write(shared.EntityId, name, bytes);
                    }
                }
            }
        }
    }

    private void WriteVars()
    {
        lock (Host.Gate)
        {
            foreach (var shared in _shared.Values.Where(shared => !IsHeld(shared)))
            {
                foreach (var name in shared.Vars)
                {
                    if (_vars.Find(shared.EntityId, name) is { } cell)
                    {
                        _layout.WriteVar(shared.Entity, name, cell.Bytes);
                    }
                }
            }
        }
    }

    private List<(string EntityId, float[] Floats)> OwnerMoves()
    {
        var moves = new List<(string, float[])>();
        lock (Host.Gate)
        {
            foreach (var (entityId, shared) in _shared)
            {
                if (shared.SyncsTransform && IsHeld(shared) && TakePlace(shared) is { } floats)
                {
                    moves.Add((entityId, floats));
                }
            }
        }

        return moves;
    }

    // Called with the host's lock held. The owner's client wrote the column, so its floats are not
    // trusted. A place that is not a number would reach every other copy, so the scene's place goes
    // back into the column and the move is dropped.
    private float[]? TakePlace(Shared shared)
    {
        var floats = _layout.ReadColumn(shared.Entity);
        if (NetLayout.IsPlace(floats))
        {
            return floats;
        }

        if (Scene.Document[shared.EntityId] is { } entity)
        {
            _layout.WriteColumn(shared.Entity, NetLayout.PlaceOf(entity));
        }

        return null;
    }

    private void Tell(string clientId, Shared shared)
    {
        if (Scene.Document[shared.EntityId] is not { } entity)
        {
            return;
        }

        Host.SendMessage(clientId, NetWire.Spawn(shared.NetworkId, entity));
        if (Host.OwnerOf(shared.NetworkId) is { } owner)
        {
            Host.SendMessage(clientId, NetWire.Owner(shared.NetworkId, owner));
        }
    }

    private void SendCalls()
    {
        foreach (var (clientId, body) in _calls)
        {
            if (clientId is null)
            {
                Broadcast(body);
            }
            else
            {
                Host.SendMessage(clientId, body);
            }
        }

        _calls.Clear();
    }

    private void Broadcast(byte[] body)
    {
        foreach (var clientId in _clients)
        {
            Host.SendMessage(clientId, body);
        }
    }

    private sealed class Shared(string entityId, ulong networkId, ulong entity, NetComponent tag, string[] vars)
    {
        public string EntityId { get; } = entityId;

        public ulong NetworkId { get; } = networkId;

        public ulong Entity { get; } = entity;

        public NetAuthority Authority { get; } = tag.Authority;

        public bool SyncsTransform { get; } = tag.Sync.Contains(NetComponent.TransformState);

        /// <summary>The declared variables this entity shares.</summary>
        public string[] Vars { get; } = vars;

        /// <summary>The entity as last written, so an untouched one costs a reference check.</summary>
        public SceneEntity? Seen { get; set; }
    }
}
