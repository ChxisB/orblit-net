using Orblit.Core;

namespace Orblit.Net.Tests;

public sealed class HostTimingTests : IAsyncLifetime
{
    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(50);

    private readonly Peer _peer = new(reverseRegistrationOrder: false);
    private readonly List<PhaseSummary> _overruns = [];
    private readonly List<LoopbackLink> _links = [];
    private readonly Dictionary<string, Tally> _received = [];
    private readonly NetHost _host;
    private TimeSpan _now = TimeSpan.Zero;

    public HostTimingTests() =>
        _host = new NetHost(new NetHostOptions
        {
            World = _peer.World,
            Set = _peer.Set,
            NetworkId = _peer.NetworkId,
            Clock = new TickClock(Period, () => _now),
            OnOverrun = _overruns.Add,
        });

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _host.Dispose();
        foreach (var link in _links)
        {
            await link.CloseAsync();
        }

        _peer.Dispose();
    }

    private sealed class Tally
    {
        public int Bytes;
    }

    private sealed class BrokenTransport : ITransport
    {
        public IDisposable Listen(Action<byte[]> onMessage, Action? onDone = null, Action<Exception>? onError = null) =>
            new Nothing();

        public void Send(byte[] message) => throw new InvalidOperationException("link is down");

        public Task CloseAsync() => Task.CompletedTask;

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private void Connect(string id)
    {
        var link = new LoopbackLink();
        _links.Add(link);
        var tally = new Tally();
        _received[id] = tally;
        link.Client.Listen(message => Interlocked.Add(ref tally.Bytes, message.Length));
        _host.AddClient(id, link.Host);
    }

    private Task Settle() => Links.SettleAsync(_links);

    private void Spawn(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var entity = _peer.World.CreateEntity();
            _host.Spawn(entity);
            _peer.World.Add<float>(entity, _peer.Position, [i, 0, 0]);
            if (i % 2 == 0)
            {
                _peer.World.Add<int>(entity, _peer.Health, [100]);
            }
        }
    }

    [Fact]
    public async Task TwoHundredEntitiesAndTwentyClientsAreEachCountedPerColumn()
    {
        Spawn(200);
        for (var i = 0; i < 20; i++)
        {
            Connect($"client-{i}");
        }

        _host.Publish();
        _host.Publish();
        await Settle();

        var bytes = _host.Summary().BytesSent;
        Assert.Equal(20, bytes.Count);
        foreach (var (id, sent) in bytes)
        {
            Assert.True(sent.Columns["Position"] > 0);
            Assert.True(sent.Columns["Health"] > 0);
            Assert.True(sent.Overhead > 0);

            // Columns and overhead must add up to every byte sent.
            Assert.Equal(_received[id].Bytes, sent.Total);
        }
    }

    [Fact]
    public async Task AColumnIsCountedAtItsComponentSizePerRow()
    {
        Spawn(4);
        Connect("a");
        _host.Publish();
        await Settle();

        var columns = _host.Summary().BytesSent["a"].Columns;
        Assert.Equal(4 * 12, columns["Position"]);
        Assert.Equal(2 * 4, columns["Health"]);
    }

    [Fact]
    public void AGameSystemTimedThroughTheHostSitsBesideTheHostStages()
    {
        Connect("a");
        _host.Phase("physics", () => _now += TimeSpan.FromMilliseconds(3));
        _host.Publish();

        var phases = _host.Summary().Phases;
        foreach (var name in new[] { "physics", "capture", "encode", "send" })
        {
            Assert.Contains(name, phases.Keys);
        }

        Assert.Equal(TimeSpan.FromMilliseconds(3), phases["physics"].Total);
    }

    [Fact]
    public void AHostWithNoClockStillTimesItsStagesAndReportsNoDrift()
    {
        using var bare = new NetHost(new NetHostOptions
        {
            World = _peer.World,
            Set = _peer.Set,
            NetworkId = _peer.NetworkId,
        });
        bare.Publish();

        var summary = bare.Summary();
        Assert.Equal(1, summary.Ticks);
        Assert.Contains("capture", summary.Phases.Keys);
        Assert.Equal(TimeSpan.Zero, summary.DriftMax);
    }

    [Fact]
    public void ASlowStagePastThePeriodCallsTheOverrunHook()
    {
        _host.Phase("physics", () => _now += TimeSpan.FromMilliseconds(60));
        _host.Publish();

        var overrun = Assert.Single(_overruns);
        Assert.Equal(TimeSpan.FromMilliseconds(60), overrun.Phases["physics"].Max);
    }

    [Fact]
    public void APublishThatThrowsStillClosesItsTick()
    {
        _host.AddClient("broken", new BrokenTransport());
        Assert.Throws<InvalidOperationException>(() => _host.Publish());
        Assert.Equal(1, _host.Summary().Ticks);

        _now += Period;
        Assert.Throws<InvalidOperationException>(() => _host.Publish());
        Assert.Equal(2, _host.Summary().Ticks);
    }

    [Fact]
    public async Task AClientThatLeavesIsForgotten()
    {
        Connect("a");
        _host.Publish();
        await Settle();
        Assert.Contains("a", _host.Summary().BytesSent.Keys);

        _host.RemoveClient("a");
        Assert.DoesNotContain("a", _host.Summary().BytesSent.Keys);
    }
}
