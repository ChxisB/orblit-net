using Orblit.Game;
using Orblit.Scene;
using Orblit.VectorMath;
using static Orblit.Net.Game.Tests.Scenes;

namespace Orblit.Net.Game.Tests;

/// <summary>
/// A call crosses a loopback link between two real game hosts. The tests send it from one end,
/// settle the wire, run the frames and read what the other end's handler saw.
/// </summary>
public sealed class NetCallTests
{
    private static SceneDocument Crate() => World(Tagged("crate", 0, 0, 0));

    [Fact]
    public async Task AClientCallReachesTheServerWithTheSameArguments()
    {
        await using var pair = new Pair(Crate());
        var client = await pair.Join("p1");
        var seen = new List<(string? From, object[] Args)>();
        pair.Server.On("shoot", (caller, args) => seen.Add((caller.ClientId, [.. args])));

        pair.Client("p1").Call("shoot", 3, "ab", true, 1.5f, new Vector3(1, 2, 3), new NetEntity("crate"));
        await pair.Settle();
        await pair.Step(client);

        var call = Assert.Single(seen);
        Assert.Equal("p1", call.From);
        Assert.Equal([3, "ab", true, 1.5f, new Vector3(1, 2, 3), new NetEntity("crate")], call.Args);
    }

    [Fact]
    public async Task ACallRunsOnTheGameThreadBeforeTheGamesUpdate()
    {
        await using var pair = new Pair(Crate());
        var client = await pair.Join("p1");
        var handlerThread = 0;
        var gameThread = 0;
        var calls = 0;
        var seenByTheGame = -1;
        pair.Server.On("shoot", (_, _) =>
        {
            calls++;
            handlerThread = Environment.CurrentManagedThreadId;
        });
        pair.ServerGame.OnUpdate = _ =>
        {
            seenByTheGame = calls;
            gameThread = Environment.CurrentManagedThreadId;
        };

        pair.Client("p1").Call("shoot");
        await pair.Settle();
        Assert.Equal(0, calls);
        await pair.Step(client);

        Assert.Equal(1, seenByTheGame);
        Assert.Equal(gameThread, handlerThread);
    }

    [Fact]
    public async Task CallsFromOneClientRunInTheOrderTheyWereMade()
    {
        await using var pair = new Pair(Crate());
        var client = await pair.Join("p1");
        var seen = new List<int>();
        pair.Server.On("n", (_, args) => seen.Add((int)args[0]));

        for (var number = 1; number <= 5; number++)
        {
            pair.Client("p1").Call("n", number);
        }

        await pair.Settle();
        await pair.Step(client);
        await pair.Step(client);

        Assert.Equal([1, 2, 3, 4, 5], seen);
    }

    [Fact]
    public async Task EachCallCarriesTheClientThatMadeIt()
    {
        await using var pair = new Pair(Crate());
        var one = await pair.Join("p1");
        var two = await pair.Join("p2");
        var seen = new List<string?>();
        pair.Server.On("who", (caller, _) => seen.Add(caller.ClientId));

        pair.Client("p2").Call("who");
        pair.Client("p1").Call("who");
        await pair.Settle();
        await pair.Step(one, two);

        Assert.Equal(["p1", "p2"], seen.Order());
    }

    [Fact]
    public async Task AServerCallToOneClientReachesOnlyThatClient()
    {
        await using var pair = new Pair(Crate());
        var one = await pair.Join("p1");
        var two = await pair.Join("p2");
        var first = new List<int>();
        var second = new List<int>();
        pair.Client("p1").On("hit", (caller, args) => first.Add((int)args[0]));
        pair.Client("p2").On("hit", (caller, args) => second.Add((int)args[0]));

        pair.Server.CallClient("p2", "hit", 5);
        await pair.Settle();
        await pair.Step(one, two);

        Assert.Empty(first);
        Assert.Equal([5], second);
    }

    [Fact]
    public async Task AServerCallToAllReachesEveryClient()
    {
        await using var pair = new Pair(Crate());
        var one = await pair.Join("p1");
        var two = await pair.Join("p2");
        var seen = new List<string>();
        pair.Client("p1").On("hit", (_, args) => seen.Add("p1 " + args[0]));
        pair.Client("p2").On("hit", (caller, args) =>
        {
            Assert.Null(caller.ClientId);
            seen.Add("p2 " + args[0]);
        });

        pair.Server.CallAll("hit", "hello");
        await pair.Settle();
        await pair.Step(one, two);

        Assert.Equal(["p1 hello", "p2 hello"], seen.Order());
    }

    [Fact]
    public async Task AClientCannotCallAnotherClientOrEveryClient()
    {
        await using var pair = new Pair(Crate());
        await pair.Join("p1");

        Assert.Throws<InvalidOperationException>(() => pair.Client("p1").CallClient("p2", "hit"));
        Assert.Throws<InvalidOperationException>(() => pair.Client("p1").CallAll("hit"));
    }

    [Fact]
    public async Task AServerCallToItselfRunsAtOnce()
    {
        await using var pair = new Pair(Crate());
        var seen = new List<(string? From, int Number)>();
        pair.Server.On("local", (caller, args) => seen.Add((caller.ClientId, (int)args[0])));

        pair.Server.Call("local", 9);

        Assert.Equal([(null, 9)], seen);
    }

    [Fact]
    public async Task AHandlerHeldTwiceUnderANameIsReplaced()
    {
        await using var pair = new Pair(Crate());
        var seen = new List<string>();
        pair.Server.On("local", (_, _) => seen.Add("first"));
        pair.Server.On("local", (_, _) => seen.Add("second"));

        pair.Server.Call("local");

        Assert.Equal(["second"], seen);
    }

    [Fact]
    public async Task ACallNobodyHandlesIsCountedAndDropped()
    {
        await using var pair = new Pair(Crate());
        var client = await pair.Join("p1");

        pair.Client("p1").Call("nothing");
        pair.Server.CallAll("nothing");
        await pair.Settle();
        await pair.Step(client);

        Assert.Equal(1, pair.Server.UnhandledCalls);
        Assert.Equal(1, pair.Client("p1").UnhandledCalls);
        Assert.Equal(0, pair.Server.Malformed);
        Assert.Equal(0, pair.Client("p1").Malformed);
    }

    [Fact]
    public async Task ACallNamingAnEntityTheSceneLacksIsDroppedAndOneNamingItRuns()
    {
        await using var pair = new Pair(Crate());
        var client = await pair.Join("p1");
        var seen = new List<string>();
        pair.Server.On("pick", (_, args) => seen.Add(((NetEntity)args[0]).Id));

        pair.Client("p1").Call("pick", new NetEntity("ghost"));
        pair.Client("p1").Call("pick", new NetEntity("crate"));
        await pair.Settle();
        await pair.Step(client);

        Assert.Equal(["crate"], seen);
        Assert.Equal(1, pair.Server.Malformed);
    }

    [Fact]
    public async Task ACallTheServerMakesToItselfDropsAnEntityTheSceneLacks()
    {
        await using var pair = new Pair(Crate());
        var seen = new List<string>();
        pair.Server.On("pick", (_, args) => seen.Add(((NetEntity)args[0]).Id));

        pair.Server.Call("pick", new NetEntity("ghost"));
        pair.Server.Call("pick", new NetEntity("crate"));

        Assert.Equal(["crate"], seen);
        Assert.Equal(1, pair.Server.Malformed);
    }

    [Fact]
    public async Task AServerCallNamingAnEntityTheClientLacksIsDroppedThereAndOneItHasRuns()
    {
        await using var pair = new Pair(Crate());
        var client = await pair.Join("p1");
        var seen = new List<string>();
        pair.Client("p1").On("pick", (_, args) => seen.Add(((NetEntity)args[0]).Id));

        pair.Server.CallAll("pick", new NetEntity("ghost"));
        pair.Server.CallAll("pick", new NetEntity("crate"));
        await pair.Settle();
        await pair.Step(client);

        Assert.Equal(["crate"], seen);
        Assert.Equal(1, pair.Client("p1").Malformed);
    }

    [Fact]
    public async Task ACallCanNameAnEntityThatTheSameTickSpawned()
    {
        await using var pair = new Pair(World());
        var client = await pair.Join("p1");
        var seen = new List<string>();
        pair.Client("p1").On("hit", (_, args) => seen.Add(((NetEntity)args[0]).Id));
        pair.ServerGame.Once(context =>
        {
            context.Scene.Apply(Add(Tagged("bullet", 0, 0, 0)));
            pair.Server.CallAll("hit", new NetEntity("bullet"));
        });

        await pair.Step(client);

        Assert.Equal(["bullet"], seen);
        Assert.Equal(0, pair.Client("p1").Malformed);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 3 })]
    [InlineData(new byte[] { 3, 1, (byte)'a', 1, 99, 0 })]
    [InlineData(new byte[] { 9, 9, 9 })]
    public async Task ABodyTheServerCannotReadIsCountedAndTheServerGoesOn(byte[] body)
    {
        await using var pair = new Pair(Crate());
        var client = await pair.Join("p1");
        var seen = 0;
        pair.Server.On("a", (_, _) => seen++);

        pair.Client("p1").ForgeMessage(body);
        await pair.Settle();
        await pair.Step(client);
        pair.Client("p1").Call("a");
        await pair.Settle();
        await pair.Step(client);

        Assert.Equal(1, pair.Server.Malformed);
        Assert.Equal(1, seen);
    }

    [Fact]
    public async Task AClientCannotSpawnOrHandOutOwnershipWithTheServersMessages()
    {
        await using var pair = new Pair(Crate());
        var client = await pair.Join("p1");

        pair.Client("p1").ForgeMessage(NetWire.Spawn(99, Tagged("fake", 0, 0, 0)));
        pair.Client("p1").ForgeMessage(NetWire.Owner(1, "p1"));
        await pair.Settle();
        await pair.Step(client);

        Assert.Equal(2, pair.Server.Malformed);
        Assert.Null(pair.Scene["fake"]);
        Assert.Null(pair.Server.OwnerOf("crate"));
    }

    [Fact]
    public async Task ABodyTheClientCannotReadIsCountedAndTheClientGoesOn()
    {
        await using var pair = new Pair(Crate());
        var client = await pair.Join("p1");

        pair.Server.Host.SendMessage("p1", [3, 0]);
        await pair.Settle();
        await pair.Step(client);

        Assert.Equal(1, pair.Client("p1").Malformed);
    }

    [Fact]
    public async Task ACallOverTheBudgetIsDropped()
    {
        await using var pair = new Pair(Crate());
        var client = await pair.Join("p1");
        var seen = 0;
        pair.Server.On("flood", (_, _) => seen++);

        for (var number = 0; number < 150; number++)
        {
            pair.Client("p1").Call("flood", number);
        }

        await pair.Settle();
        await pair.Step(client);

        Assert.InRange(seen, 1, 100);
        Assert.True(pair.Server.Host.Rejections.GetValueOrDefault(InputRejection.OverBudget) > 0);
    }

    [Fact]
    public async Task ACallWithAnArgumentItCannotCarryThrowsWhereItIsMadeAndSendsNothing()
    {
        await using var pair = new Pair(Crate());
        var client = await pair.Join("p1");
        IGameNet[] nets = [pair.Server, pair.Client("p1"), OfflineNet.Instance];

        foreach (var net in nets)
        {
            Assert.Throws<ArgumentException>(() => net.Call("x", 1.5));
        }

        await pair.Settle();
        await pair.Step(client);
        Assert.Equal(0, pair.Server.Malformed + pair.Server.UnhandledCalls);
    }

    [Fact]
    public async Task ANameThatIsEmptyOrAHandlerThatIsMissingIsRefused()
    {
        await using var pair = new Pair(Crate());

        Assert.Throws<ArgumentException>(() => pair.Server.On(" ", (_, _) => { }));
        Assert.Throws<ArgumentNullException>(() => pair.Server.On("x", null!));
        Assert.Throws<ArgumentException>(() => pair.Server.Call(string.Empty));
        Assert.Throws<ArgumentException>(() => pair.Server.CallClient(" ", "x"));
    }

    [Fact]
    public void AnOfflineGameRunsItsOwnCallsAtOnce()
    {
        var seen = new List<(string? From, int Number)>();
        OfflineNet.Instance.On("offline.ping", (caller, args) => seen.Add((caller.ClientId, (int)args[0])));

        OfflineNet.Instance.Call("offline.ping", 1);
        OfflineNet.Instance.CallAll("offline.ping", 2);
        OfflineNet.Instance.CallClient("p1", "offline.ping", 3);

        Assert.Equal([(null, 1), (null, 2), (null, 3)], seen);
    }
}
