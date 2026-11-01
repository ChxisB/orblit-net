namespace Orblit.Net.Tests;

public sealed class LoadTests
{
    private static readonly LoadScript Walk = LoadScripts.All["walk"];

    private static LoadOptions Basic(int clients = 8, int ticks = 10, string script = "walk", int periodMs = 20) =>
        new(clients, ticks, LoadScripts.All[script]) { Period = TimeSpan.FromMilliseconds(periodMs) };

    private static int Total(LoadReport report) => report.Summary.BytesSent.Values.Sum(sent => sent.Total);

    private static Task<LoadReport> Run(LoadOptions options) =>
        LoadHarness.RunAsync(options, TestContext.Current.CancellationToken);

    [Fact]
    public async Task EveryClientIsSentBytesInThePositionColumnUnderWalk()
    {
        var report = await Run(Basic());

        Assert.Equal(8, report.Summary.BytesSent.Count);
        foreach (var sent in report.Summary.BytesSent.Values)
        {
            Assert.True(sent.Columns["Position"] > 0);
        }
    }

    [Fact]
    public async Task EveryInputAClientSendsBeforeTheLastTickIsApplied()
    {
        var report = await Run(Basic());

        // A pool thread may deliver the last tick's inputs before or after the report is built, so
        // the count is either side of that line.
        Assert.InRange(report.Accepted, 8 * 9, 8 * 10);
        Assert.Equal(0, report.Rejected);
    }

    [Fact]
    public async Task OverRealSocketsEveryClientIsSentBytesAndHeard()
    {
        var report = await Run(new LoadOptions(4, 10, Walk) { Period = TimeSpan.FromMilliseconds(20), Sockets = true });

        Assert.Equal(4, report.Summary.BytesSent.Count);
        foreach (var sent in report.Summary.BytesSent.Values)
        {
            Assert.True(sent.Columns["Position"] > 0);
        }

        Assert.True(report.Accepted > 0);
        Assert.Equal(0, report.Rejected);
    }

    [Fact]
    public async Task StillSendsLessThanWalk()
    {
        var walk = await Run(Basic());
        var still = await Run(Basic(script: "still"));

        Assert.True(Total(still) < Total(walk));
    }

    [Fact]
    public async Task ScatteredClientsAreSentLessWhenEachSeesItsOwnCell()
    {
        var everyone = await Run(Basic(clients: 40, script: "scatter"));
        var nearby = await Run(new LoadOptions(40, 10, LoadScripts.All["scatter"])
        {
            Period = TimeSpan.FromMilliseconds(20),
            CellSize = 50,
        });

        Assert.True(Total(nearby) < Total(everyone) / 2);
        Assert.Equal(0, nearby.Rejected);
    }

    [Fact]
    public async Task ASlowSystemIsThePhaseWithTheLargestAverage()
    {
        var report = await Run(new LoadOptions(8, 10, Walk)
        {
            Period = TimeSpan.FromMilliseconds(20),
            ServerWork = _ => Thread.Sleep(5),
        });

        var worst = report.Summary.Phases.MaxBy(phase => phase.Value.Average);
        Assert.Equal("system", worst.Key);
    }

    [Fact]
    public async Task APeriodTheHostCannotKeepIsReportedAsAnOverrun()
    {
        var slow = await Run(new LoadOptions(8, 10, Walk)
        {
            Period = TimeSpan.FromMilliseconds(2),
            ServerWork = _ => Thread.Sleep(6),
        });
        var easy = await Run(Basic(ticks: 4, periodMs: 100));

        Assert.True(slow.Overran);
        Assert.False(easy.Overran);
    }

    [Fact]
    public async Task DescribeListsTheWorstPhaseFirstAndTheBytesPerClient()
    {
        var report = await Run(new LoadOptions(8, 10, Walk)
        {
            Period = TimeSpan.FromMilliseconds(20),
            ServerWork = _ => Thread.Sleep(3),
        });

        var lines = report.Describe().Split('\n');
        var first = lines[Array.IndexOf(lines, "phase (per tick)") + 1];
        Assert.StartsWith("system", first.Trim(), StringComparison.Ordinal);
        Assert.Contains(lines, line => line.StartsWith("  Position", StringComparison.Ordinal));
        Assert.StartsWith("8 clients", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunThatFinishesInTimeExitsZeroAnOverrunOneARefusalTwo()
    {
        var easy = await Run(Basic(ticks: 3, periodMs: 100));
        var refused = easy with { Rejected = 1 };
        var overran = await Run(new LoadOptions(8, 4, Walk)
        {
            Period = TimeSpan.FromMilliseconds(2),
            ServerWork = _ => Thread.Sleep(6),
        });

        Assert.Equal(0, easy.ExitCode);
        Assert.Equal(1, overran.ExitCode);
        Assert.Equal(2, refused.ExitCode);
    }

    [Fact]
    public async Task ARunThatStartsTicksLateSaysSo()
    {
        var report = await Run(new LoadOptions(8, 10, Walk)
        {
            Period = TimeSpan.FromMilliseconds(2),
            ServerWork = _ => Thread.Sleep(8),
        });

        Assert.True(report.Behind);
        Assert.Contains("behind schedule", report.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsThatCannotRunAreRefused()
    {
        Assert.ThrowsAny<ArgumentException>(() => new LoadOptions(0, 1, Walk));
        Assert.ThrowsAny<ArgumentException>(() => new LoadOptions(1, 0, Walk));
        Assert.ThrowsAny<ArgumentException>(() => new LoadOptions(1, 1, Walk) { Period = TimeSpan.Zero });
        Assert.ThrowsAny<ArgumentException>(() => new LoadOptions(1, 1, Walk) { CellSize = 0 });
        Assert.ThrowsAny<ArgumentException>(() => new LoadOptions(1, 1, Walk) { CellSize = double.NaN });
    }

    [Fact]
    public void ScriptsStillSendsNothingRandomRepeatsAndDiffersByClient()
    {
        Assert.Null(LoadScripts.All["still"](0, 0));

        var random = LoadScripts.All["random"];
        Assert.Equal(random(3, 7), random(3, 7));
        Assert.NotEqual(random(3, 7), random(4, 7));
        Assert.NotEqual(random(0, 100003), random(1, 0));
    }
}
