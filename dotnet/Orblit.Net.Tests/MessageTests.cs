using Orblit.Core;

namespace Orblit.Net.Tests;

public sealed class MessageTests : IAsyncLifetime
{
    private readonly List<(string Id, byte[] Body)> _atHost = [];
    private readonly List<byte[]> _atClient = [];
    private World _authority = null!;
    private World _replica = null!;
    private LoopbackLink _link = null!;
    private NetHost _host = null!;
    private NetClient _client = null!;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _host.Dispose();
        _client.Dispose();
        await _link.CloseAsync();
        _authority.Dispose();
        _replica.Dispose();
    }

    private void Build(InboundBudget? budget = null, bool listening = true)
    {
        _authority = new World();
        _replica = new World();
        _link = new LoopbackLink();
        _host = new NetHost(new NetHostOptions
        {
            World = _authority,
            Set = new ReplicationSet([]),
            NetworkId = _authority.RegisterComponent("NetworkId", ComponentKind.Int64),
            Budget = budget,
            OnMessage = listening ? (id, body) => _atHost.Add((id, body)) : null,
        });
        _client = new NetClient(new NetClientOptions
        {
            World = _replica,
            Set = new ReplicationSet([]),
            NetworkId = _replica.RegisterComponent("NetworkId", ComponentKind.Int64),
            Transport = _link.Client,
            OnMessage = listening ? _atClient.Add : null,
        });
        _host.AddClient("a", _link.Host);
    }

    [Fact]
    public async Task AClientMessageReachesTheHostWithTheSenderAndTheBytes()
    {
        Build();

        _client.SendMessage([1, 2, 3]);
        await _link.IdleAsync();

        var (id, body) = Assert.Single(_atHost);
        Assert.Equal("a", id);
        Assert.Equal([1, 2, 3], body);
    }

    [Fact]
    public async Task AHostMessageReachesTheClient()
    {
        Build();

        _host.SendMessage("a", [9, 8]);
        await _link.IdleAsync();

        Assert.Equal([9, 8], Assert.Single(_atClient));
    }

    [Fact]
    public async Task AnEmptyMessageArrivesEmpty()
    {
        Build();

        _client.SendMessage([]);
        _host.SendMessage("a", []);
        await _link.IdleAsync();

        Assert.Empty(Assert.Single(_atHost).Body);
        Assert.Empty(Assert.Single(_atClient));
    }

    [Fact]
    public async Task MessagesArriveInTheOrderTheyWereSent()
    {
        Build();

        for (byte i = 0; i < 5; i++)
        {
            _host.SendMessage("a", [i]);
        }

        await _link.IdleAsync();

        Assert.Equal([0, 1, 2, 3, 4], _atClient.Select(message => (int)Assert.Single(message)));
    }

    [Fact]
    public async Task AMessageToAClientThatIsNotAttachedIsDropped()
    {
        Build();

        _host.SendMessage("nobody", [1]);
        await _link.IdleAsync();

        Assert.Empty(_atClient);
    }

    [Fact]
    public async Task MessagesAndSnapshotsShareALinkWithoutDisturbingEachOther()
    {
        Build();

        _host.SendMessage("a", [1]);
        _host.Publish();
        _host.SendMessage("a", [2]);
        _client.SendMessage([3]);
        await _link.IdleAsync();

        Assert.Equal(2, _atClient.Count);
        Assert.Equal(1, _client.LastAppliedTick);
        Assert.Single(_atHost);
    }

    [Fact]
    public async Task AHostAndClientWithNoHandlerIgnoreMessages()
    {
        Build(listening: false);

        _client.SendMessage([1]);
        _host.SendMessage("a", [2]);
        _host.Publish();
        await _link.IdleAsync();

        Assert.Equal(1, _client.LastAppliedTick);
    }

    [Fact]
    public async Task AMessageOverTheByteBudgetIsDroppedAndCounted()
    {
        Build(budget: new InboundBudget(bytesPerMessage: 8));

        _client.SendMessage(new byte[100]);
        await _link.IdleAsync();

        Assert.Empty(_atHost);
        Assert.Equal(1, _host.Rejections[InputRejection.OverBudget]);
    }

    [Fact]
    public async Task MessagesOverTheCountBudgetAreDroppedAndCounted()
    {
        Build(budget: new InboundBudget(messagesPerTick: 3));

        for (byte i = 0; i < 10; i++)
        {
            _client.SendMessage([i]);
        }

        await _link.IdleAsync();

        Assert.Equal(3, _atHost.Count);
        Assert.Equal(7, _host.Rejections[InputRejection.OverBudget]);
    }
}
