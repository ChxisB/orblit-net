namespace Orblit.Net.Tests;

/// <summary>
/// The C# package and the Dart package on one socket, each as host and as client. They share a
/// wire format, so a client must end up holding the world its host played.
/// </summary>
[Collection(DartPeer.Collection)]
public sealed class CrossJoinTests
{
    [Fact]
    public async Task ACSharpClientHoldsTheWorldADartHostPlayed()
    {
        Assert.SkipWhen(DartPeer.Unavailable is not null, DartPeer.Unavailable ?? "");
        await using var dart = await DartPeer.Host();
        // Registered in the opposite order to the Dart side, so local ids differ.
        using var peer = new Peer(reverseRegistrationOrder: true);
        var transport = await SocketTransport.ConnectAsync(dart.Address, DartPeer.Patience);
        try
        {
            using var client = new NetClient(new NetClientOptions
            {
                World = peer.World,
                Set = peer.Set,
                NetworkId = peer.NetworkId,
                Transport = transport,
            });
            var played = CrossScene.Parse(await dart.Finished());
            await Wait.Until(() => client.LastAppliedTick >= CrossScene.Ticks, "the last tick to reach the client");

            // Ids 1, 3 and 4 are left. Two empty worlds would also be equal.
            Assert.Equal(3, played.Count);
            Assert.Equal(played, CrossScene.Describe(peer, client.EntityFor));
        }
        finally
        {
            await transport.CloseAsync();
        }
    }

    [Fact]
    public async Task ADartClientHoldsTheWorldACSharpHostPlayed()
    {
        Assert.SkipWhen(DartPeer.Unavailable is not null, DartPeer.Unavailable ?? "");
        await using var server = SocketServer.Bind();
        var arrived = new TaskCompletionSource<SocketTransport>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listening = server.Listen(transport => arrived.TrySetResult(transport));
        using var scene = new CrossHost();
        var joining = DartPeer.Client(server.Url, CrossScene.Ticks);

        var transport = await arrived.Task.WaitAsync(DartPeer.Patience, TestContext.Current.CancellationToken);
        try
        {
            scene.Net.AddClient("a", transport);
            for (var tick = 1; tick <= CrossScene.Ticks; tick++)
            {
                scene.Step(tick);
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }

            // A delta that does not follow what the client holds is dropped, and the next tick is
            // a fresh baseline. Idle ticks let the last change arrive.
            while (!joining.IsCompleted)
            {
                scene.Net.Publish();
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }

            Assert.Equal(3, scene.Describe().Count);
            Assert.Equal(scene.Describe(), CrossScene.Parse(await joining));
        }
        finally
        {
            await transport.CloseAsync();
        }
    }
}
