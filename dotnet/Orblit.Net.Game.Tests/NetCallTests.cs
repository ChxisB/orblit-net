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
        IGameNet[] nets = [pair.Server, pair.Client("p1"), new OfflineNet()];

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
    public async Task AHandlerThatThrowsOnAClientsArgumentsDoesNotEndTheServerOrLoseTheRestOfTheBatch()
    {
        await using var pair = new Pair(Crate());
        var client = await pair.Join("p1");
        var seen = new List<int>();
        pair.Server.On("shoot", (_, args) =>
        {
            var aim = (int)args[0];
            seen.Add(aim >= 0 ? aim : throw new InvalidOperationException("bad aim"));
        });

        foreach (var aim in new[] { 1, -1, 2 })
        {
            pair.Client("p1").Call("shoot", aim);
        }

        await pair.Settle();
        await pair.Step(client);

        Assert.Equal([1, 2], seen);
        Assert.True(pair.ServerHost.Running);
        Assert.Null(pair.ServerHost.Fault);
        Assert.Equal(1, pair.Server.FaultedCalls);
        Assert.Equal("bad aim", pair.Server.LastCallFault?.Message);
    }

    [Fact]
    public async Task AHandlerThatThrowsOnAClientDoesNotEndTheClientOrLoseTheRestOfTheBatch()
    {
        await using var pair = new Pair(Crate());
        var client = await pair.Join("p1");
        var seen = new List<string>();
        pair.Client("p1").On("flash", (_, args) =>
        {
            var colour = (string)args[0];
            seen.Add(colour != "bad" ? colour : throw new InvalidOperationException("bad colour"));
        });

        foreach (var colour in new[] { "red", "bad", "blue" })
        {
            pair.Server.CallClient("p1", "flash", colour);
        }

        await pair.Settle();
        await pair.Step(client);

        Assert.Equal(["red", "blue"], seen);
        Assert.Null(client.Fault);
        Assert.Equal(1, pair.Client("p1").FaultedCalls);
        Assert.Equal("bad colour", pair.Client("p1").LastCallFault?.Message);
    }

    [Fact]
    public async Task ACallToOneClientThatNamesAnEntityMadeThisUpdateWaitsForTheTickThatCarriesIt()
    {
        await using var pair = new Pair(Crate());
        var client = await pair.Join("p1");
        var seen = new List<string>();
        pair.Client("p1").On("look", (_, args) => seen.Add(((NetEntity)args[0]).Id));

        pair.Server.BeforeUpdate();
        pair.ServerHost.ApplyEdit(Add(Tagged("late", 0, 0, 0)));
        pair.Server.CallClient("p1", "look", new NetEntity("late"));
        await InTheMiddleOfTheServersUpdate(pair, client);
        pair.Server.AfterUpdate();
        await pair.Settle();
        client.Update(Pair.Idle, 0.016);

        Assert.Equal(["late"], seen);
        Assert.Equal(0, pair.Client("p1").Malformed);
    }

    [Fact]
    public async Task ACallToAllThatNamesAnEntityMadeThisUpdateWaitsForTheTickThatCarriesIt()
    {
        await using var pair = new Pair(Crate());
        var client = await pair.Join("p1");
        var seen = new List<string>();
        pair.Client("p1").On("look", (_, args) => seen.Add(((NetEntity)args[0]).Id));

        pair.Server.BeforeUpdate();
        pair.ServerHost.ApplyEdit(Add(Tagged("late", 0, 0, 0)));
        pair.Server.CallAll("look", new NetEntity("late"));
        await InTheMiddleOfTheServersUpdate(pair, client);
        pair.Server.AfterUpdate();
        await pair.Settle();
        client.Update(Pair.Idle, 0.016);

        Assert.Equal(["late"], seen);
        Assert.Equal(0, pair.Client("p1").Malformed);
    }

    [Fact]
    public async Task CallsToClientsGoOutInTheOrderTheyWereMadeAndAfterTheTick()
    {
        await using var pair = new Pair(Crate());
        var client = await pair.Join("p1");
        var seen = new List<int>();
        pair.Client("p1").On("n", (_, args) => seen.Add((int)args[0]));

        pair.ServerGame.Once(context =>
        {
            context.Net.CallAll("n", 1);
            context.Net.CallClient("p1", "n", 2);
            context.Net.CallAll("n", 3);
        });
        await pair.Step(client);

        Assert.Equal([1, 2, 3], seen);
    }

    // A client frame that runs while the server is part way through its own, as a real client's does.
    private static async Task InTheMiddleOfTheServersUpdate(Pair pair, GameHost client)
    {
        await pair.Settle();
        client.Update(Pair.Idle, 0.016);
        await pair.Settle();
    }

    [Fact]
    public void OnlyACallBodyIsReadAsACall()
    {
        Assert.NotNull(NetWire.ReadCall(NetWire.Call("a", [1])));
        Assert.Null(NetWire.ReadCall(NetWire.Spawn(1, Tagged("x", 0, 0, 0))));
        Assert.Null(NetWire.ReadCall(NetWire.Owner(1, "p1")));
        Assert.Null(NetWire.ReadCall([]));
        Assert.Null(NetWire.ReadCall([3]));
    }

    [Fact]
    public void TwoOfflineNetsHoldTheirOwnHandlers()
    {
        var first = new OfflineNet();
        var second = new OfflineNet();
        var seen = new List<string>();
        first.On("ping", (_, _) => seen.Add("first"));
        second.On("ping", (_, _) => seen.Add("second"));

        second.Call("ping");
        first.Call("ping");

        Assert.Equal(["second", "first"], seen);
    }

    [Fact]
    public void AnOfflineGameRunsItsOwnCallsAtOnce()
    {
        var net = new OfflineNet();
        var seen = new List<(string? From, int Number)>();
        net.On("offline.ping", (caller, args) => seen.Add((caller.ClientId, (int)args[0])));

        net.Call("offline.ping", 1);
        net.CallAll("offline.ping", 2);
        net.CallClient("p1", "offline.ping", 3);

        Assert.Equal([(null, 1), (null, 2), (null, 3)], seen);
    }
}
