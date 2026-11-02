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
/// </remarks>
public sealed class NetworkedClient : IGameNet, IDisposable
{
    private readonly World _world = new();
    private readonly NetLayout _layout;
    private readonly NetClient _client;
    private readonly string _clientId;
    private readonly Dictionary<ulong, SceneEntity> _spawns = [];
    private readonly Dictionary<ulong, string> _owners = [];
    private readonly Dictionary<ulong, Shown> _shown = [];
    private readonly Dictionary<string, ulong> _networkIds = [];
    private readonly Dictionary<ulong, float[]> _sent = [];
    private readonly List<ulong> _arrived = [];
    private readonly List<ulong> _departed = [];
    private GameScene? _scene;
    private int _malformed;

    /// <param name="clientId">The id the server knows this client by.</param>
    /// <param name="transport">The link to the server.</param>
    /// <param name="interpolationDelay">How far behind the server to show other entities. Zero shows each tick as it lands.</param>
    /// <param name="clock">Time since the client started. Leave it out for a stopwatch.</param>
    public NetworkedClient(string clientId, ITransport transport, TimeSpan interpolationDelay = default, Func<TimeSpan>? clock = null)
    {
        _clientId = clientId;
        _layout = new NetLayout(_world);
        _client = new NetClient(new NetClientOptions
        {
            World = _world,
            Set = _layout.Set,
            NetworkId = _layout.NetworkId,
            Transport = transport,
            InterpolationDelay = interpolationDelay,
            Clock = clock,
            OnSpawn = (networkId, _) => _arrived.Add(networkId),
            OnDespawn = (networkId, _) => _departed.Add(networkId),
            OnMessage = Receive,
        });
    }

    public GameRole Role => GameRole.Client;

    public string? LocalClient => _clientId;

    /// <summary>How many server messages could not be read. They are dropped, and the game goes on.</summary>
    public int Malformed
    {
        get
        {
            lock (_client.Gate)
            {
                return _malformed;
            }
        }
    }

    private GameScene Scene => _scene ?? throw new InvalidOperationException("Give this net to a GameHost before using it.");

    public string? OwnerOf(string entityId)
    {
        lock (_client.Gate)
        {
            return _networkIds.TryGetValue(entityId, out var networkId) ? _owners.GetValueOrDefault(networkId) : null;
        }
    }

    public void Attach(GameScene scene) => _scene = scene;

    public void BeforeUpdate()
    {
        _client.Advance();
        Scene.Apply(TakeStructure());
        Scene.Apply(TakePlaces());
    }

    public void AfterUpdate()
    {
        var writes = TakeOwnedMoves();
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
    internal void Forge(string entityId, float[] floats)
    {
        ulong networkId;
        lock (_client.Gate)
        {
            networkId = _networkIds[entityId];
        }

        _client.SendInput(new Dictionary<ulong, IReadOnlyDictionary<ComponentType, byte[]>>
        {
            [networkId] = new Dictionary<ComponentType, byte[]> { [_layout.Transform] = NetLayout.Bytes(floats) },
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
            default:
                _malformed++;
                break;
        }
    }

    // What was last sent belongs to the last owner. The next one starts again from the server's state.
    private void Hand(OwnerMessage owner)
    {
        _sent.Remove(owner.NetworkId);
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
        if (!_shown.Remove(networkId, out var shown))
        {
            return [];
        }

        _networkIds.Remove(shown.EntityId);
        return Scene.Document[shown.EntityId] is { } entity ? [new RemoveEntity(entity.ToJson())] : [];
    }

    private IEnumerable<SceneOp> Show(ulong networkId)
    {
        if (!_spawns.Remove(networkId, out var entity))
        {
            return [];
        }

        var tag = entity[SceneComponents.Net] as NetComponent ?? new NetComponent();
        _shown[networkId] = new Shown(entity.Id, tag.Authority, tag.Sync.Contains(NetComponent.TransformState));
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

    private Dictionary<ulong, IReadOnlyDictionary<ComponentType, byte[]>> TakeOwnedMoves()
    {
        var writes = new Dictionary<ulong, IReadOnlyDictionary<ComponentType, byte[]>>();
        lock (_client.Gate)
        {
            foreach (var (networkId, shown) in _shown)
            {
                if (shown.SyncsTransform && IsMine(networkId, shown) && Moved(networkId, shown) is { } floats)
                {
                    writes[networkId] = new Dictionary<ComponentType, byte[]> { [_layout.Transform] = NetLayout.Bytes(floats) };
                }
            }
        }

        return writes;
    }

    private float[]? Moved(ulong networkId, Shown shown)
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

    private sealed record Shown(string EntityId, NetAuthority Authority, bool SyncsTransform);
}
