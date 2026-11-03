using Orblit.Game;
using Orblit.Scene;
using Orblit.VectorMath;
using static Orblit.Net.Game.Tests.Scenes;

namespace Orblit.Net.Game.Tests;

/// <summary>
/// A tagged entity crosses the wire and an untagged one does not. Every test runs two real game hosts
/// over a loopback link, because the frame order is part of what is being promised.
/// </summary>
public sealed class NetworkedSceneTests
{
    [Fact]
    public async Task ATaggedEntityTheServerMovesMovesOnTheClient()
    {
        await using var pair = new Pair(World(Tagged("crate", 0, 0, 0)));
        var client = await pair.Join("p1");
        pair.Move("crate", 4, 5, 6);

        await pair.Step(client);

        Assert.Equal([4, 5, 6], PositionOf(client, "crate"));
    }

    [Fact]
    public async Task AnUntaggedEntityIsNeverSentSoItStaysWhereTheClientHasIt()
    {
        await using var pair = new Pair(World(Tagged("crate", 0, 0, 0), Plain("rock", 1, 1, 1)));
        var client = await pair.Join("p1");
        pair.Move("rock", 9, 9, 9);

        await pair.Step(client);

        Assert.Equal([1, 1, 1], PositionOf(client, "rock"));
        Assert.Equal(1, pair.Server.Host.World.EntityCount);
    }

    [Fact]
    public async Task ASharedEntityWithNothingToSyncIsSpawnedButNotMoved()
    {
        await using var pair = new Pair(World(Tagged("crate", 0, 0, 0, sync: [])));
        var client = await pair.Join("p1");
        pair.Move("crate", 7, 7, 7);

        await pair.Step(client);

        Assert.Equal([0, 0, 0], PositionOf(client, "crate"));
        Assert.Equal(1, pair.Server.Host.World.EntityCount);
    }

    [Fact]
    public async Task AnEntityTheClientAlreadyHasIsAdoptedWithoutAnAddBeingHeard()
    {
        await using var pair = new Pair(World(Tagged("crate", 0, 0, 0)));
        var mine = new SceneEntity("crate", "my crate", components: Tagged("crate", 0, 0, 0).Components);
        var client = await pair.Join("p1", scene: World(mine));
        var heard = new List<SceneOp>();
        client.SceneChanged += diff => heard.AddRange(diff.Operations);

        await pair.Step(client);

        Assert.Single(client.Scene.Entities, entity => entity.Id == "crate");
        Assert.Equal("my crate", client.Scene["crate"]!.Name);
        Assert.DoesNotContain(heard, op => op is AddEntity);
    }

    [Fact]
    public async Task ASpawnTheServerMakesAppearsOnTheClientAsTheSameEntity()
    {
        await using var pair = new Pair(World());
        var client = await pair.Join("p1");
        pair.ServerGame.Once(context => context.Scene.Apply(Add(Tagged("bullet", 1, 2, 3))));

        await pair.Step(client);

        var seen = client.Scene["bullet"];
        Assert.NotNull(seen);
        Assert.Equal("bullet", seen.Name);
        Assert.True(seen.Has(SceneComponents.Net));
        Assert.Equal([1, 2, 3], PositionOf(client, "bullet"));
    }

    [Fact]
    public async Task ALateJoinerSeesTheEntitiesThatAreAlreadyLive()
    {
        await using var pair = new Pair(World());
        var early = await pair.Join("p1");
        pair.ServerGame.Once(context => context.Scene.Apply(Add(Tagged("bullet", 1, 2, 3))));
        await pair.Step(early);

        var late = await pair.Join("p2", scene: World());
        await pair.Step(late);

        Assert.Equal([1, 2, 3], PositionOf(late, "bullet"));
    }

    [Fact]
    public async Task ADespawnRemovesTheEntityOnEveryClient()
    {
        await using var pair = new Pair(World(Tagged("crate", 0, 0, 0)));
        var first = await pair.Join("p1");
        var second = await pair.Join("p2");
        await pair.Step(first, second);
        pair.ServerGame.Once(context => context.Scene.Apply(Remove(context.Scene.Document, "crate")));

        await pair.Step(first, second);

        Assert.False(first.Scene.Contains("crate"));
        Assert.False(second.Scene.Contains("crate"));
        Assert.Equal(0, pair.Server.Host.World.EntityCount);
    }

    [Fact]
    public async Task AnEntityThatComesAndGoesBetweenTwoClientFramesIsNeverMade()
    {
        await using var pair = new Pair(World());
        var client = await pair.Join("p1");
        pair.ServerGame.Once(context => context.Scene.Apply(Add(Tagged("spark", 0, 0, 0))));
        pair.ServerHost.Update(Pair.Idle, 0.016);
        await pair.Settle();
        pair.ServerGame.Once(context => context.Scene.Apply(Remove(context.Scene.Document, "spark")));
        pair.ServerHost.Update(Pair.Idle, 0.016);
        await pair.Settle();

        client.Update(Pair.Idle, 0.016);

        Assert.False(client.Scene.Contains("spark"));
        Assert.Null(client.Fault);
    }

    [Fact]
    public async Task TwoClientsAndTheServerEndWithTheSameWorld()
    {
        await using var pair = new Pair(World(Tagged("a", 0, 0, 0), Tagged("b", 0, 0, 0)));
        var first = await pair.Join("p1");
        var second = await pair.Join("p2");
        pair.ServerGame.Once(context =>
        {
            context.Scene.Apply(Pair.Moved(context.Scene.Document, "a", new Vector3(1, 2, 3)));
            context.Scene.Apply(Pair.Moved(context.Scene.Document, "b", new Vector3(-4, 0, 8)));
        });

        await pair.Step(first, second);

        Assert.Equal([1, 2, 3], PositionOf(first, "a"));
        Assert.Equal([-4, 0, 8], PositionOf(second, "b"));
        Assert.Equal(PositionOf(first, "a"), PositionOf(second, "a"));
        Assert.Equal(PositionOf(first, "b"), PositionOf(second, "b"));
    }

    [Fact]
    public async Task AClientCannotMoveAnEntityTheServerHoldsTheAuthorityOver()
    {
        await using var pair = new Pair(World(Tagged("crate", 0, 0, 0)));
        var client = await pair.Join("p1");
        await pair.Step(client);

        pair.Client("p1").Forge("crate", Place(50, 0, 0));
        await pair.Step(client);

        Assert.Equal([0, 0, 0], PositionOf(pair.ServerHost, "crate"));
        Assert.Equal(1, pair.Server.Host.Rejections[InputRejection.NotOwned]);
    }

    [Fact]
    public async Task TheOwnerMovesAnOwnerAuthorityEntityAndTheServerTakesIt()
    {
        await using var pair = new Pair(World(Tagged("hero", 0, 0, 0, NetAuthority.Owner)));
        var client = await pair.Join("p1");
        pair.Server.Assign("hero", "p1");
        await pair.Step(client);
        pair.ClientGame("p1").Once(context =>
            context.Scene.Apply(Pair.Moved(context.Scene.Document, "hero", new Vector3(3, 0, 0))));

        await pair.Step(client);
        await pair.Step(client);

        Assert.Equal([3, 0, 0], PositionOf(pair.ServerHost, "hero"));
        Assert.Empty(pair.Server.Host.Rejections);
    }

    [Fact]
    public async Task AClientThatIsNotTheOwnerCannotMoveAnOwnerAuthorityEntity()
    {
        await using var pair = new Pair(World(Tagged("hero", 0, 0, 0, NetAuthority.Owner)));
        var owner = await pair.Join("p1");
        var other = await pair.Join("p2");
        pair.Server.Assign("hero", "p1");
        await pair.Step(owner, other);

        pair.Client("p2").Forge("hero", Place(50, 0, 0));
        await pair.Step(owner, other);

        Assert.Equal([0, 0, 0], PositionOf(pair.ServerHost, "hero"));
        Assert.Equal(1, pair.Server.Host.Rejections[InputRejection.NotOwned]);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public async Task AnOwnerCannotPutItsEntityAtAPlaceThatIsNotAFiniteNumber(float bad)
    {
        await using var pair = new Pair(World(Tagged("hero", 1, 2, 3, NetAuthority.Owner)));
        var owner = await pair.Join("p1");
        var other = await pair.Join("p2");
        pair.Server.Assign("hero", "p1");
        await pair.Step(owner, other);

        pair.Client("p1").Forge("hero", Place(bad, 0, 0));
        await pair.Step(owner, other);
        await pair.Step(owner, other);

        Assert.Equal([1, 2, 3], PositionOf(pair.ServerHost, "hero"));
        Assert.Equal([1, 2, 3], PositionOf(other, "hero"));
        Assert.Null(pair.ServerHost.Fault);
    }

    [Fact]
    public async Task AnOwnerCanStillMoveAfterItsEntityWasPutBackFromABadPlace()
    {
        await using var pair = new Pair(World(Tagged("hero", 1, 2, 3, NetAuthority.Owner)));
        var owner = await pair.Join("p1");
        pair.Server.Assign("hero", "p1");
        await pair.Step(owner);
        pair.Client("p1").Forge("hero", Place(float.NaN, 0, 0));
        await pair.Step(owner);
        pair.ClientGame("p1").Once(context =>
            context.Scene.Apply(Pair.Moved(context.Scene.Document, "hero", new Vector3(4, 0, 0))));

        await pair.Step(owner);
        await pair.Step(owner);

        Assert.Equal([4, 0, 0], PositionOf(pair.ServerHost, "hero"));
    }

    [Fact]
    public async Task AnEntityCannotBeHandedToAClientTheServerDoesNotKnow()
    {
        await using var pair = new Pair(World(Tagged("hero", 0, 0, 0, NetAuthority.Owner)));
        await pair.Join("p1");

        Assert.Throws<ArgumentException>(() => pair.Server.Assign("hero", "ghost"));
        pair.Server.RemoveClient("p1");
        Assert.Throws<ArgumentException>(() => pair.Server.Assign("hero", "p1"));
        Assert.Null(pair.Server.OwnerOf("hero"));
    }

    [Fact]
    public async Task AnOwnerAuthorityEntityTheOwnerMovesShowsOnTheOtherClient()
    {
        await using var pair = new Pair(World(Tagged("hero", 0, 0, 0, NetAuthority.Owner)));
        var owner = await pair.Join("p1");
        var other = await pair.Join("p2");
        pair.Server.Assign("hero", "p1");
        await pair.Step(owner, other);
        pair.ClientGame("p1").Once(context =>
            context.Scene.Apply(Pair.Moved(context.Scene.Document, "hero", new Vector3(0, 0, 9))));

        await pair.Step(owner, other);
        await pair.Step(owner, other);

        Assert.Equal([0, 0, 9], PositionOf(other, "hero"));
    }

    [Fact]
    public async Task TheOwnersPlaceWinsOverAServerMoveOfTheSameEntity()
    {
        await using var pair = new Pair(World(Tagged("hero", 0, 0, 0, NetAuthority.Owner)));
        var owner = await pair.Join("p1");
        pair.Server.Assign("hero", "p1");
        await pair.Step(owner);
        pair.ClientGame("p1").Once(context =>
            context.Scene.Apply(Pair.Moved(context.Scene.Document, "hero", new Vector3(5, 0, 0))));
        await pair.Step(owner);
        await pair.Step(owner);
        pair.Move("hero", 9, 0, 0);

        await pair.Step(owner);
        await pair.Step(owner);

        Assert.Equal([5, 0, 0], PositionOf(owner, "hero"));
        Assert.Equal([5, 0, 0], PositionOf(pair.ServerHost, "hero"));
    }

    [Fact]
    public async Task AnOwnerAuthorityEntityNobodyOwnsIsMovedByTheServer()
    {
        await using var pair = new Pair(World(Tagged("hero", 0, 0, 0, NetAuthority.Owner)));
        var client = await pair.Join("p1");
        pair.Move("hero", 2, 0, 0);

        await pair.Step(client);

        Assert.Equal([2, 0, 0], PositionOf(client, "hero"));
    }

    [Fact]
    public async Task WhenAnOwnerLeavesTheEntityGoesBackToTheServerOnEveryEnd()
    {
        await using var pair = new Pair(World(Tagged("hero", 0, 0, 0, NetAuthority.Owner)));
        var leaving = await pair.Join("p1");
        var staying = await pair.Join("p2");
        pair.Server.Assign("hero", "p1");
        await pair.Step(leaving, staying);
        Assert.Equal("p1", pair.Client("p2").OwnerOf("hero"));

        pair.Server.RemoveClient("p1");
        await pair.Step(staying);

        Assert.Null(pair.Server.OwnerOf("hero"));
        Assert.Null(pair.Client("p2").OwnerOf("hero"));
    }

    [Fact]
    public async Task AClientBlendsBetweenTwoServerStatesWhenItInterpolates()
    {
        var now = TimeSpan.Zero;
        await using var pair = new Pair(World(Tagged("crate", 0, 0, 0)), () => now, TimeSpan.FromMilliseconds(100));
        var client = await pair.Join("p1");
        await pair.Step(client);
        now = TimeSpan.FromMilliseconds(100);
        pair.Move("crate", 10, 0, 0);
        pair.ServerHost.Update(Pair.Idle, 0.016);
        await pair.Settle();
        now = TimeSpan.FromMilliseconds(150);

        client.Update(Pair.Idle, 0.016);

        Assert.Equal([5, 0, 0], PositionOf(client, "crate"));
    }

    [Fact]
    public async Task OwnerOfAnswersOnBothEnds()
    {
        await using var pair = new Pair(World(Tagged("hero", 0, 0, 0, NetAuthority.Owner), Tagged("crate", 0, 0, 0)));
        var client = await pair.Join("p1");
        pair.Server.Assign("hero", "p1");
        await pair.Step(client);

        Assert.Equal("p1", pair.Server.OwnerOf("hero"));
        Assert.Null(pair.Server.OwnerOf("crate"));
        Assert.Equal("p1", pair.Client("p1").OwnerOf("hero"));
        Assert.Null(pair.Client("p1").OwnerOf("crate"));
        Assert.Equal(GameRole.Server, pair.Server.Role);
        Assert.Equal(GameRole.Client, pair.Client("p1").Role);
        Assert.Equal("p1", pair.Client("p1").LocalClient);
        Assert.Null(pair.Server.LocalClient);
    }

    [Fact]
    public async Task AServerEntityCannotBeHandedToAClient()
    {
        await using var pair = new Pair(World(Tagged("crate", 0, 0, 0)));
        var client = await pair.Join("p1");
        await pair.Step(client);

        Assert.Throws<InvalidOperationException>(() => pair.Server.Assign("crate", "p1"));
        Assert.Throws<InvalidOperationException>(() => pair.Server.Assign("missing", "p1"));
    }

    [Fact]
    public async Task AMalformedMessageIsCountedAndDoesNotEndTheGame()
    {
        await using var pair = new Pair(World());
        var client = await pair.Join("p1");
        byte[] unknownTag = [250, 0, 0, 0, 0, 0, 0, 0, 0];
        byte[] spawnWithBadJson = [1, 0, 0, 0, 0, 0, 0, 0, 0, (byte)'{'];

        pair.Server.Host.SendMessage("p1", [1, 2, 3]);
        pair.Server.Host.SendMessage("p1", unknownTag);
        pair.Server.Host.SendMessage("p1", spawnWithBadJson);
        await pair.Settle();
        client.Update(Pair.Idle, 0.016);

        Assert.Equal(3, pair.Client("p1").Malformed);
        Assert.Null(client.Fault);
    }

    private static double[] PositionOf(GameHost host, string id)
    {
        var position = ((TransformComponent)host.Scene[id]![SceneComponents.Transform]!).Position;
        return [position.X, position.Y, position.Z];
    }

    private static float[] Place(float x, float y, float z) => [x, y, z, 0, 0, 0, 1, 1, 1];

    private static SceneEntity Plain(string id, double x, double y, double z) => new(
        id,
        id,
        components: new Dictionary<string, SceneComponent>
        {
            [SceneComponents.Transform] = new TransformComponent(new Vector3(x, y, z)),
        });
}
