namespace Orblit.Net.Tests;

public sealed class LoadPaceTests
{
    [Fact]
    public async Task TicksAreNeverEarlyEvenAtAPeriodThatTimersRoundUp()
    {
        var period = TimeSpan.FromMilliseconds(5);
        var options = new LoadOptions(2, 40, LoadScripts.All["still"]) { Period = period };

        var report = await LoadHarness.RunAsync(options, TestContext.Current.CancellationToken);

        // Forty ticks hold thirty nine periods between the first and the last.
        Assert.True(report.Elapsed >= period * 39, $"Ran in {report.Elapsed}.");
    }
}
