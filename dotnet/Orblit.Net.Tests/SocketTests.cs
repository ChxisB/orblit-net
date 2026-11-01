namespace Orblit.Net.Tests;

public sealed class SocketTests : IAsyncLifetime
{
    private readonly List<IAsyncDisposable> _cleanup = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var item in Enumerable.Reverse(_cleanup))
        {
            await item.DisposeAsync();
        }
    }

    private SocketServer Serve()
    {
        var server = SocketServer.Bind();
        _cleanup.Add(server);
        return server;
    }

    private T Own<T>(T item)
        where T : IAsyncDisposable
    {
        _cleanup.Add(item);
        return item;
    }

    [Fact]
    public async Task ASessionReplicatesOverARealSocket()
    {
        var server = Serve();

        // Port zero means the operating system picks, so two tests never collide.
        Assert.True(server.Port > 0);

        using var host = new Peer(reverseRegistrationOrder: false);
        using var client = new Peer(reverseRegistrationOrder: true);
        var accepted = new TaskCompletionSource<SocketTransport>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listening = server.Listen(transport => accepted.TrySetResult(transport));

        var clientTransport = Own(await SocketTransport.ConnectAsync(server.Url));
        var hostTransport = Own(await accepted.Task.WaitAsync(Wait.Patience, TestContext.Current.CancellationToken));
        using var netHost = new NetHost(new NetHostOptions
        {
            World = host.World,
            Set = host.Set,
            NetworkId = host.NetworkId,
        });
        using var netClient = new NetClient(new NetClientOptions
        {
            World = client.World,
            Set = client.Set,
            NetworkId = client.NetworkId,
            Transport = clientTransport,
        });
        netHost.AddClient("player-1", hostTransport);

        var entity = host.World.CreateEntity();
        var networkId = netHost.Spawn(entity);
        host.World.Add<float>(entity, host.Position, [1, 2, 3]);

        netHost.Publish();
        await Wait.Until(() => netClient.EntityFor(networkId) is not null, "the spawn to arrive");

        var replica = netClient.EntityFor(networkId)!.Value;
        Assert.Equal([1f, 2f, 3f], ReplicaFloats(netClient, client, replica));

        // The acknowledgement travelled back, so the next publish is a delta.
        await Wait.Until(() => netClient.LastAppliedTick == 1, "the first tick");
        Column.SetFloat(host.World, entity, host.Position, 0, 9);
        netHost.Publish();
        await Wait.Until(() => ReplicaFloats(netClient, client, replica)[0] == 9, "the update to arrive");
    }

    private static float[] ReplicaFloats(NetClient netClient, Peer client, ulong replica)
    {
        lock (netClient.Gate)
        {
            return Column.Floats(client.World, replica, client.Position);
        }
    }

    [Fact]
    public async Task APlainHttpRequestIsAnsweredNotIgnored()
    {
        var server = Serve();
        using var http = new HttpClient();

        // A browser pointed at the game port should be told why nothing happens.
        using var response = await http.GetAsync(
            new Uri($"http://127.0.0.1:{server.Port}/"), TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SendingOnAClosedTransportDoesNothing()
    {
        var server = Serve();

        var transport = await SocketTransport.ConnectAsync(server.Url);
        await transport.CloseAsync();

        Assert.False(transport.IsOpen);
        transport.Send(new byte[4]);
    }

    [Fact]
    public async Task ASessionSurvivesADroppedSocketAndResumes()
    {
        var server = Serve();
        using var authority = new Peer(reverseRegistrationOrder: false);
        using var replica = new Peer(reverseRegistrationOrder: true);
        using var netHost = new NetHost(new NetHostOptions
        {
            World = authority.World,
            Set = authority.Set,
            NetworkId = authority.NetworkId,
            Sessions = new SessionPolicy { Admit = Names.Welcome, LingerTicks = 100 },
        });
        using var listening = server.Listen(netHost.Accept);

        NetClient NewClient(ITransport transport) => new(new NetClientOptions
        {
            World = replica.World,
            Set = replica.Set,
            NetworkId = replica.NetworkId,
            Transport = transport,
        });

        var first = await SocketTransport.ConnectAsync(server.Url);
        using var firstClient = NewClient(first);
        firstClient.Join(Names.Credentials("ann"));
        await Wait.Until(() => netHost.SessionStateOf("ann") == SessionState.Live, "the join to be admitted");

        await first.CloseAsync();
        firstClient.Dispose();
        await Wait.Until(() => netHost.SessionStateOf("ann") == SessionState.Lingering, "the drop to be noticed");

        var second = Own(await SocketTransport.ConnectAsync(server.Url));
        using var secondClient = NewClient(second);
        secondClient.Join(Names.Credentials("ann"));
        await Wait.Until(() => netHost.SessionStateOf("ann") == SessionState.Live, "the rejoin to resume the session");
        Assert.Equal(1, netHost.ClientCount);
    }
}
