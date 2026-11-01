using Orblit.Core;

namespace Orblit.Net.Tests;

/// <summary>Velocity is the client's to drive. Position is the authority's answer about where that got them.</summary>
public sealed class OwnershipTests : IAsyncLifetime
{
    private readonly Rig _rig = new(includeOwner: true, ownerWritable: ["Velocity"]);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private async Task<ulong> SpawnOwned(string? owner = "player-1")
    {
        var entity = _rig.Host.World.CreateEntity();
        var networkId = _rig.NetHost.Spawn(entity, owner);
        _rig.Host.World.Add<float>(entity, _rig.Host.Position, [0, 0, 0]);
        _rig.Host.World.Add<float>(entity, _rig.Host.Velocity, [0, 0, 0]);
        _rig.NetHost.Publish();
        await _rig.Settle();
        return networkId;
    }

    [Fact]
    public async Task TheOwnerMayWriteAnOwnerWritableComponent()
    {
        var networkId = await SpawnOwned();

        _rig.NetClient.SendInput(Column.Write(networkId, _rig.Client.Velocity, 3, 0, 0));
        await _rig.Settle();

        Assert.Equal(1, _rig.NetHost.AcceptedInputs);
        Assert.Empty(_rig.NetHost.Rejections);

        // The authority's own world now carries what the client asked for.
        var authoritative = _rig.NetHost.EntityFor(networkId)!.Value;
        Assert.Equal([3f, 0f, 0f], Column.Floats(_rig.Host.World, authoritative, _rig.Host.Velocity));
    }

    [Fact]
    public async Task AClientMayNotWriteAComponentTheAuthorityReserves()
    {
        var networkId = await SpawnOwned();

        _rig.NetClient.SendInput(Column.Write(networkId, _rig.Client.Position, 999, 0, 0));
        await _rig.Settle();

        Assert.Equal(0, _rig.NetHost.AcceptedInputs);
        Assert.Equal(1, _rig.NetHost.Rejections[InputRejection.NotWritable]);
        Assert.Equal(
            [0f, 0f, 0f],
            Column.Floats(_rig.Host.World, _rig.NetHost.EntityFor(networkId)!.Value, _rig.Host.Position));
    }

    [Fact]
    public async Task AClientMayNotWriteAnEntityItDoesNotOwn()
    {
        var networkId = await SpawnOwned(owner: null);

        _rig.NetClient.SendInput(Column.Write(networkId, _rig.Client.Velocity, 3, 0, 0));
        await _rig.Settle();

        Assert.Equal(0, _rig.NetHost.AcceptedInputs);
        Assert.Equal(1, _rig.NetHost.Rejections[InputRejection.NotOwned]);
    }

    [Fact]
    public async Task AnUnknownEntityIsRefusedRatherThanCrashing()
    {
        await SpawnOwned();

        _rig.NetClient.SendInput(Column.Write(9999, _rig.Client.Velocity, 1, 0, 0));
        await _rig.Settle();

        Assert.Equal(1, _rig.NetHost.Rejections[InputRejection.UnknownEntity]);
    }

    [Fact]
    public async Task AMalformedPayloadIsCountedNotThrown()
    {
        var networkId = await SpawnOwned();

        // A row claiming velocity but carrying too few bytes.
        var bit = _rig.Host.Set.BitOf(_rig.Host.Velocity)!.Value;
        _rig.Link.Client.Send(InputCodec.Encode(new InputMessage(0, [new InputEntry(networkId, 1UL << bit, new byte[4])])));
        await _rig.Settle();

        Assert.Equal(1, _rig.NetHost.Rejections[InputRejection.Malformed]);
        Assert.Equal(0, _rig.NetHost.AcceptedInputs);
    }

    [Fact]
    public async Task OwnershipReplicatesSoAClientKnowsWhatItDrives()
    {
        var networkId = await SpawnOwned();

        var replica = _rig.NetClient.EntityFor(networkId)!.Value;

        // The first client is index 1. Zero means the authority.
        Assert.Equal(1u, Column.UInt(_rig.Client.World, replica, _rig.Client.Owner));
    }

    [Fact]
    public async Task ADepartingClientLosesWhatItOwned()
    {
        var networkId = await SpawnOwned();
        Assert.Equal("player-1", _rig.NetHost.OwnerOf(networkId));

        _rig.NetHost.RemoveClient("player-1");

        // Ownership must not survive under a reconnectable name.
        Assert.Null(_rig.NetHost.OwnerOf(networkId));
    }

    [Fact]
    public async Task WritingAnUnreplicatedComponentIsAProgrammingError()
    {
        var networkId = await SpawnOwned();
        var stray = _rig.Client.World.RegisterComponent("Stray", ComponentKind.Int32);

        Assert.Throws<ArgumentException>(() => _rig.NetClient.SendInput(
            new Dictionary<ulong, IReadOnlyDictionary<ComponentType, byte[]>>
            {
                [networkId] = new Dictionary<ComponentType, byte[]> { [stray] = [1, 0, 0, 0] },
            }));
    }
}
