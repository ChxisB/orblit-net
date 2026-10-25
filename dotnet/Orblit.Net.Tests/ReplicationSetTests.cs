namespace Orblit.Net.Tests;

public sealed class ReplicationSetTests
{
    [Fact]
    public void OrdersByNameSoBothEndsAgreeWithoutAHandshake()
    {
        using var a = new Peer(reverseRegistrationOrder: false);
        using var b = new Peer(reverseRegistrationOrder: true);

        Assert.Equal(["Health", "Position", "Velocity"], a.Set.Components.Select(c => c.Type.Name));
        Assert.Equal(["Health", "Position", "Velocity"], b.Set.Components.Select(c => c.Type.Name));
        // The same component must occupy the same bit on both peers, even though the local ids differ.
        Assert.Equal(a.Set.BitOf(a.Position), b.Set.BitOf(b.Position));
        Assert.NotEqual(a.Position.Id, b.Position.Id);
    }

    [Fact]
    public void RefusesADuplicateComponent()
    {
        using var peer = new Peer(reverseRegistrationOrder: false);

        Assert.Throws<ArgumentException>(() => new ReplicationSet([peer.Position, peer.Position]));
    }

    [Fact]
    public void ComputesAStrideFromAMask()
    {
        using var peer = new Peer(reverseRegistrationOrder: false);

        var mask = peer.Set.MaskOfIds([peer.Position.Id, peer.Health.Id]);

        Assert.Equal(12 + 4, peer.Set.StrideOf(mask));
    }
}
