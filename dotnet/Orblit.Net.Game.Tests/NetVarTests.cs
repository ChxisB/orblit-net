using Orblit.Game;
using Orblit.Scene;
using Orblit.VectorMath;
using static Orblit.Net.Game.Tests.Scenes;

namespace Orblit.Net.Game.Tests;

/// <summary>
/// A named variable on a tagged entity crosses the wire as a column. Every test runs two real game
/// hosts over a loopback link, and reads the variable through the same handle a game would.
/// </summary>
public sealed class NetVarTests
{
    private static readonly string[] Everything = ["transform", "health", "alive", "score", "spot"];

    private static readonly NetVarSchema Vars = NetVarSchema.Empty
        .With<float>("health")
        .With<bool>("alive")
        .With<int>("score")
        .With<Vector3>("spot");

    [Fact]
    public async Task AFloatTheServerSetsReadsTheSameOnTheClient()
    {
        await using var pair = new Pair(World(Tagged("crate", 0, 0, 0, sync: Everything)), vars: Vars);
        var client = await pair.Join("p1");

        pair.Server.Var<float>("crate", "health").Value = 72.5f;
        await pair.Step(client);

        Assert.Equal(72.5f, pair.Client("p1").Var<float>("crate", "health").Value);
    }

    [Fact]
    public async Task ABoolAnIntAndAVectorReadTheSameOnTheClient()
    {
        await using var pair = new Pair(World(Tagged("crate", 0, 0, 0, sync: Everything)), vars: Vars);
        var client = await pair.Join("p1");

        pair.Server.Var<bool>("crate", "alive").Value = true;
        pair.Server.Var<int>("crate", "score").Value = -123456789;
        pair.Server.Var<Vector3>("crate", "spot").Value = new Vector3(1.5, -2, 3.25);
        await pair.Step(client);

        var spot = pair.Client("p1").Var<Vector3>("crate", "spot").Value;
        Assert.True(pair.Client("p1").Var<bool>("crate", "alive").Value);
        Assert.Equal(-123456789, pair.Client("p1").Var<int>("crate", "score").Value);
        Assert.Equal([1.5, -2, 3.25], [spot.X, spot.Y, spot.Z]);
    }

    [Fact]
    public async Task AVariableChangesAgainWhenTheServerSetsItAgain()
    {
        await using var pair = new Pair(World(Tagged("crate", 0, 0, 0, sync: Everything)), vars: Vars);
        var client = await pair.Join("p1");
        var health = pair.Server.Var<float>("crate", "health");
        health.Value = 10;
        await pair.Step(client);

        health.Value = 4;
        await pair.Step(client);

        Assert.Equal(4f, pair.Client("p1").Var<float>("crate", "health").Value);
    }

    [Fact]
    public async Task ALateJoinerReadsTheValueTheServerHolds()
    {
        await using var pair = new Pair(World(Tagged("crate", 0, 0, 0, sync: Everything)), vars: Vars);
        var early = await pair.Join("p1");
        pair.Server.Var<int>("crate", "score").Value = 41;
        await pair.Step(early);

        var late = await pair.Join("p2");
        await pair.Step(late);

        Assert.Equal(41, pair.Client("p2").Var<int>("crate", "score").Value);
    }

    [Fact]
    public async Task TwoEndsThatListTheVariablesInADifferentOrderStillAgree()
    {
        var reversed = NetVarSchema.Empty
            .With<Vector3>("spot")
            .With<int>("score")
            .With<bool>("alive")
            .With<float>("health");
        await using var pair = new Pair(World(Tagged("crate", 0, 0, 0, sync: Everything)), vars: Vars);
        var client = await pair.Join("p1", vars: reversed);

        pair.Server.Var<float>("crate", "health").Value = 9;
        pair.Server.Var<int>("crate", "score").Value = 7;
        pair.Server.Var<bool>("crate", "alive").Value = true;
        await pair.Step(client);

        Assert.Equal(9f, pair.Client("p1").Var<float>("crate", "health").Value);
        Assert.Equal(7, pair.Client("p1").Var<int>("crate", "score").Value);
        Assert.True(pair.Client("p1").Var<bool>("crate", "alive").Value);
    }

    [Fact]
    public async Task AFloatVariableBlendsBetweenTwoServerStatesWhenTheClientInterpolates()
    {
        var now = TimeSpan.Zero;
        await using var pair = new Pair(
            World(Tagged("crate", 0, 0, 0, sync: Everything)),
            () => now,
            TimeSpan.FromMilliseconds(100),
            Vars);
        var client = await pair.Join("p1");
        await pair.Step(client);
        now = TimeSpan.FromMilliseconds(100);
        pair.Server.Var<float>("crate", "health").Value = 10;
        pair.ServerHost.Update(Pair.Idle, 0.016);
        await pair.Settle();
        now = TimeSpan.FromMilliseconds(150);

        client.Update(Pair.Idle, 0.016);

        Assert.Equal(5f, pair.Client("p1").Var<float>("crate", "health").Value);
    }

    [Fact]
    public async Task AVariableOnAnEntityTheServerSpawnsReachesTheClient()
    {
        await using var pair = new Pair(World(), vars: Vars);
        var client = await pair.Join("p1");
        pair.ServerGame.Once(context => context.Scene.Apply(Add(Tagged("bullet", 0, 0, 0, sync: Everything))));
        await pair.Step(client);

        pair.Server.Var<int>("bullet", "score").Value = 3;
        await pair.Step(client);

        Assert.Equal(3, pair.Client("p1").Var<int>("bullet", "score").Value);
    }

    [Fact]
    public async Task AnEntityThatIsRemovedAndAddedAgainStartsFromZero()
    {
        await using var pair = new Pair(World(Tagged("crate", 0, 0, 0, sync: Everything)), vars: Vars);
        var client = await pair.Join("p1");
        pair.Server.Var<int>("crate", "score").Value = 5;
        await pair.Step(client);
        pair.ServerGame.Once(context => context.Scene.Apply(Remove(context.Scene.Document, "crate")));
        await pair.Step(client);
        pair.ServerGame.Once(context => context.Scene.Apply(Add(Tagged("crate", 0, 0, 0, sync: Everything))));
        await pair.Step(client);

        Assert.Equal(0, pair.Server.Var<int>("crate", "score").Value);
        Assert.Equal(0, pair.Client("p1").Var<int>("crate", "score").Value);
    }

    [Fact]
    public async Task AClientCannotWriteAVariableTheServerHoldsTheAuthorityOver()
    {
        await using var pair = new Pair(World(Tagged("crate", 0, 0, 0, sync: Everything)), vars: Vars);
        var client = await pair.Join("p1");
        await pair.Step(client);

        Assert.Throws<InvalidOperationException>(() => pair.Client("p1").Var<float>("crate", "health").Value = 1);
    }

    [Fact]
    public async Task AForgedWriteToAServerVariableIsRefusedAndCounted()
    {
        await using var pair = new Pair(World(Tagged("crate", 0, 0, 0, sync: Everything)), vars: Vars);
        var client = await pair.Join("p1");
        pair.Server.Var<float>("crate", "health").Value = 8;
        await pair.Step(client);

        pair.Client("p1").ForgeVar("crate", "health", BitConverter.GetBytes(99f));
        await pair.Step(client);

        Assert.Equal(8f, pair.Server.Var<float>("crate", "health").Value);
        Assert.Equal(1, pair.Server.Host.Rejections[InputRejection.NotOwned]);
    }

    [Fact]
    public async Task TheOwnerWritesAVariableAndTheServerAndTheOtherClientReadIt()
    {
        await using var pair = new Pair(World(Tagged("hero", 0, 0, 0, NetAuthority.Owner, Everything)), vars: Vars);
        var owner = await pair.Join("p1");
        var other = await pair.Join("p2");
        pair.Server.Assign("hero", "p1");
        await pair.Step(owner, other);

        pair.Client("p1").Var<int>("hero", "score").Value = 12;
        await pair.Step(owner, other);
        await pair.Step(owner, other);

        Assert.Equal(12, pair.Server.Var<int>("hero", "score").Value);
        Assert.Equal(12, pair.Client("p2").Var<int>("hero", "score").Value);
        Assert.Empty(pair.Server.Host.Rejections);
    }

    [Fact]
    public async Task AClientThatIsNotTheOwnerCannotWriteAnOwnerVariable()
    {
        await using var pair = new Pair(World(Tagged("hero", 0, 0, 0, NetAuthority.Owner, Everything)), vars: Vars);
        var owner = await pair.Join("p1");
        var other = await pair.Join("p2");
        pair.Server.Assign("hero", "p1");
        await pair.Step(owner, other);

        Assert.Throws<InvalidOperationException>(() => pair.Client("p2").Var<int>("hero", "score").Value = 1);
        pair.Client("p2").ForgeVar("hero", "score", BitConverter.GetBytes(1));
        await pair.Step(owner, other);

        Assert.Equal(0, pair.Server.Var<int>("hero", "score").Value);
        Assert.Equal(1, pair.Server.Host.Rejections[InputRejection.NotOwned]);
    }

    [Fact]
    public async Task AServerWriteToAVariableItsOwnerHoldsIsUndoneByTheOwnersValue()
    {
        await using var pair = new Pair(World(Tagged("hero", 0, 0, 0, NetAuthority.Owner, Everything)), vars: Vars);
        var owner = await pair.Join("p1");
        var other = await pair.Join("p2");
        pair.Server.Assign("hero", "p1");
        await pair.Step(owner, other);
        pair.Client("p1").Var<int>("hero", "score").Value = 12;
        await pair.Step(owner, other);
        await pair.Step(owner, other);

        var score = pair.Server.Var<int>("hero", "score");
        pair.ServerGame.Once(_ => score.Value = 99);
        await pair.Step(owner, other);
        await pair.Step(owner, other);

        Assert.Equal(12, score.Value);
        Assert.Equal(12, pair.Client("p2").Var<int>("hero", "score").Value);
    }

    [Fact]
    public async Task AnOwnerThatChangesNothingSendsNothing()
    {
        await using var pair = new Pair(World(Tagged("hero", 0, 0, 0, NetAuthority.Owner, Everything)), vars: Vars);
        var owner = await pair.Join("p1");
        pair.Server.Assign("hero", "p1");
        await pair.Step(owner);
        pair.Client("p1").Var<int>("hero", "score").Value = 12;
        await pair.Step(owner);
        await pair.Step(owner);
        var accepted = pair.Server.Host.AcceptedInputs;

        await pair.Step(owner);
        await pair.Step(owner);
        await pair.Step(owner);

        Assert.Equal(accepted, pair.Server.Host.AcceptedInputs);
    }

    [Fact]
    public async Task AnOwnerHandedTheEntityAgainTakesTheServersValueWithoutSendingIt()
    {
        await using var pair = new Pair(World(Tagged("hero", 0, 0, 0, NetAuthority.Owner, Everything)), vars: Vars);
        var owner = await pair.Join("p1");
        pair.Server.Assign("hero", "p1");
        await pair.Step(owner);
        pair.Client("p1").Var<int>("hero", "score").Value = 12;
        await pair.Step(owner);
        await pair.Step(owner);
        pair.Server.Assign("hero", null);
        await pair.Step(owner);

        pair.Server.Var<int>("hero", "score").Value = 40;
        await pair.Step(owner);
        var accepted = pair.Server.Host.AcceptedInputs;

        pair.Server.Assign("hero", "p1");
        await pair.Step(owner);
        await pair.Step(owner);

        Assert.Equal(40, pair.Client("p1").Var<int>("hero", "score").Value);
        Assert.Equal(40, pair.Server.Var<int>("hero", "score").Value);
        Assert.Equal(accepted, pair.Server.Host.AcceptedInputs);
    }

    [Fact]
    public async Task AnOwnerStartsFromTheValueTheServerHeldWhenItWasHandedTheEntity()
    {
        await using var pair = new Pair(World(Tagged("hero", 0, 0, 0, NetAuthority.Owner, Everything)), vars: Vars);
        var owner = await pair.Join("p1");
        pair.Server.Var<int>("hero", "score").Value = 30;
        await pair.Step(owner);

        pair.Server.Assign("hero", "p1");
        await pair.Step(owner);
        await pair.Step(owner);

        Assert.Equal(30, pair.Client("p1").Var<int>("hero", "score").Value);
        Assert.Equal(30, pair.Server.Var<int>("hero", "score").Value);
    }

    [Fact]
    public void EachTypeEncodesToTheSizeOfItsColumn()
    {
        Assert.Equal(NetVarCodec.ByteSize(NetVarKind.Bool), NetVarCodec.Encode(true).Length);
        Assert.Equal(NetVarCodec.ByteSize(NetVarKind.Int), NetVarCodec.Encode(7).Length);
        Assert.Equal(NetVarCodec.ByteSize(NetVarKind.Float), NetVarCodec.Encode(1.5f).Length);
        Assert.Equal(NetVarCodec.ByteSize(NetVarKind.Vector), NetVarCodec.Encode(new Vector3(1, 2, 3)).Length);
    }

    [Fact]
    public void ANameDeclaredWithTwoTypesThrows()
    {
        var schema = NetVarSchema.Empty.With<float>("health");

        Assert.Throws<ArgumentException>(() => schema.With<int>("health"));
        Assert.Same(schema, schema.With<float>("health"));
    }

    [Fact]
    public void TransformIsReservedForTheEntitysPlace()
    {
        Assert.Throws<ArgumentException>(() => NetVarSchema.Empty.With<float>("transform"));
    }

    [Fact]
    public void ASchemaHoldsSixtyThreeVariablesAndThrowsAtTheSixtyFourth()
    {
        var schema = NetVarSchema.Empty;
        for (var i = 1; i <= 63; i++)
        {
            schema = schema.With<int>($"v{i}");
        }

        var error = Assert.Throws<ArgumentException>(() => schema.With<int>("v64"));

        Assert.Equal(63, schema.Names.Count);
        Assert.Contains("64", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AVariableOfAnyOtherTypeThrows()
    {
        Assert.Throws<ArgumentException>(() => NetVarSchema.Empty.With<double>("height"));
    }

    [Fact]
    public async Task AskingForAVariableTheSchemaDoesNotHoldThrows()
    {
        await using var pair = new Pair(World(Tagged("crate", 0, 0, 0, sync: Everything)), vars: Vars);

        Assert.Throws<ArgumentException>(() => pair.Server.Var<float>("crate", "mana"));
        Assert.Throws<ArgumentException>(() => pair.Server.Var<int>("crate", "health"));
    }

    [Fact]
    public async Task AskingForAVariableTheEntityDoesNotShareThrows()
    {
        await using var pair = new Pair(
            World(Tagged("crate", 0, 0, 0, sync: ["transform", "health"]), Plain("rock"), Tagged("rune", 0, 0, 0)),
            vars: Vars);

        Assert.Throws<InvalidOperationException>(() => pair.Server.Var<int>("crate", "score"));
        Assert.Throws<InvalidOperationException>(() => pair.Server.Var<float>("rock", "health"));
        Assert.Throws<InvalidOperationException>(() => pair.Server.Var<float>("rune", "health"));
    }

    [Fact]
    public void AnOfflineNetKeepsTheValueInTheHandle()
    {
        var score = OfflineNet.Instance.Var<int>("anything", "score");

        score.Value = 6;

        Assert.Equal(6, score.Value);
    }

    private static SceneEntity Plain(string id) => new(id, id);
}
