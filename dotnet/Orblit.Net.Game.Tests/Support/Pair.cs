using Orblit.Game;
using Orblit.Input;
using Orblit.Scene;
using Orblit.VectorMath;

namespace Orblit.Net.Game.Tests;

/// <summary>
/// One server and any number of clients, each a real <see cref="GameHost"/> over a loopback link, so a
/// test drives the same frame calls the editor and the runner do.
/// </summary>
internal sealed class Pair : IAsyncDisposable
{
    internal static readonly GameInput Idle = new(new FrameControls());
    private readonly List<LoopbackLink> _links = [];
    private readonly Dictionary<string, NetworkedClient> _clients = [];
    private readonly Dictionary<string, ScriptedGame> _games = [];

    public Pair(SceneDocument scene, Func<TimeSpan>? clock = null, TimeSpan interpolationDelay = default)
    {
        Scene = scene;
        Clock = clock;
        InterpolationDelay = interpolationDelay;
        Server = new NetworkedServer();
        ServerGame = new ScriptedGame();
        ServerHost = new GameHost(ServerGame, scene, "/project", new SilentLog());
        ServerHost.UseNet(Server);
        ServerHost.Start();
    }

    public SceneDocument Scene { get; }

    public Func<TimeSpan>? Clock { get; }

    public TimeSpan InterpolationDelay { get; }

    public NetworkedServer Server { get; }

    public ScriptedGame ServerGame { get; }

    public GameHost ServerHost { get; }

    public NetworkedClient Client(string clientId) => _clients[clientId];

    public ScriptedGame ClientGame(string clientId) => _games[clientId];

    /// <summary>Connects a client that sees the scene the server started from, and returns its host.</summary>
    public async Task<GameHost> Join(string clientId, SceneDocument? scene = null)
    {
        var link = new LoopbackLink();
        _links.Add(link);
        var client = new NetworkedClient(clientId, link.Client, InterpolationDelay, Clock);
        var game = new ScriptedGame();
        _clients[clientId] = client;
        _games[clientId] = game;
        var host = new GameHost(game, scene ?? Scene, "/project", new SilentLog());
        host.UseNet(client);
        host.Start();
        Server.AddClient(clientId, link.Host);
        await Settle();
        return host;
    }

    /// <summary>The server's frame, then a pause for the wire, then each client's frame.</summary>
    public async Task Step(params GameHost[] clients)
    {
        ServerHost.Update(Idle, 0.016);
        await Settle();
        foreach (var client in clients)
        {
            client.Update(Idle, 0.016);
        }

        await Settle();
    }

    public async Task Settle()
    {
        foreach (var link in _links)
        {
            await link.IdleAsync();
        }
    }

    /// <summary>The server's game moves an entity on its next update, as a game would.</summary>
    public void Move(string entityId, double x, double y, double z) => ServerGame.Once(context =>
        context.Scene.Apply(Moved(context.Scene.Document, entityId, new Vector3(x, y, z))));

    public static SceneDiff Moved(SceneDocument scene, string entityId, Vector3 to)
    {
        var before = (TransformComponent?)scene[entityId]![SceneComponents.Transform];
        var after = new TransformComponent(to, before?.Rotation, before?.Scale);
        return new SceneDiff([new SetComponent(entityId, SceneComponents.Transform, before?.ToJson(), after.ToJson())]);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        Server.Dispose();
        foreach (var link in _links)
        {
            await link.CloseAsync();
        }
    }

    internal sealed class ScriptedGame : IGame
    {
        public Action<GameContext>? OnUpdate { get; set; }

        /// <summary>Runs <paramref name="action"/> in the next update only.</summary>
        public void Once(Action<GameContext> action) => OnUpdate = context =>
        {
            OnUpdate = null;
            action(context);
        };

        public void Start(GameContext context)
        {
        }

        public void Update(GameContext context, double seconds) => OnUpdate?.Invoke(context);

        public void Stop(GameContext context)
        {
        }
    }

    private sealed class SilentLog : IGameLog
    {
        public void Say(string message)
        {
        }

        public void Error(string message, Exception error) => throw new InvalidOperationException(message, error);
    }
}
