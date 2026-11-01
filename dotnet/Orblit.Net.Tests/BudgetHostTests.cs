namespace Orblit.Net.Tests;

public sealed class BudgetHostTests : IAsyncLifetime
{
    private readonly Harness _harness = new(ownerWritable: ["Position"]);
    private readonly List<(string Key, GoneReason Reason)> _gone = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    private NetHost Host => _harness.Host;

    private void Build(InboundBudget? budget = null, SessionPolicy? sessions = null) =>
        _harness.Host = new NetHost(new NetHostOptions
        {
            World = _harness.Authority.World,
            Set = _harness.Authority.Set,
            NetworkId = _harness.Authority.NetworkId,
            Budget = budget,
            Sessions = sessions,
        });

    private SessionPolicy Policy(int lingerTicks = 600) => new()
    {
        Admit = Names.Welcome,
        LingerTicks = lingerTicks,
        OnGone = (key, reason) => _gone.Add((key, reason)),
    };

    private ulong SpawnFor(string key)
    {
        var entity = _harness.Authority.World.CreateEntity();
        _harness.Authority.World.Add(entity, _harness.Authority.Position);
        return Host.Spawn(entity, key);
    }

    private Visitor AddPlayer(string key)
    {
        var player = _harness.NewVisitor();
        Host.AddClient(key, player.Link.Host);
        player.NetworkId = SpawnFor(key);
        return player;
    }

    private async Task Publish(int times)
    {
        for (var i = 0; i < times; i++)
        {
            Host.Publish();
        }

        await _harness.Settle();
    }

    [Fact]
    public async Task AThousandMessagesInATickLetOnlyTheBudgetThrough()
    {
        Build(new InboundBudget(messagesPerTick: 10));
        var ann = AddPlayer("ann");
        ann.Flood(1000);
        await _harness.Settle();

        Assert.Equal(10, Host.AcceptedInputs);
        Assert.Equal(990, Host.Rejections[InputRejection.OverBudget]);
    }

    [Fact]
    public async Task TheBudgetStartsAgainEachTick()
    {
        Build(new InboundBudget(messagesPerTick: 10));
        var ann = AddPlayer("ann");
        ann.Flood(15);
        await _harness.Settle();
        await Publish(1);
        ann.Flood(15);
        await _harness.Settle();

        // The second tick has the ack for the snapshot in it, so nine inputs fit.
        Assert.Equal(19, Host.AcceptedInputs);
    }

    [Fact]
    public async Task AnInputUnderBytesPerMessageIsApplied()
    {
        Build();
        var ann = AddPlayer("ann");
        ann.Link.Client.Send(ann.Move());
        await _harness.Settle();

        Assert.Equal(1, Host.AcceptedInputs);
    }

    [Fact]
    public async Task AnInputOverBytesPerMessageIsNotApplied()
    {
        Build(new InboundBudget(bytesPerMessage: 40));
        var ann = AddPlayer("ann");
        Assert.True(ann.Move().Length > 40);
        ann.Link.Client.Send(ann.Move());
        await _harness.Settle();

        Assert.Equal(0, Host.AcceptedInputs);
        Assert.Equal(1, Host.Rejections[InputRejection.OverBudget]);
    }

    [Fact]
    public async Task ABurstShorterThanStrikesDoesNotDisconnect()
    {
        Build(new InboundBudget(messagesPerTick: 5, strikes: 3));
        var ann = AddPlayer("ann");

        foreach (var flooding in new[] { true, true, false, false, true, true })
        {
            if (flooding)
            {
                ann.Flood(20);
            }

            await _harness.Settle();
            await Publish(1);
        }

        Assert.Equal(1, Host.ClientCount);
        Assert.Empty(ann.Reasons);
    }

    [Fact]
    public async Task AFlooderWhoPausesNowAndThenIsStillCutOff()
    {
        Build(new InboundBudget(messagesPerTick: 5, strikes: 3));
        var ann = AddPlayer("ann");

        foreach (var flooding in new[] { true, true, false, true, true })
        {
            if (flooding)
            {
                ann.Flood(20);
            }

            await _harness.Settle();
            await Publish(1);
        }

        Assert.Equal([GoodbyeReason.OverBudget], ann.Reasons);
        Assert.Equal(0, Host.ClientCount);
    }

    [Fact]
    public async Task ASustainedBreachDisconnectsAndFreesWhatItOwned()
    {
        Build(new InboundBudget(messagesPerTick: 5, strikes: 3));
        var ann = AddPlayer("ann");
        var bob = AddPlayer("bob");

        for (var round = 0; round < 3; round++)
        {
            ann.Flood(20);
            await _harness.Settle();
            await Publish(1);
        }

        Assert.Equal([GoodbyeReason.OverBudget], ann.Reasons);
        Assert.Equal(1, Host.ClientCount);
        Assert.Null(Host.OwnerOf(ann.NetworkId));
        Assert.Empty(bob.Reasons);
        Assert.Equal(2, bob.Client.EntityCount);
    }

    [Fact]
    public async Task AFloodingSessionEndsAsOverBudgetAndCannotResume()
    {
        Build(new InboundBudget(messagesPerTick: 5, strikes: 2), Policy());
        var ann = _harness.NewVisitor();
        Host.Accept(ann.Link.Host);
        ann.Client.Join(Names.Credentials("ann"));
        await _harness.Settle();
        ann.NetworkId = SpawnFor("ann");

        for (var round = 0; round < 2; round++)
        {
            ann.Flood(20);
            await _harness.Settle();
            await Publish(1);
        }

        Assert.Equal([GoodbyeReason.OverBudget], ann.Reasons);
        Assert.Equal([("ann", GoneReason.OverBudget)], _gone);
        Assert.Null(Host.SessionStateOf("ann"));
    }

    [Fact]
    public async Task TheBytesCountedForASessionGoWhenTheSessionEnds()
    {
        Build(sessions: Policy(lingerTicks: 1));
        var ann = _harness.NewVisitor();
        Host.Accept(ann.Link.Host);
        ann.Client.Join(Names.Credentials("ann"));
        await _harness.Settle();
        SpawnFor("ann");

        await Publish(1);
        Assert.Contains("ann", Host.Summary().BytesSent.Keys);

        await ann.Link.CloseAsync();
        await _harness.Settle();
        await Publish(2);
        Assert.Null(Host.SessionStateOf("ann"));
        Assert.DoesNotContain("ann", Host.Summary().BytesSent.Keys);
    }

    private Visitor Stranger(InboundBudget budget, List<string> asked)
    {
        Build(
            budget,
            new SessionPolicy
            {
                Admit = bytes =>
                {
                    asked.Add("asked");
                    return Names.Welcome(bytes);
                },
            });
        var stranger = _harness.NewVisitor();
        Host.Accept(stranger.Link.Host);
        return stranger;
    }

    [Fact]
    public async Task AJoinOverBytesPerMessageNeverReachesAdmit()
    {
        var asked = new List<string>();
        var stranger = Stranger(new InboundBudget(bytesPerMessage: 20), asked);
        stranger.Client.Join(new byte[100]);
        await _harness.Settle();

        Assert.Equal([GoodbyeReason.OverBudget], stranger.Reasons);
        Assert.Empty(asked);
        Assert.Equal(1, Host.Rejections[InputRejection.OverBudget]);
    }

    [Fact]
    public async Task AStreamOfJunkIsClosedOnceItPassesTheBudget()
    {
        var stranger = Stranger(new InboundBudget(messagesPerTick: 10), []);
        for (var i = 0; i < 1000; i++)
        {
            stranger.Link.Client.Send(new byte[16]);
        }

        await _harness.Settle();

        Assert.Equal([GoodbyeReason.OverBudget], stranger.Reasons);
        Assert.Equal(1, Host.Rejections[InputRejection.OverBudget]);
    }
}
