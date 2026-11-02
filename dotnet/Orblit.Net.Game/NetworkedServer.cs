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
/// undone by the owner's place at the start of the next update.
/// </para>
/// <para>
/// What is shared is read from the scene when its document changes, and not on every frame. The
/// <c>net</c> component is read when the entity first appears. Changing it on a live entity does
/// nothing until the entity is removed and added again.
/// </para>
/// </remarks>
public sealed class NetworkedServer : IGameNet, IDisposable
{
    private readonly World _world = new();
    private readonly NetLayout _layout;
    private readonly Dictionary<string, Shared> _shared = [];
    private readonly List<string> _clients = [];
    private GameScene? _scene;
    private SceneDocument? _synced;

    public NetworkedServer()
    {
        _layout = new NetLayout(_world);
        Host = new NetHost(new NetHostOptions { World = _world, Set = _layout.Set, NetworkId = _layout.NetworkId });
    }

    /// <summary>The host that sends the ticks. Its counts and its lock are for whoever runs the server.</summary>
    public NetHost Host { get; }

    public GameRole Role => GameRole.Server;

    public string? LocalClient => null;

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
    public void Assign(string entityId, string? clientId)
    {
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

    public void BeforeUpdate()
    {
        var moves = new List<SceneOp>();
        foreach (var (entityId, floats) in OwnerMoves())
        {
            if (Scene.Document[entityId] is { } entity && NetLayout.Placing(entity, floats) is { } move)
            {
                moves.Add(move);
            }
        }

        Scene.Apply(new SceneDiff(moves));
    }

    public void AfterUpdate()
    {
        SyncChanges();
        Host.Publish();
    }

    public void Dispose()
    {
        Host.Dispose();
        _world.Dispose();
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
            shared = new Shared(entity.Id, Host.Spawn(ecs), ecs, tag) { Seen = entity };
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

    private List<(string EntityId, float[] Floats)> OwnerMoves()
    {
        var moves = new List<(string, float[])>();
        lock (Host.Gate)
        {
            foreach (var (entityId, shared) in _shared)
            {
                if (shared.SyncsTransform && shared.Authority == NetAuthority.Owner && IsOwned(shared))
                {
                    moves.Add((entityId, _layout.ReadColumn(shared.Entity)));
                }
            }
        }

        return moves;
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

    private void Broadcast(byte[] body)
    {
        foreach (var clientId in _clients)
        {
            Host.SendMessage(clientId, body);
        }
    }

    private sealed class Shared(string entityId, ulong networkId, ulong entity, NetComponent tag)
    {
        public string EntityId { get; } = entityId;

        public ulong NetworkId { get; } = networkId;

        public ulong Entity { get; } = entity;

        public NetAuthority Authority { get; } = tag.Authority;

        public bool SyncsTransform { get; } = tag.Sync.Contains(NetComponent.TransformState);

        /// <summary>The entity as last written, so an untouched one costs a reference check.</summary>
        public SceneEntity? Seen { get; set; }
    }
}
