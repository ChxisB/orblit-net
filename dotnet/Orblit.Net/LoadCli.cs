namespace Orblit.Net;

/// <summary>The command line of the load harness, with its streams passed in so a test can read them.</summary>
public static class LoadCli
{
    /// <summary>Runs the harness. Returns the exit code: 0, 1 for an overrun, 2 for refused inputs, 64 for bad arguments.</summary>
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Contains("--help"))
        {
            await output.WriteLineAsync(LoadArgs.Usage);
            return 0;
        }

        (LoadOptions Options, string Script) run;
        try
        {
            run = LoadArgs.Parse(args);
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            await error.WriteLineAsync($"{e.Message}\n\n{LoadArgs.Usage}");
            return 64;
        }

        await output.WriteLineAsync($"script {run.Script} over {(run.Options.Sockets ? "sockets" : "loopback")}");
        var report = await LoadHarness.RunAsync(run.Options, cancellationToken);
        await output.WriteAsync(report.Describe());
        return report.ExitCode;
    }
}
