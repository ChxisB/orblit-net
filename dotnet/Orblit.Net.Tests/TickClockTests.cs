namespace Orblit.Net.Tests;

public sealed class TickClockTests
{
    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(50);

    private TimeSpan _now = TimeSpan.Zero;

    private TickClock NewClock() => new(Period, () => _now);

    [Fact]
    public void TheFirstTickSetsTheScheduleAndMeasuresNothing()
    {
        var clock = NewClock();

        Assert.Equal(TimeSpan.Zero, clock.UntilNext());
        clock.Begin();
        Assert.Null(clock.Drift);
        Assert.Equal(Period, clock.UntilNext());
    }

    [Fact]
    public void ALateTickReportsItsDrift()
    {
        var clock = NewClock();
        clock.Begin();
        _now = TimeSpan.FromMilliseconds(58);

        Assert.Equal(TimeSpan.Zero, clock.UntilNext());
        clock.Begin();
        Assert.Equal(TimeSpan.FromMilliseconds(8), clock.Drift);
    }

    [Fact]
    public void ATickThatStartsEarlyIsNotLate()
    {
        var clock = NewClock();
        clock.Begin();
        _now = TimeSpan.FromMilliseconds(40);

        clock.Begin();

        Assert.Equal(TimeSpan.Zero, clock.Drift);
        Assert.Equal(TimeSpan.FromMilliseconds(60), clock.UntilNext());
    }

    [Fact]
    public void TheNextTickKeepsItsPhaseAfterALateOne()
    {
        var clock = NewClock();
        clock.Begin();
        _now = TimeSpan.FromMilliseconds(58);

        clock.Begin();

        Assert.Equal(TimeSpan.FromMilliseconds(42), clock.UntilNext());
    }

    [Fact]
    public void AnEarlyCallWaitsForWhatIsLeft()
    {
        var clock = NewClock();
        clock.Begin();
        _now = TimeSpan.FromMilliseconds(20);

        Assert.Equal(TimeSpan.FromMilliseconds(30), clock.UntilNext());
    }

    [Fact]
    public void AStallOfFivePeriodsRunsOneTickNotFive()
    {
        var clock = NewClock();
        clock.Begin();
        _now = Period * 6;

        clock.Begin();

        Assert.Equal(Period * 5, clock.Drift);
        Assert.Equal(Period, clock.UntilNext());
    }

    [Fact]
    public void TwoPeriodsBehindStillCatchesUpInPlace()
    {
        var clock = NewClock();
        clock.Begin();
        _now = Period * 3;

        clock.Begin();

        Assert.Equal(Period * 2, clock.Drift);
        Assert.Equal(TimeSpan.Zero, clock.UntilNext());
    }
}
