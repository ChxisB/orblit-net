using System.Globalization;
using System.Runtime.InteropServices;

namespace Orblit.Net;

/// <summary>What a load run measured, and the words to say it.</summary>
public sealed record LoadReport
{
    public required int Clients { get; init; }

    public required int Ticks { get; init; }

    public required TimeSpan Period { get; init; }

    public required TimeSpan Elapsed { get; init; }

    public required PhaseSummary Summary { get; init; }

    /// <summary>Inputs the host applied. A run that refuses any is mis-wired, and its figures are not a load.</summary>
    public required int Accepted { get; init; }

    public required int Rejected { get; init; }

    /// <summary>Where the run was made, so two reports can be compared fairly. It names no host or user.</summary>
    public string Machine { get; init; } = ThisMachine();

    /// <summary>The host's own tick, averaged over the measured window, is longer than the period.</summary>
    public bool Overran => Summary.TickAverage > Period;

    /// <summary>
    /// Ticks started late on average. With the clients on the host's threads, this can be the
    /// harness, not the host.
    /// </summary>
    public bool Behind => Summary.DriftAverage > Period;

    /// <summary>2 when the host refused inputs, since the figures are not a load. 1 when the host's tick overran. Otherwise 0.</summary>
    public int ExitCode => Rejected > 0 ? 2 : Overran ? 1 : 0;

    public string Describe()
    {
        var lines = new List<string>
        {
            $"{Clients} clients, {Ticks} ticks of {Ms(Period)} (last {Summary.Ticks} measured)",
            $"machine {Machine}",
            $"tick   avg {Ms(Summary.TickAverage)}  p95 {Ms(Summary.TickP95)}  max {Ms(Summary.TickMax)}",
            $"drift  avg {Ms(Summary.DriftAverage)}  max {Ms(Summary.DriftMax)}",
        };
        lines.AddRange(PhaseLines());
        lines.AddRange(ByteLines());
        lines.Add($"inputs applied {Accepted}, refused {Rejected}");
        if (Behind)
        {
            lines.Add(
                $"behind schedule: ticks started {Ms(Summary.DriftAverage)} late on average. " +
                "The clients share the host's threads.");
        }

        return string.Join('\n', lines) + '\n';
    }

    private static string ThisMachine() =>
        $"{RuntimeInformation.OSDescription}, {Environment.ProcessorCount} cores, " +
        $"{RuntimeInformation.ProcessArchitecture}, {RuntimeInformation.FrameworkDescription}";

    private static string Ms(TimeSpan span) =>
        (span.TotalMicroseconds / 1000).ToString("F2", CultureInfo.InvariantCulture) + " ms";

    private List<string> PhaseLines()
    {
        var lines = new List<string> { "phase (per tick)" };
        foreach (var (name, stats) in Summary.Phases.OrderByDescending(phase => phase.Value.Average))
        {
            lines.Add($"  {name,-10} avg {Ms(stats.Average)}  max {Ms(stats.Max)}");
        }

        return lines;
    }

    private List<string> ByteLines()
    {
        var columns = new Dictionary<string, int>();
        var overhead = 0;
        foreach (var sent in Summary.BytesSent.Values)
        {
            foreach (var (name, bytes) in sent.Columns)
            {
                columns[name] = columns.GetValueOrDefault(name) + bytes;
            }

            overhead += sent.Overhead;
        }

        string Rate(int total) => ((double)total / Clients / Elapsed.TotalSeconds).ToString("F0", CultureInfo.InvariantCulture);

        var lines = new List<string> { "bytes per client per second (first full snapshot included)" };
        lines.AddRange(columns.Select(column => $"  {column.Key,-10} {Rate(column.Value)}"));
        lines.Add($"  {"overhead",-10} {Rate(overhead)}");
        return lines;
    }
}
