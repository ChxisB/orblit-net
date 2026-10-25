namespace Orblit.Net.Tests;

public sealed class LoadCliTests : IDisposable
{
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    public void Dispose()
    {
        _output.Dispose();
        _error.Dispose();
    }

    private Task<int> Run(params string[] args) =>
        LoadCli.RunAsync(args, _output, _error, TestContext.Current.CancellationToken);

    [Fact]
    public async Task HelpPrintsTheUsageAndExitsZero()
    {
        var code = await Run("--help");

        Assert.Equal(0, code);
        Assert.Contains("Usage: orblit-load", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABadArgumentExitsSixtyFourWithTheReasonAndTheUsage()
    {
        var code = await Run("--hz", "0");

        Assert.Equal(64, code);
        Assert.Contains("--hz wants a whole number", _error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Usage: orblit-load", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunNamesTheMachineTheClientCountAndTheP95()
    {
        var code = await Run("--clients", "4", "--seconds", "1", "--hz", "10");

        var lines = _output.ToString().Split('\n');
        Assert.Equal(0, code);
        Assert.Equal("script walk over loopback", lines[0]);
        Assert.StartsWith("4 clients, 10 ticks", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("machine ", lines[2], StringComparison.Ordinal);
        Assert.Contains(lines, line => line.StartsWith("tick   avg ", StringComparison.Ordinal) && line.Contains(" p95 ", StringComparison.Ordinal));
    }
}
