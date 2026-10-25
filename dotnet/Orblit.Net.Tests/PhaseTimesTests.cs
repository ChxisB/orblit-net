namespace Orblit.Net.Tests;

public sealed class PhaseTimesTests
{
    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(50);

    private readonly List<PhaseSummary> _overruns = [];
    private readonly PhaseTimes _times;
    private TimeSpan _now = TimeSpan.Zero;

    public PhaseTimesTests()
    {
        var clock = new TickClock(Period, () => _now);
        _times = new PhaseTimes(clock, _overruns.Add, capacity: 3);
    }

    private static TimeSpan Ms(int n) => TimeSpan.FromMilliseconds(n);

    private TimeSpan Advance(int ms) => _now += Ms(ms);

    /// <summary>Runs one tick whose phases take the given milliseconds, then closes it.</summary>
    private void Tick(params (string Name, int Ms)[] phases)
    {
        foreach (var (name, ms) in phases)
        {
            _times.Phase(name, () => Advance(ms));
        }

        _times.Finish();
    }

    [Fact]
    public void SummarisesTickLengthAndEachPhase()
    {
        Tick(("capture", 2), ("encode", 4));
        Advance(44);
        Tick(("capture", 4), ("encode", 8));

        var summary = _times.Summary();
        Assert.Equal(2, summary.Ticks);
        Assert.Equal(Ms(9), summary.TickAverage);
        Assert.Equal(Ms(12), summary.TickMax);

        var capture = summary.Phases["capture"];
        Assert.Equal(Ms(6), capture.Total);
        Assert.Equal(Ms(3), capture.Average);
        Assert.Equal(Ms(4), capture.Max);
        Assert.Equal(Ms(12), summary.Phases["encode"].Total);
    }

    [Fact]
    public void APhaseRunTwiceInOneTickAddsUp()
    {
        _times.Phase("encode", () => Advance(3));
        _times.Phase("encode", () => Advance(5));
        _times.Finish();

        var encode = _times.Summary().Phases["encode"];
        Assert.Equal(Ms(8), encode.Total);
        Assert.Equal(Ms(8), encode.Max);
    }

    [Fact]
    public void APhaseThatTakesNoMeasurableTimeIsStillListed()
    {
        _times.Phase("instant", () => { });
        _times.Finish();

        Assert.Equal(TimeSpan.Zero, _times.Summary().Phases["instant"].Max);
    }

    [Fact]
    public void ReturnsWhatTheBodyReturns()
    {
        Assert.Equal(7, _times.Phase("x", () => 7));
    }

    [Fact]
    public void ReportsDriftWhichTheFirstTickDoesNotHave()
    {
        Tick(("a", 1));
        _now = Ms(58);
        Tick(("a", 1));

        var summary = _times.Summary();
        Assert.Equal(Ms(8), summary.DriftAverage);
        Assert.Equal(Ms(8), summary.DriftMax);
    }

    [Fact]
    public void KeepsOnlyTheLastCapacityTicks()
    {
        for (var i = 1; i <= 5; i++)
        {
            Tick(("a", i));
            Advance(50 - i);
        }

        var summary = _times.Summary();
        Assert.Equal(3, summary.Ticks);
        Assert.Equal(Ms(3 + 4 + 5), summary.Phases["a"].Total);
    }

    [Fact]
    public void LastLimitsTheWindow()
    {
        for (var i = 1; i <= 3; i++)
        {
            Tick(("a", i));
            Advance(50 - i);
        }

        var summary = _times.Summary(last: 1);
        Assert.Equal(1, summary.Ticks);
        Assert.Equal(Ms(3), summary.TickMax);
    }

    [Fact]
    public void ATickOverThePeriodCallsTheHookOnceInFiveSeconds()
    {
        Tick(("slow", 60));
        Assert.Single(_overruns);
        Assert.Equal(Ms(60), _overruns.Single().Phases["slow"].Max);

        Advance(40);
        Tick(("slow", 60));
        Assert.Single(_overruns);

        _now += TimeSpan.FromSeconds(5);
        Tick(("slow", 60));
        Assert.Equal(2, _overruns.Count);
    }

    [Fact]
    public void ATickInsideThePeriodDoesNotCallTheHook()
    {
        Tick(("a", 49));

        Assert.Empty(_overruns);
    }
}
