namespace Orblit.Net.Tests;

public sealed class SessionTests : IAsyncLifetime
{
    private readonly Harness _harness = new(includeOwner: true, ownerWritable: ["Position"]);
    private readonly List<string> _joined = [];
    private readonly List<(string Key, GoneReason Reason)> _gone = [];
    private readonly List<ulong> _spawned = [];
    private TaskCompletionSource<Admission> _answer = new();
    private int _asked;
    private bool _failJoin;
    private bool _failGone;

    public SessionTests() => _harness.Host = Build();

    private Peer Authority => _harness.Authority;

    private NetHost Host => _harness.Host;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    private NetHost Build(
        bool takeOver = false,
        int joinTicks = 300,
        Func<string, int>? lingerFor = null,
        Func<byte[], ValueTask<Admission>>? admit = null) =>
        new(new NetHostOptions
        {
            World = Authority.World,
            Set = Authority.Set,
            NetworkId = Authority.NetworkId,
            OwnerComponent = Authority.Owner,
            Sessions = new SessionPolicy
            {
                Admit = admit ?? Names.ByName,
                LingerTicks = 3,
                LingerFor = lingerFor,
                JoinTicks = joinTicks,
                TakeOver = takeOver,
                OnJoin = Join,
                OnGone = Gone,
            },
        });

    private void Join(string key)
    {
        if (_failJoin)
        {
            throw new InvalidOperationException("onJoin failed");
        }

        var entity = Authority.World.CreateEntity();
        Authority.World.Add(entity, Authority.Position);
        _spawned.Add(Host.Spawn(entity, key));
        _joined.Add(key);
    }

    private void Gone(string key, GoneReason reason)
    {
        // The entities are still the key's when the game is told.
        Assert.Equal(key, Host.OwnerOf(_spawned[0]));
        _gone.Add((key, reason));
        if (_failGone)
        {
            throw new InvalidOperationException("onGone failed");
        }
    }

    private Visitor Connect(string? name = null)
    {
        var visitor = _harness.NewVisitor();
        Host.Accept(visitor.Link.Host);
        if (name is not null)
        {
            visitor.Client.Join(Names.Credentials(name));
        }

        return visitor;
    }

    private void Rebuild(
        bool takeOver = false,
        int joinTicks = 300,
        Func<string, int>? lingerFor = null,
        Func<byte[], ValueTask<Admission>>? admit = null)
    {
        Host.Dispose();
        _harness.Host = Build(takeOver, joinTicks, lingerFor, admit);
    }

    private Task Settle() => _harness.Settle();

    /// <summary>The client index the host wrote into the entity's owner component.</summary>
    private uint IndexOf(ulong networkId) =>
        Column.UInt(Authority.World, Host.EntityFor(networkId)!.Value, Authority.Owner);

    private async Task Drop(Visitor visitor)
    {
        await visitor.Link.CloseAsync();
        await Settle();
    }

    private async Task Publish(int times)
    {
        for (var i = 0; i < times; i++)
        {
            Host.Publish();
        }

        await Settle();
    }

    [Fact]
    public async Task ARefusedJoinGetsTheReasonAndLeavesNothingBehind()
    {
        var entities = Authority.World.EntityCount;
        var stranger = Connect("banned");
        await Settle();

        Assert.Equal(["banned"], stranger.Reasons);
        Assert.Equal(0, Host.ClientCount);
        Assert.Null(Host.SessionStateOf("banned"));
        Assert.Empty(_joined);
        Assert.Equal(entities, Authority.World.EntityCount);
    }

    [Fact]
    public async Task AnAdmittedJoinStartsALiveSessionOnce()
    {
        Connect("ann");
        await Settle();

        Assert.Equal(SessionState.Live, Host.SessionStateOf("ann"));
        Assert.Equal(1, Host.ClientCount);
        Assert.Equal(["ann"], _joined);
    }

    [Fact]
    public async Task AMessageBeforeTheJoinAppliesNothing()
    {
        var early = Connect();
        await Settle();
        early.Link.Client.Send(InputCodec.Encode(new InputMessage(1, [new InputEntry(1, 1, new byte[12])])));
        await Settle();

        Assert.Equal(0, Host.AcceptedInputs);
        Assert.Empty(Host.Rejections);
        Assert.Equal(0, Host.ClientCount);
    }

    [Fact]
    public async Task AnAdmissionThatThrowsRefusesTheJoin()
    {
        Rebuild(admit: _ => throw new InvalidOperationException("garbled"));
        var stranger = Connect("anyone");
        await Settle();

        Assert.Equal([GoodbyeReason.Refused], stranger.Reasons);
        Assert.Equal(0, Host.ClientCount);
    }

    [Fact]
    public async Task AnAdmissionThatThrowsAnotherTypeRefusesTheJoin()
    {
        Rebuild(admit: _ => throw new FormatException("garbled"));
        var stranger = Connect("anyone");
        await Settle();

        Assert.Equal([GoodbyeReason.Refused], stranger.Reasons);
        Assert.Equal(0, Host.ClientCount);
        Assert.Empty(_joined);
    }

    private Visitor Waiting(int joinTicks = 300)
    {
        _answer = new TaskCompletionSource<Admission>();
        _asked = 0;
        Rebuild(
            joinTicks: joinTicks,
            admit: _ =>
            {
                _asked++;
                return new ValueTask<Admission>(_answer.Task);
            });
        return Connect("ann");
    }

    [Fact]
    public async Task AnAnswerThatComesLaterSeatsTheConnectionWhenItAdmits()
    {
        Waiting();
        await Settle();
        Assert.Equal(0, Host.ClientCount);

        _answer.SetResult(new Admit("ann"));
        await Settle();

        Assert.Equal(SessionState.Live, Host.SessionStateOf("ann"));
        Assert.Equal(["ann"], _joined);
    }

    [Fact]
    public async Task AnAnswerThatComesLaterTurnsTheConnectionAwayWhenItRefuses()
    {
        var stranger = Waiting();
        await Settle();
        _answer.SetResult(new Refuse("no"));
        await Settle();

        Assert.Equal(["no"], stranger.Reasons);
        Assert.Equal(0, Host.ClientCount);
        Assert.Empty(_joined);
    }

    [Fact]
    public async Task AnAnswerThatFailsLaterRefusesTheJoin()
    {
        var stranger = Waiting();
        await Settle();
        _answer.SetException(new InvalidOperationException("database down"));
        await Settle();

        Assert.Equal([GoodbyeReason.Refused], stranger.Reasons);
        Assert.Equal(0, Host.ClientCount);
    }

    [Fact]
    public async Task TheHostKeepsPublishingWhileAnAnswerIsAwaited()
    {
        Waiting();
        await Publish(3);

        Assert.Equal(3L, Host.Tick);
    }

    [Fact]
    public async Task TheHostAsksOnceHoweverManyJoinsArriveMeanwhile()
    {
        var ann = Waiting();
        ann.Client.Join(Names.Credentials("ann"));
        ann.Client.Join(Names.Credentials("ann"));
        await Settle();

        Assert.Equal(1, _asked);
    }

    [Fact]
    public async Task TheVerdictOfAConnectionThatHungUpIsDropped()
    {
        var ann = Waiting();
        await Settle();
        await Drop(ann);
        _answer.SetResult(new Admit("ann"));
        await Settle();

        Assert.Null(Host.SessionStateOf("ann"));
        Assert.Empty(_joined);
    }

    [Fact]
    public async Task TheVerdictThatArrivesAfterJoinTicksIsDropped()
    {
        var ann = Waiting(joinTicks: 2);
        await Publish(2);
        _answer.SetResult(new Admit("ann"));
        await Settle();

        Assert.Equal([GoodbyeReason.JoinTimeout], ann.Reasons);
        Assert.Null(Host.SessionStateOf("ann"));
        Assert.Empty(_joined);
    }

    [Fact]
    public async Task TheVerdictThatArrivesAfterDisposeIsDropped()
    {
        Waiting();
        await Settle();
        Host.Dispose();
        _answer.SetResult(new Admit("ann"));
        await Settle();

        Assert.Equal(0, Host.ClientCount);
        Assert.Empty(_joined);
    }

    [Fact]
    public async Task AConnectionThatNeverJoinsIsClosedAfterJoinTicks()
    {
        Rebuild(joinTicks: 2);
        var silent = new Probe();
        Host.Accept(silent);

        await Publish(1);
        Assert.Empty(silent.Goodbyes);
        Assert.False(silent.Closed);

        await Publish(1);
        Assert.Equal([GoodbyeReason.JoinTimeout], silent.Goodbyes);
        Assert.True(silent.Closed);
    }

    [Fact]
    public async Task AConnectionThatDropsBeforeJoiningLeavesNothing()
    {
        Rebuild(joinTicks: 2);
        var early = Connect();
        await Settle();
        await Drop(early);
        await Publish(5);

        Assert.Empty(early.Reasons);
        Assert.Equal(0, Host.ClientCount);
    }

    [Fact]
    public void TheHostClosesATransportWhosePeerHungUp()
    {
        var quitter = new Probe();
        Host.Accept(quitter);
        quitter.HangUp();
        Assert.True(quitter.Closed);

        var ann = new Probe();
        Host.Accept(ann);
        ann.Hear(SessionMessage.EncodeJoin(Names.Credentials("ann")));
        ann.HangUp();
        Assert.True(ann.Closed);
        Assert.Equal(SessionState.Lingering, Host.SessionStateOf("ann"));
    }

    [Fact]
    public async Task AJoinThatArrivesAfterTheDeadlineIsNotAdmitted()
    {
        Rebuild(joinTicks: 1);
        var late = Connect();
        await Settle();
        await Publish(1);
        late.Client.Join(Names.Credentials("ann"));
        await Settle();

        Assert.Null(Host.SessionStateOf("ann"));
        Assert.Empty(_joined);
    }

    [Fact]
    public void AHostWithNoPolicyCannotAccept()
    {
        using var plain = new NetHost(new NetHostOptions
        {
            World = Authority.World,
            Set = Authority.Set,
            NetworkId = Authority.NetworkId,
        });
        var link = new LoopbackLink();

        Assert.Throws<InvalidOperationException>(() => plain.Accept(link.Host));
    }

    [Fact]
    public async Task ADropKeepsTheSessionAndItsEntity()
    {
        var ann = Connect("ann");
        await Settle();
        await Drop(ann);

        Assert.Equal(SessionState.Lingering, Host.SessionStateOf("ann"));
        Assert.Equal(0, Host.ClientCount);
        Assert.Equal("ann", Host.OwnerOf(_spawned[0]));
        Assert.NotNull(Host.EntityFor(_spawned[0]));
    }

    [Fact]
    public async Task AJoinWithTheSameKeyResumesTheSameEntity()
    {
        var first = Connect("ann");
        await Settle();
        var before = IndexOf(_spawned[0]);
        await Drop(first);
        await Publish(1);

        var second = Connect("ann");
        await Settle();
        await Publish(1);

        Assert.Equal(SessionState.Live, Host.SessionStateOf("ann"));
        Assert.Equal(1, Host.ClientCount);
        Assert.Equal(["ann"], _joined);
        Assert.Empty(_gone);
        Assert.Equal("ann", Host.OwnerOf(_spawned[0]));
        Assert.Equal(before, IndexOf(_spawned[0]));
        Assert.NotNull(second.Client.EntityFor(_spawned[0]));
    }

    [Fact]
    public async Task AResumedClientIsToldTheWholeWorldNotADelta()
    {
        var first = Connect("ann");
        await Settle();
        await Publish(2);
        await Drop(first);
        await Publish(2);

        var second = Connect("ann");
        await Settle();
        await Publish(1);

        Assert.Equal(1, second.Client.EntityCount);
    }

    [Fact]
    public async Task SetOwnerForALingeringKeyWritesItsIndex()
    {
        var ann = Connect("ann");
        await Settle();
        var index = IndexOf(_spawned[0]);
        await Drop(ann);

        var entity = Authority.World.CreateEntity();
        Authority.World.Add(entity, Authority.Position);
        var extra = Host.Spawn(entity);
        Host.SetOwner(extra, "ann");

        Assert.Equal(index, IndexOf(extra));
    }

    [Fact]
    public async Task AnotherKeyGetsItsOwnIndex()
    {
        Connect("ann");
        Connect("bob");
        await Settle();

        Assert.Equal(2, _spawned.Count);
        Assert.NotEqual(IndexOf(_spawned[0]), IndexOf(_spawned[1]));
    }

    [Fact]
    public async Task ASecondJoinIsRefusedAndTheFirstCarriesOn()
    {
        var first = Connect("ann");
        await Settle();
        var second = Connect("ann");
        await Settle();
        await Publish(1);

        Assert.Equal(["alreadyLive"], second.Reasons);
        Assert.Empty(first.Reasons);
        Assert.Equal(1, Host.ClientCount);
        Assert.Equal(["ann"], _joined);
        Assert.Equal(1, first.Client.EntityCount);
    }

    [Fact]
    public async Task AGameAddedClientCannotBeTakenOver()
    {
        Rebuild(takeOver: true);
        var owned = _harness.NewVisitor();
        Host.AddClient("ann", owned.Link.Host);

        var second = Connect("ann");
        await Settle();

        Assert.Equal([GoodbyeReason.AlreadyLive], second.Reasons);
        Assert.Empty(owned.Reasons);
        Assert.Equal(1, Host.ClientCount);
        Assert.Null(Host.SessionStateOf("ann"));
    }

    [Fact]
    public async Task AddClientRefusesAKeyThatHasASession()
    {
        var ann = Connect("ann");
        await Settle();
        await Drop(ann);
        var link = new LoopbackLink();

        Assert.Throws<InvalidOperationException>(() => Host.AddClient("ann", link.Host));
    }

    [Fact]
    public async Task TakeOverMovesTheSessionToTheNewConnection()
    {
        Rebuild(takeOver: true);
        var first = Connect("ann");
        await Settle();
        var second = Connect("ann");
        await Settle();
        await Publish(1);

        Assert.Equal(["replaced"], first.Reasons);
        Assert.Empty(second.Reasons);
        Assert.Equal(SessionState.Live, Host.SessionStateOf("ann"));
        Assert.Equal(1, Host.ClientCount);
        Assert.Equal(["ann"], _joined);
        Assert.Empty(_gone);
        Assert.Equal(1, second.Client.EntityCount);
    }

    [Fact]
    public async Task ASessionThatLingersLongEnoughEndsOnce()
    {
        var ann = Connect("ann");
        await Settle();
        await Drop(ann);

        await Publish(2);
        Assert.Equal(SessionState.Lingering, Host.SessionStateOf("ann"));
        Assert.Empty(_gone);

        await Publish(1);
        Assert.Equal([("ann", GoneReason.Disconnect)], _gone);
        Assert.Null(Host.SessionStateOf("ann"));
        Assert.Null(Host.OwnerOf(_spawned[0]));

        await Publish(5);
        Assert.Single(_gone);
    }

    [Fact]
    public async Task LingerForSetsTheWaitAndIsAskedForTheKey()
    {
        var asked = new List<string>();
        Rebuild(lingerFor: key =>
        {
            asked.Add(key);
            return 6;
        });
        var ann = Connect("ann");
        await Settle();
        await Drop(ann);

        await Publish(5);
        Assert.Empty(_gone);
        await Publish(1);
        Assert.Equal([("ann", GoneReason.Disconnect)], _gone);
        Assert.Equal(["ann"], asked);
    }

    [Fact]
    public async Task ANegativeLingerForEndsTheSessionAtOnce()
    {
        Rebuild(lingerFor: _ => -5);
        var ann = Connect("ann");
        await Settle();
        await Drop(ann);
        await Publish(1);

        Assert.Equal([("ann", GoneReason.Disconnect)], _gone);
    }

    [Fact]
    public async Task ALingerForThatThrowsGivesTheDefaultWait()
    {
        Rebuild(lingerFor: _ => throw new InvalidOperationException("no rule"));
        var ann = Connect("ann");
        await Settle();
        await Drop(ann);

        await Publish(2);
        Assert.Empty(_gone);
        await Publish(1);
        Assert.Equal([("ann", GoneReason.Disconnect)], _gone);
    }

    [Fact]
    public async Task AResumeInTimeMeansItNeverEnds()
    {
        var first = Connect("ann");
        await Settle();
        await Drop(first);
        await Publish(2);

        Connect("ann");
        await Settle();
        await Publish(10);

        Assert.Empty(_gone);
        Assert.Equal(SessionState.Live, Host.SessionStateOf("ann"));
    }

    [Fact]
    public async Task RemoveClientEndsALiveSessionAsRemoved()
    {
        Connect("ann");
        await Settle();
        Host.RemoveClient("ann");

        Assert.Equal([("ann", GoneReason.Removed)], _gone);
        Assert.Null(Host.SessionStateOf("ann"));
        Assert.Null(Host.OwnerOf(_spawned[0]));
    }

    [Fact]
    public void RemoveClientTellsALiveClientAndClosesIt()
    {
        var ann = new Probe();
        Host.Accept(ann);
        ann.Hear(SessionMessage.EncodeJoin(Names.Credentials("ann")));
        Host.RemoveClient("ann");

        Assert.Equal([GoodbyeReason.Removed], ann.Goodbyes);
        Assert.True(ann.Closed);
    }

    [Fact]
    public async Task AThrowInOnJoinLeavesASessionThatADropCanLinger()
    {
        _failJoin = true;
        var ann = Connect("ann");

        // The throw is the game's bug, so it surfaces rather than being swallowed. The host stays
        // usable.
        await Assert.ThrowsAsync<InvalidOperationException>(Settle);
        await Drop(ann);

        Assert.Equal(SessionState.Lingering, Host.SessionStateOf("ann"));
        Assert.Equal(0, Host.ClientCount);
    }

    [Fact]
    public async Task AThrowInOnGoneStillEndsTheSession()
    {
        _failGone = true;
        var ann = Connect("ann");
        await Settle();
        await Drop(ann);
        await Publish(2);

        Assert.Throws<InvalidOperationException>(() => Host.Publish());
        Assert.Equal([("ann", GoneReason.Disconnect)], _gone);
        Assert.Null(Host.SessionStateOf("ann"));
        Assert.Null(Host.OwnerOf(_spawned[0]));
        Host.Publish();
        Assert.Single(_gone);
    }

    [Fact]
    public async Task RemoveClientEndsALingeringSessionOnce()
    {
        var ann = Connect("ann");
        await Settle();
        await Drop(ann);
        Host.RemoveClient("ann");
        await Publish(10);

        Assert.Equal([("ann", GoneReason.Removed)], _gone);
    }

    [Fact]
    public void NegativeLingerTicksAreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionPolicy { Admit = Names.ByName, LingerTicks = -1 });
    }

    [Fact]
    public void JoinTicksUnderOneAreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionPolicy { Admit = Names.ByName, JoinTicks = 0 });
    }
}
