using Orblit.Core;

namespace Orblit.Net.Tests;

public sealed class InterestTests : IAsyncLifetime
{
    private readonly Harness _harness = new();
    private readonly List<Player> _players = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    private NetHost Host => _harness.Host;

    private Peer Authority => _harness.Authority;

    /// <summary>A link that can lose everything the host sends it.</summary>
    private sealed class Lossy(ITransport inner) : ITransport
    {
        public bool Dropping { get; set; }

        public IDisposable Listen(Action<byte[]> onMessage, Action? onDone = null, Action<Exception>? onError = null) =>
            inner.Listen(onMessage, onDone, onError);

        public void Send(byte[] message)
        {
            if (!Dropping)
            {
                inner.Send(message);
            }
        }

        public Task CloseAsync() => inner.CloseAsync();
    }

    private sealed class Nothing : IInterestRule
    {
        public void Observe(WorldSnapshot world, IReadOnlyDictionary<string, IReadOnlyList<ulong>> focus)
        {
        }

        public bool IsRelevant(string clientId, ulong networkId) => false;
    }

    private sealed class Counting : IInterestRule
    {
        public int Questions { get; private set; }

        public void Observe(WorldSnapshot world, IReadOnlyDictionary<string, IReadOnlyList<ulong>> focus)
        {
        }

        public bool IsRelevant(string clientId, ulong networkId)
        {
            Questions++;
            return true;
        }
    }

    private sealed class Player(Visitor visitor)
    {
        public Lossy Lossy { get; } = new(visitor.Link.Host);

        public List<ulong> Despawned => visitor.Despawned;

        public NetClient Client => visitor.Client;

        public bool Sees(ulong networkId) => Client.EntityFor(networkId) is not null;

        public float XOf(ulong networkId) =>
            Column.Floats(visitor.Peer.World, Client.EntityFor(networkId)!.Value, visitor.Peer.Position)[0];
    }

    private void Build(IInterestRule rule, int historyDepth = 64) =>
        _harness.Host = new NetHost(new NetHostOptions
        {
            World = Authority.World,
            Set = Authority.Set,
            NetworkId = Authority.NetworkId,
            Interest = rule,
            HistoryDepth = historyDepth,
        });

    private GridInterest Grid(int ring = 1) => new(Authority.Set, Authority.Position, new Grid(10), ring);

    private void Place(ulong networkId, float x, float z = 0)
    {
        var entity = Host.EntityFor(networkId)!.Value;
        Column.SetFloat(Authority.World, entity, Authority.Position, 0, x);
        Column.SetFloat(Authority.World, entity, Authority.Position, 2, z);
    }

    private ulong SpawnAt(float x, string? owner = null, float z = 0)
    {
        var entity = Authority.World.CreateEntity();
        Authority.World.Add<float>(entity, Authority.Position, [0, 0, 0]);
        var networkId = Host.Spawn(entity, owner);
        Place(networkId, x, z);
        return networkId;
    }

    private Player Join(string key)
    {
        var player = new Player(_harness.NewVisitor());
        _players.Add(player);
        Host.AddClient(key, player.Lossy);
        return player;
    }

    private async Task Tick()
    {
        Host.Publish();
        await _harness.Settle();
    }

    [Fact]
    public async Task AnEntityLeavesOneClientAndEntersAnotherAsItCrossesCells()
    {
        Build(Grid());
        var a = Join("a");
        var b = Join("b");
        SpawnAt(1, "a");
        SpawnAt(55, "b");
        var walker = SpawnAt(2);

        await Tick();
        Assert.True(a.Sees(walker));
        Assert.False(b.Sees(walker));

        Place(walker, 52);
        await Tick();

        Assert.False(a.Sees(walker));
        Assert.Equal([walker], a.Despawned);
        Assert.True(b.Sees(walker));
        Assert.Equal(52f, b.XOf(walker));
    }

    [Fact]
    public async Task AnEntityComingIntoViewIsSentWholeNotAsADelta()
    {
        Build(Grid());
        var a = Join("a");
        var avatar = SpawnAt(1, "a");
        var statue = SpawnAt(55);

        await Tick();
        Assert.False(a.Sees(statue));

        Place(avatar, 52);
        await Tick();

        Assert.True(a.Sees(statue));
        Assert.Equal(55f, a.XOf(statue));
    }

    [Fact]
    public async Task ARingOfTwoSeesTwoCellsOut()
    {
        Build(Grid(ring: 2));
        var a = Join("a");
        SpawnAt(5, "a");
        var near = SpawnAt(25);
        var far = SpawnAt(35);

        await Tick();

        Assert.True(a.Sees(near));
        Assert.False(a.Sees(far));
    }

    [Fact]
    public async Task CellsAreCountedAcrossZeroAndOnTheOtherAxis()
    {
        Build(Grid());
        var a = Join("a");
        SpawnAt(1, "a", z: 1);
        var left = SpawnAt(-1, z: -1);
        var beyond = SpawnAt(-11, z: 1);
        var north = SpawnAt(1, z: 19);
        var further = SpawnAt(1, z: 21);

        await Tick();

        Assert.True(a.Sees(left));
        Assert.False(a.Sees(beyond));
        Assert.True(a.Sees(north));
        Assert.False(a.Sees(further));
    }

    [Fact]
    public async Task AnEntityWithNoPositionIsSeenByNobody()
    {
        Build(Grid());
        var a = Join("a");
        SpawnAt(1, "a");
        var nowhere = SpawnAt(float.NaN);

        await Tick();

        Assert.False(a.Sees(nowhere));
    }

    [Fact]
    public async Task AClientWithTwoFarApartEntitiesSeesAroundBoth()
    {
        Build(Grid());
        var a = Join("a");
        SpawnAt(1, "a");
        SpawnAt(101, "a");
        var first = SpawnAt(3);
        var second = SpawnAt(103);
        var between = SpawnAt(51);

        await Tick();

        Assert.True(a.Sees(first));
        Assert.True(a.Sees(second));
        Assert.False(a.Sees(between));
    }

    [Fact]
    public async Task FocusCellsListTheCellsClientsStandInOnceEach()
    {
        var rule = Grid();
        Build(rule);
        Join("a");
        Join("b");
        Join("c");
        SpawnAt(1, "a");
        SpawnAt(2, "b");
        SpawnAt(55, "c");

        await Tick();

        var cells = rule.FocusCells.ToList();
        Assert.Equal(2, cells.Count);
        Assert.Equivalent(new[] { new Cell(0, 0), new Cell(5, 0) }, cells, strict: false);
    }

    [Fact]
    public void ARuleThatCannotReadPositionsIsRefused()
    {
        using var other = new Peer(reverseRegistrationOrder: false);
        var unreplicated = other.World.RegisterComponent("Stray", ComponentKind.Float32, 3);
        var flat = other.World.RegisterComponent("Flat", ComponentKind.Int32);

        Assert.ThrowsAny<ArgumentException>(() => new GridInterest(other.Set, unreplicated, new Grid(10)));
        Assert.ThrowsAny<ArgumentException>(() => new GridInterest(new ReplicationSet([flat]), flat, new Grid(10)));
        Assert.ThrowsAny<ArgumentException>(() => new GridInterest(other.Set, other.Position, new Grid(10), ring: -1));
    }

    [Fact]
    public void AGridNeedsACellSizeAboveZero()
    {
        foreach (var size in new[] { 0, -1, double.NaN, double.PositiveInfinity })
        {
            Assert.ThrowsAny<ArgumentException>(() => new Grid(size));
        }
    }

    [Fact]
    public async Task AClientAlwaysSeesWhatItOwnsWhateverTheRuleSays()
    {
        Build(new Nothing());
        var a = Join("a");
        var mine = SpawnAt(1, "a");
        var stranger = SpawnAt(2);

        await Tick();

        Assert.True(a.Sees(mine));
        Assert.False(a.Sees(stranger));
    }

    [Fact]
    public async Task TheRuleIsAskedOncePerClientPerEntityNotOnceForEachThatItOwns()
    {
        var rule = new Counting();
        Build(rule);
        Join("a");
        Join("b");
        SpawnAt(1, "a");
        SpawnAt(2, "b");
        SpawnAt(3);
        SpawnAt(4);
        SpawnAt(5);

        await Tick();

        // Each client skips its own entity, so five entities ask four questions each.
        Assert.Equal(2 * 4, rule.Questions);
    }

    [Fact]
    public async Task ADeltaThatNeverArrivedIsRepairedByTheNextOne()
    {
        Build(Grid());
        var a = Join("a");
        SpawnAt(1, "a");
        var walker = SpawnAt(55);
        await Tick();
        Assert.False(a.Sees(walker));

        a.Lossy.Dropping = true;
        Place(walker, 4);
        await Tick();
        Assert.False(a.Sees(walker));

        a.Lossy.Dropping = false;
        await Tick();
        Assert.True(a.Sees(walker));
        Assert.Equal(4f, a.XOf(walker));

        a.Lossy.Dropping = true;
        Place(walker, 85);
        await Tick();
        Assert.True(a.Sees(walker));

        a.Lossy.Dropping = false;
        await Tick();
        Assert.False(a.Sees(walker));
        Assert.Equal([walker], a.Despawned);
    }

    [Fact]
    public async Task AClientsFirstMessageIsAFilteredFullSnapshot()
    {
        Build(Grid());
        SpawnAt(1, "a");
        SpawnAt(55, "b");
        var nearA = SpawnAt(3);
        var nearB = SpawnAt(57);
        var a = Join("a");
        await Tick();
        await Tick();

        var b = Join("b");
        await Tick();

        Assert.True(b.Sees(nearB));
        Assert.False(b.Sees(nearA));
        Assert.True(a.Sees(nearA));
        Assert.Equal(2, b.Client.EntityCount);
    }

    [Fact]
    public async Task AClientFarBehindStillGetsOnlyWhatIsNearIt()
    {
        Build(Grid(), historyDepth: 3);
        var a = Join("a");
        SpawnAt(1, "a");
        var near = SpawnAt(3);
        var far = SpawnAt(55);
        await Tick();

        a.Lossy.Dropping = true;
        for (var i = 0; i < 6; i++)
        {
            await Tick();
        }

        a.Lossy.Dropping = false;
        Place(near, 4);
        await Tick();

        Assert.Equal(4f, a.XOf(near));
        Assert.False(a.Sees(far));
    }

    [Fact]
    public async Task ADyingEntityIsRemovedFromEveryoneWhoSawIt()
    {
        Build(Grid());
        var a = Join("a");
        SpawnAt(1, "a");
        var mortal = SpawnAt(3);
        await Tick();
        Assert.True(a.Sees(mortal));

        Host.Despawn(Host.EntityFor(mortal)!.Value);
        await Tick();

        Assert.False(a.Sees(mortal));
        Assert.Equal([mortal], a.Despawned);
    }
}
