namespace Orblit.Net.Tests;

public sealed class LoadArgsTests
{
    [Fact]
    public void NoArgumentsGiveTheDefaults()
    {
        var run = LoadArgs.Parse([]);

        Assert.Equal("walk", run.Script);
        Assert.Equal(50, run.Options.Clients);
        Assert.Equal(200, run.Options.Ticks);
        Assert.Equal(TimeSpan.FromMilliseconds(50), run.Options.Period);
        Assert.False(run.Options.Sockets);
        Assert.Null(run.Options.ServerWork);
    }

    [Fact]
    public void AValueFollowsAFlagOrSitsAfterAnEqualsSign()
    {
        var spaced = LoadArgs.Parse(["--clients", "8", "--seconds", "2"]);
        var joined = LoadArgs.Parse(["--clients=8", "--seconds=2"]);

        Assert.Equal(8, spaced.Options.Clients);
        Assert.Equal(40, spaced.Options.Ticks);
        Assert.Equal(8, joined.Options.Clients);
        Assert.Equal(40, joined.Options.Ticks);
    }

    [Fact]
    public void HzSetsThePeriodAndTheTickCount()
    {
        var run = LoadArgs.Parse(["--hz", "10", "--seconds", "3"]);

        Assert.Equal(30, run.Options.Ticks);
        Assert.Equal(TimeSpan.FromMilliseconds(100), run.Options.Period);
    }

    [Fact]
    public void SocketsAndASlowSystemSwitchOn()
    {
        var run = LoadArgs.Parse(["--sockets", "--slow-ms", "5"]);

        Assert.True(run.Options.Sockets);
        Assert.NotNull(run.Options.ServerWork);
    }

    [Fact]
    public void ACellSizeTurnsInterestOnAndNoneLeavesItOff()
    {
        Assert.Null(LoadArgs.Parse([]).Options.CellSize);
        Assert.Equal(25, LoadArgs.Parse(["--cell", "25"]).Options.CellSize);
        Assert.Throws<FormatException>(() => LoadArgs.Parse(["--cell", "0"]));
    }

    [Fact]
    public void ASlowSystemOfZeroIsNone()
    {
        Assert.Null(LoadArgs.Parse(["--slow-ms", "0"]).Options.ServerWork);
    }

    [Fact]
    public void WhatCannotRunIsRefusedWithAMessage()
    {
        string[][] refused =
        [
            ["--hz", "0"],
            ["--hz", "-5"],
            ["--clients", "0"],
            ["--clients", "many"],
            ["--slow-ms", "-5"],
            ["--script", "jump"],
            ["--nope"],
            ["--clients"],
            ["--sockets=yes"],
            ["--seconds", "2000000000", "--hz", "20"],
        ];

        foreach (var args in refused)
        {
            Assert.Throws<FormatException>(() => LoadArgs.Parse(args));
        }
    }

    [Fact]
    public void APeriodUnderAMicrosecondIsRefusedByTheOptions()
    {
        Assert.ThrowsAny<ArgumentException>(() => LoadArgs.Parse(["--hz", "2000000"]));
    }
}
