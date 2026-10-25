namespace Orblit.Net.Tests;

public sealed class LoopbackSessionTests : IAsyncLifetime
{
    private readonly Rig _rig = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private ulong Spawn(float x = 0)
    {
        var entity = _rig.Host.World.CreateEntity();
        _rig.NetHost.Spawn(entity);
        _rig.Host.World.Add<float>(entity, _rig.Host.Position, [x, 0, 0]);
        return entity;
    }

    [Fact]
    public async Task AnEntitySpawnedOnTheHostAppearsOnTheClient()
    {
        var entity = _rig.Host.World.CreateEntity();
        var networkId = _rig.NetHost.Spawn(entity);
        _rig.Host.World.Add<float>(entity, _rig.Host.Position, [4, 5, 6]);

        _rig.NetHost.Publish();
        await _rig.Settle();

        var replica = _rig.NetClient.EntityFor(networkId);
        Assert.NotNull(replica);

        // Component bytes cross unchanged, despite different local ids.
        Assert.Equal([4f, 5f, 6f], Column.Floats(_rig.Client.World, replica.Value, _rig.Client.Position));
    }

    [Fact]
    public async Task LaterTicksUpdateInPlaceRatherThanRespawning()
    {
        var entity = _rig.Host.World.CreateEntity();
        var networkId = _rig.NetHost.Spawn(entity);
        _rig.Host.World.Add<float>(entity, _rig.Host.Position, [0, 0, 0]);
        _rig.NetHost.Publish();
        await _rig.Settle();
        var replica = _rig.NetClient.EntityFor(networkId);

        Column.SetFloat(_rig.Host.World, entity, _rig.Host.Position, 0, 12);
        _rig.NetHost.Publish();
        await _rig.Settle();

        Assert.Equal(replica, _rig.NetClient.EntityFor(networkId));
        Assert.Equal(12f, Column.Floats(_rig.Client.World, replica!.Value, _rig.Client.Position)[0]);
    }

    [Fact]
    public async Task AComponentAddedLaterAppearsOnTheReplica()
    {
        var entity = _rig.Host.World.CreateEntity();
        var networkId = _rig.NetHost.Spawn(entity);
        _rig.Host.World.Add(entity, _rig.Host.Position);
        _rig.NetHost.Publish();
        await _rig.Settle();

        var replica = _rig.NetClient.EntityFor(networkId)!.Value;
        Assert.False(_rig.Client.World.Has(replica, _rig.Client.Health));

        _rig.Host.World.Add<int>(entity, _rig.Host.Health, [55]);
        _rig.NetHost.Publish();
        await _rig.Settle();

        Assert.True(_rig.Client.World.Has(replica, _rig.Client.Health));
        Assert.Equal(55, Column.Int(_rig.Client.World, replica, _rig.Client.Health));
    }

    [Fact]
    public async Task DespawningOnTheHostRemovesTheReplica()
    {
        var entity = _rig.Host.World.CreateEntity();
        var networkId = _rig.NetHost.Spawn(entity);
        _rig.Host.World.Add(entity, _rig.Host.Position);
        _rig.NetHost.Publish();
        await _rig.Settle();
        Assert.NotNull(_rig.NetClient.EntityFor(networkId));

        _rig.NetHost.Despawn(entity);
        _rig.NetHost.Publish();
        await _rig.Settle();

        Assert.Null(_rig.NetClient.EntityFor(networkId));
        Assert.Equal(0, _rig.NetClient.EntityCount);
    }

    [Fact]
    public async Task TheClientAcknowledgesSoTheHostSwitchesToDeltas()
    {
        Spawn();
        _rig.NetHost.Publish();
        await _rig.Settle();
        Assert.Equal(1, _rig.NetClient.LastAppliedTick);

        // The ack has arrived, so this publish is a delta against tick 1. With nothing changed it
        // should carry no rows at all.
        var sent = new List<byte[]>();
        using var probe = _rig.Link.Client.Listen(sent.Add);

        _rig.NetHost.Publish();
        await _rig.Settle();

        var decoded = SnapshotCodec.Decode(Assert.Single(sent));
        Assert.True(decoded.IsDelta);
        Assert.Equal(1, decoded.BaselineTick);
        Assert.Empty(decoded.Groups);
    }

    [Fact]
    public async Task ManyEntitiesReplicateInOnePublish()
    {
        const int count = 500;
        for (var i = 0; i < count; i++)
        {
            Spawn(i);
        }

        _rig.NetHost.Publish();
        await _rig.Settle();

        Assert.Equal(count, _rig.NetClient.EntityCount);
        var replica = _rig.NetClient.EntityFor(count)!.Value;
        Assert.Equal(count - 1f, Column.Floats(_rig.Client.World, replica, _rig.Client.Position)[0]);
    }
}
