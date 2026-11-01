namespace Orblit.Net.Tests;

public sealed class InterpolationTests : IAsyncLifetime
{
    private readonly Rig _rig;
    private TimeSpan _now = TimeSpan.Zero;

    public InterpolationTests() =>
        _rig = new Rig(interpolationDelay: TimeSpan.FromMilliseconds(100), clock: () => _now);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private static TimeSpan Seconds(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public async Task NothingIsWrittenUntilTheWorldIsAdvanced()
    {
        var entity = _rig.Host.World.CreateEntity();
        var networkId = _rig.NetHost.Spawn(entity);
        _rig.Host.World.Add<float>(entity, _rig.Host.Position, [10, 0, 0]);
        _rig.NetHost.Publish();
        await _rig.Settle();

        // An interpolating client shows the past, not the newest message the moment it lands.
        Assert.True(_rig.NetClient.IsInterpolating);
        Assert.Null(_rig.NetClient.EntityFor(networkId));

        _rig.NetClient.Advance();
        Assert.NotNull(_rig.NetClient.EntityFor(networkId));
    }

    [Fact]
    public async Task AFloatComponentBlendsBetweenTwoStates()
    {
        var entity = _rig.Host.World.CreateEntity();
        var networkId = _rig.NetHost.Spawn(entity);
        _rig.Host.World.Add<float>(entity, _rig.Host.Position, [0, 0, 0]);
        _rig.NetHost.Publish();
        await _rig.Settle();

        _now = Seconds(0.1);
        Column.SetFloat(_rig.Host.World, entity, _rig.Host.Position, 0, 10);
        _rig.NetHost.Publish();
        await _rig.Settle();

        // Render time is now 0.2 - 0.1 = 0.1, exactly the second state.
        _now = Seconds(0.2);
        _rig.NetClient.Advance();
        var replica = _rig.NetClient.EntityFor(networkId)!.Value;
        Assert.Equal(10f, Column.Floats(_rig.Client.World, replica, _rig.Client.Position)[0]);

        // Halfway between the two arrivals: halfway between the two positions.
        _now = Seconds(0.15);
        _rig.NetClient.Advance();
        Assert.Equal(5f, Column.Floats(_rig.Client.World, replica, _rig.Client.Position)[0], 1e-4f);
    }

    [Fact]
    public async Task ANonFloatComponentTakesTheEarlierValueNeverABlend()
    {
        var entity = _rig.Host.World.CreateEntity();
        var networkId = _rig.NetHost.Spawn(entity);
        _rig.Host.World.Add<float>(entity, _rig.Host.Position, [0, 0, 0]);
        _rig.Host.World.Add<int>(entity, _rig.Host.Health, [100]);
        _rig.NetHost.Publish();
        await _rig.Settle();

        _now = Seconds(0.1);
        Column.SetInt(_rig.Host.World, entity, _rig.Host.Health, 50);
        _rig.NetHost.Publish();
        await _rig.Settle();

        _now = Seconds(0.15);
        _rig.NetClient.Advance();
        var replica = _rig.NetClient.EntityFor(networkId)!.Value;

        // Half of a hundred and fifty is not a health value.
        Assert.Equal(100, Column.Int(_rig.Client.World, replica, _rig.Client.Health));
    }

    [Fact]
    public async Task PastTheNewestStateItHoldsRatherThanExtrapolating()
    {
        var entity = _rig.Host.World.CreateEntity();
        var networkId = _rig.NetHost.Spawn(entity);
        _rig.Host.World.Add<float>(entity, _rig.Host.Position, [7, 0, 0]);
        _rig.NetHost.Publish();
        await _rig.Settle();

        _now = Seconds(10);
        _rig.NetClient.Advance();
        var replica = _rig.NetClient.EntityFor(networkId)!.Value;

        // The authority has gone quiet. Inventing motion would be a guess presented as fact.
        Assert.Equal(7f, Column.Floats(_rig.Client.World, replica, _rig.Client.Position)[0]);
    }
}
