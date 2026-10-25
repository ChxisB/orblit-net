using Orblit.Core;

namespace Orblit.Net.Tests;

public sealed class SessionMessageTests
{
    [Fact]
    public void AJoinCarriesItsCredentials()
    {
        var message = SessionMessage.EncodeJoin([1, 2, 3]);

        Assert.Equal([1, 2, 3], SessionMessage.DecodeJoin(message));
    }

    [Fact]
    public void AJoinWithNoCredentialsIsStillAJoin()
    {
        Assert.Empty(SessionMessage.DecodeJoin(SessionMessage.EncodeJoin([]))!);
    }

    [Fact]
    public void AGoodbyeCarriesItsReason()
    {
        Assert.Equal("alreadyLive", SessionMessage.DecodeGoodbye(SessionMessage.EncodeGoodbye("alreadyLive")));
    }

    [Fact]
    public void EachMessageDecodesAsNullForEveryOtherKind()
    {
        var join = SessionMessage.EncodeJoin([1]);
        var goodbye = SessionMessage.EncodeGoodbye("x");
        var ack = Ack.Encode(7);
        var input = InputCodec.Encode(new InputMessage(1, []));

        Assert.Null(SessionMessage.DecodeGoodbye(join));
        Assert.Null(SessionMessage.DecodeJoin(goodbye));
        foreach (var other in new[] { ack, input, [], new byte[3] })
        {
            Assert.Null(SessionMessage.DecodeJoin(other));
            Assert.Null(SessionMessage.DecodeGoodbye(other));
        }

        Assert.Null(Ack.Decode(join));
        Assert.Null(InputCodec.Decode(join));
        Assert.Null(InputCodec.Decode(goodbye));
    }

    [Fact]
    public async Task AClientReportsAGoodbyeOnceAndKeepsItsWorld()
    {
        using var world = new World();
        var networkId = world.RegisterComponent("NetworkId", ComponentKind.Int64);
        var link = new LoopbackLink();
        var reasons = new List<string>();
        using var client = new NetClient(new NetClientOptions
        {
            World = world,
            Set = new ReplicationSet([]),
            NetworkId = networkId,
            Transport = link.Client,
            OnGoodbye = reasons.Add,
        });

        link.Host.Send(SessionMessage.EncodeGoodbye("alreadyLive"));
        await link.IdleAsync();
        await link.CloseAsync();

        Assert.Equal(["alreadyLive"], reasons);
    }
}
