using Orblit.Game;
using Orblit.Scene;
using Orblit.VectorMath;
using static Orblit.Net.Game.Tests.Scenes;

namespace Orblit.Net.Game.Tests;

/// <summary>
/// Methods marked <c>[ServerRpc]</c> and <c>[ClientRpc]</c> are bound by name, on the end whose
/// role they are for, and checked against the codec's types before any call is made.
/// </summary>
public sealed class NetRpcTests
{
    private static SceneDocument Crate() => World(Tagged("crate", 0, 0, 0));

    [Fact]
    public async Task AServerRpcRunsWhenAClientCallsItByName()
    {
        await using var pair = new Pair(Crate());
        var client = await pair.Join("p1");
        var arena = new Arena();
        pair.Server.Bind(arena);

        pair.Client("p1").Call("Hit", 5);
        await pair.Settle();
        await pair.Step(client);

        Assert.Equal([("p1", 5)], arena.Hits);
    }

    [Fact]
    public async Task EveryTypeACallCarriesReachesAMethodThatTakesIt()
    {
        await using var pair = new Pair(Crate());
        var client = await pair.Join("p1");
        var arena = new Arena();
        pair.Server.Bind(arena);

        pair.Client("p1").Call("Pick", new NetEntity("crate"), new Vector3(1, 2, 3), 0.5f, true, "note");
        await pair.Settle();
        await pair.Step(client);

        Assert.Equal(["p1 crate 1,2,3 0.5 True note"], arena.Picks);
    }

    [Fact]
    public async Task AClientRpcRunsWhenTheServerCallsIt()
    {
        await using var pair = new Pair(Crate());
        var client = await pair.Join("p1");
        var arena = new Arena();
        pair.Client("p1").Bind(arena);

        pair.Server.CallAll("Flash", "red");
        await pair.Settle();
        await pair.Step(client);

        Assert.Equal(["red"], arena.Flashes);
    }

    [Fact]
    public async Task AnEndBindsOnlyTheMethodsForItsRole()
    {
        await using var pair = new Pair(Crate());
        var client = await pair.Join("p1");
        var onServer = new Arena();
        var onClient = new Arena();
        pair.Server.Bind(onServer);
        pair.Client("p1").Bind(onClient);

        pair.Client("p1").Call("Flash", "red");
        pair.Server.CallAll("Hit", 1);
        await pair.Settle();
        await pair.Step(client);

        Assert.Empty(onServer.Flashes);
        Assert.Empty(onClient.Hits);
        Assert.Equal(1, pair.Server.UnhandledCalls);
        Assert.Equal(1, pair.Client("p1").UnhandledCalls);
    }

    [Fact]
    public async Task AMethodWithNoParametersAndOneMarkedForBothRolesAreBound()
    {
        await using var pair = new Pair(Crate());
        var client = await pair.Join("p1");
        var arena = new Arena();
        pair.Server.Bind(arena);
        pair.Client("p1").Bind(arena);

        pair.Client("p1").Call("Both");
        pair.Server.CallAll("Both");
        await pair.Settle();
        await pair.Step(client);

        Assert.Equal(2, arena.Boths);
    }

    [Fact]
    public async Task AnUnmarkedMethodIsNotBoundAndANonPublicMarkedOneIs()
    {
        await using var pair = new Pair(Crate());
        var client = await pair.Join("p1");
        var arena = new Arena();
        pair.Server.Bind(arena);

        pair.Client("p1").Call("Helper");
        pair.Client("p1").Call("Quiet");
        await pair.Settle();
        await pair.Step(client);

        Assert.Equal(0, arena.Helped);
        Assert.Equal(1, arena.Quiets);
        Assert.Equal(1, pair.Server.UnhandledCalls);
    }

    [Theory]
    [InlineData("string")]
    [InlineData("none")]
    [InlineData("extra")]
    public async Task ACallThatDoesNotFitTheMethodIsDroppedAndTheGameGoesOn(string how)
    {
        await using var pair = new Pair(Crate());
        var client = await pair.Join("p1");
        var arena = new Arena();
        pair.Server.Bind(arena);

        switch (how)
        {
            case "string":
                pair.Client("p1").Call("Hit", "five");
                break;
            case "none":
                pair.Client("p1").Call("Hit");
                break;
            default:
                pair.Client("p1").Call("Hit", 1, 2);
                break;
        }

        await pair.Settle();
        await pair.Step(client);

        Assert.Empty(arena.Hits);
        Assert.Equal(1, pair.Server.Malformed);
    }

    [Theory]
    [InlineData(typeof(TakesADouble), "TakesADouble.Bad")]
    [InlineData(typeof(ReturnsAValue), "ReturnsAValue.Returns")]
    [InlineData(typeof(CallerNotFirst), "CallerNotFirst.Late")]
    [InlineData(typeof(TakesARef), "TakesARef.Ref")]
    [InlineData(typeof(TakesAList), "TakesAList.Many")]
    [InlineData(typeof(Overloaded), "Overloaded.Same")]
    public async Task ABadMarkedMethodFailsAtBindOnEveryEndAndNamesTheMethod(Type game, string named)
    {
        await using var pair = new Pair(Crate());
        await pair.Join("p1");
        var instance = Activator.CreateInstance(game)!;
        IGameNet[] nets = [pair.Server, pair.Client("p1"), OfflineNet.Instance];

        foreach (var net in nets)
        {
            var error = Assert.Throws<ArgumentException>(() => net.Bind(instance));
            Assert.Contains(named, error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BindingNoGameIsRefused() => Assert.Throws<ArgumentNullException>(() => OfflineNet.Instance.Bind(null!));

    [Fact]
    public void AnOfflineGameBindsBothRolesAndRunsThemAtOnce()
    {
        var game = new OfflineArena();
        OfflineNet.Instance.Bind(game);

        OfflineNet.Instance.Call("OfflineHit", 2);
        OfflineNet.Instance.Call("OfflineFlash", "blue");

        Assert.Equal([(null, 2)], game.Hits);
        Assert.Equal(["blue"], game.Flashes);
    }

    [Fact]
    public void AMethodThatThrowsThrowsItsOwnErrorNotTheReflectionWrapper()
    {
        OfflineNet.Instance.Bind(new OfflineArena());

        var error = Assert.Throws<InvalidOperationException>(() => OfflineNet.Instance.Call("OfflineBoom"));

        Assert.Equal("boom", error.Message);
    }

    public sealed class Arena
    {
        public List<(string? From, int Amount)> Hits { get; } = [];

        public List<string> Flashes { get; } = [];

        public List<string> Picks { get; } = [];

        public int Boths { get; private set; }

        public int Helped { get; private set; }

        public int Quiets { get; private set; }

        [ServerRpc]
        public void Hit(NetCaller caller, int amount) => Hits.Add((caller.ClientId, amount));

        [ClientRpc]
        public void Flash(string colour) => Flashes.Add(colour);

        [ServerRpc]
        public void Pick(NetCaller caller, NetEntity entity, Vector3 spot, float power, bool flag, string note) =>
            Picks.Add($"{caller.ClientId} {entity.Id} {spot.X},{spot.Y},{spot.Z} {power} {flag} {note}");

        [ServerRpc]
        [ClientRpc]
        public void Both() => Boths++;

        public void Helper() => Helped++;

        [ServerRpc]
        internal void Quiet() => Quiets++;
    }

    public sealed class OfflineArena
    {
        public List<(string? From, int Amount)> Hits { get; } = [];

        public List<string> Flashes { get; } = [];

        [ServerRpc]
        public void OfflineHit(NetCaller caller, int amount) => Hits.Add((caller.ClientId, amount));

        [ClientRpc]
        public void OfflineFlash(string colour) => Flashes.Add(colour);

        [ServerRpc]
        public static void OfflineBoom() => throw new InvalidOperationException("boom");
    }

    public sealed class TakesADouble
    {
        [ServerRpc]
        public static void Bad(double amount) => _ = amount;
    }

    public sealed class ReturnsAValue
    {
        [ClientRpc]
        public static int Returns() => 1;
    }

    public sealed class CallerNotFirst
    {
        [ServerRpc]
        public static void Late(int amount, NetCaller caller) => _ = (amount, caller);
    }

    public sealed class TakesARef
    {
        [ServerRpc]
        public static void Ref(ref int amount) => amount++;
    }

    public sealed class TakesAList
    {
        [ServerRpc]
        public static void Many(List<int> amounts) => _ = amounts;
    }

    public sealed class Overloaded
    {
        [ServerRpc]
        public static void Same(int amount) => _ = amount;

        [ServerRpc]
        public static void Same(string text) => _ = text;
    }
}
