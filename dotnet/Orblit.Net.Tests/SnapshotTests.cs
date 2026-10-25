using System.Buffers.Binary;
using Orblit.Core;

namespace Orblit.Net.Tests;

public sealed class SnapshotTests : IDisposable
{
    private readonly Peer _host = new(reverseRegistrationOrder: false);
    private readonly SnapshotCapture _capture;

    public SnapshotTests() => _capture = new SnapshotCapture(_host.World, _host.Set, _host.NetworkId);

    public void Dispose()
    {
        _capture.Dispose();
        _host.Dispose();
    }

    [Fact]
    public void TheNetworkIdMayNotBeInsideTheReplicationSet()
    {
        var set = new ReplicationSet([_host.Position, _host.NetworkId]);

        Assert.Throws<ArgumentException>(() => new SnapshotCapture(_host.World, set, _host.NetworkId));
    }

    [Fact]
    public void CapturesOnlyReplicatedEntities()
    {
        var replicated = _host.World.CreateEntity();
        _host.World.Add<ulong>(replicated, _host.NetworkId, [7]);
        _host.World.Add<float>(replicated, _host.Position, [1, 2, 3]);

        var ignored = _host.World.CreateEntity();
        _host.World.Add<float>(ignored, _host.Position, [9, 9, 9]);

        var snapshot = _capture.Capture(1);

        Assert.Equal(1, snapshot.EntityCount);
        Assert.Equal([7UL], snapshot.NetworkIds);
    }

    [Fact]
    public void AFullSnapshotRoundTrips()
    {
        var entity = _host.World.CreateEntity();
        _host.World.Add<ulong>(entity, _host.NetworkId, [42]);
        _host.World.Add<float>(entity, _host.Position, [1, 2, 3]);
        _host.World.Add<int>(entity, _host.Health, [80]);

        var decoded = SnapshotCodec.Decode(SnapshotCodec.EncodeFull(_capture.Capture(5)));

        Assert.Equal(5, decoded.Tick);
        Assert.False(decoded.IsDelta);
        var group = Assert.Single(decoded.Groups);
        Assert.Equal([42UL], group.NetworkIds);
        // Health sorts before Position, so the row is health then position.
        var row = group.RowAt(0);
        Assert.Equal(80, BinaryPrimitives.ReadInt32LittleEndian(row));
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(row[4..]));
        Assert.Equal(3f, BinaryPrimitives.ReadSingleLittleEndian(row[12..]));
    }

    [Fact]
    public void ADeltaCarriesOnlyWhatChanged()
    {
        var entities = new List<ulong>();
        for (var i = 0; i < 5; i++)
        {
            var entity = _host.World.CreateEntity();
            _host.World.Add<ulong>(entity, _host.NetworkId, [(ulong)(i + 1)]);
            _host.World.Add<float>(entity, _host.Position, [i * 1f, 0, 0]);
            entities.Add(entity);
        }

        var baseline = _capture.Capture(1);
        Assert.True(_host.World.TryGetFloat32(entities[2], _host.Position, out var position));
        position[1] = 99;

        var decoded = SnapshotCodec.Decode(SnapshotCodec.EncodeDelta(_capture.Capture(2), baseline));

        Assert.True(decoded.IsDelta);
        Assert.Equal(1, decoded.BaselineTick);
        // Only the entity that moved should be on the wire.
        Assert.Equal([3UL], decoded.Groups[0].NetworkIds);
    }

    [Fact]
    public void ADeltaReportsEntitiesThatDisappeared()
    {
        var entity = _host.World.CreateEntity();
        _host.World.Add<ulong>(entity, _host.NetworkId, [11]);
        _host.World.Add(entity, _host.Position);

        var baseline = _capture.Capture(1);
        _host.World.DestroyEntity(entity);

        var decoded = SnapshotCodec.Decode(SnapshotCodec.EncodeDelta(_capture.Capture(2), baseline));

        Assert.Equal([11UL], decoded.Despawned);
    }

    [Fact]
    public void AForeignMessageIsRefused() =>
        Assert.Throws<SnapshotFormatException>(() => SnapshotCodec.Decode(new byte[64]));
}
