using Orblit.Core;

namespace Orblit.Net.Tests;

public sealed class ActiveCellsHostTests
{
    [Fact]
    public async Task AnAiSystemStepsOnlyTheEntitiesInActiveCells()
    {
        using var peer = new Peer(reverseRegistrationOrder: false);
        var grid = new Grid(10);
        var interest = new GridInterest(peer.Set, peer.Position, grid);
        using var host = new NetHost(new NetHostOptions
        {
            World = peer.World,
            Set = peer.Set,
            NetworkId = peer.NetworkId,
            Interest = interest,
        });
        var now = TimeSpan.Zero;
        var active = new ActiveCells(grid, 1, TimeSpan.FromSeconds(5), () => now);
        var link = new LoopbackLink();
        host.AddClient("a", link.Host);

        ulong Spawn(float x, string? owner = null)
        {
            var entity = peer.World.CreateEntity();
            peer.World.Add<float>(entity, peer.Position, [x, 0, 0]);
            return host.Spawn(entity, owner);
        }

        var avatar = Spawn(1, "a");
        var near = Spawn(15);
        var far = Spawn(95);

        // Wanders each guard once, if its cell is active.
        List<ulong> Step() =>
        [
            .. new[] { near, far }.Where(id =>
                active.IsActiveAt(Column.Floats(peer.World, host.EntityFor(id)!.Value, peer.Position)[0], 0)),
        ];

        // The focus is as of the last publish, so the first tick publishes before the system looks.
        host.Publish();
        active.Update(interest.FocusCells);
        Assert.Equal([near], Step());

        // The client's avatar walks away. The old cells hold for five seconds.
        Column.SetFloat(peer.World, host.EntityFor(avatar)!.Value, peer.Position, 0, 90);
        host.Publish();
        now = TimeSpan.FromSeconds(3);
        active.Update(interest.FocusCells);
        Assert.Equal([near, far], Step());

        now = TimeSpan.FromSeconds(9);
        active.Update(interest.FocusCells);
        Assert.Equal([far], Step());

        await link.CloseAsync();
    }
}
