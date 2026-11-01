namespace Orblit.Net;

/// <summary>How long one named phase took across a window of ticks.</summary>
/// <param name="Total">All the time the phase took in the window.</param>
/// <param name="Average">Per tick in the window, so the phases add up to the tick average.</param>
/// <param name="Max">The most it took in a single tick.</param>
public readonly record struct PhaseStats(TimeSpan Total, TimeSpan Average, TimeSpan Max);

/// <summary>What one client was sent, split by component column.</summary>
/// <param name="Columns">Bytes of component data, keyed by component name.</param>
/// <param name="Overhead">Network ids, group headers and the despawn list.</param>
public sealed record ClientBytes(IReadOnlyDictionary<string, int> Columns, int Overhead)
{
    public int Total => Overhead + Columns.Values.Sum();
}

/// <summary>Where the time went over the last few ticks.</summary>
public sealed record PhaseSummary
{
    public required int Ticks { get; init; }

    public required TimeSpan TickAverage { get; init; }

    public required TimeSpan TickMax { get; init; }

    /// <summary>The tick length that 95 of every 100 ticks stayed within. The average hides the stalls that drop frames.</summary>
    public required TimeSpan TickP95 { get; init; }

    public required TimeSpan DriftAverage { get; init; }

    public required TimeSpan DriftMax { get; init; }

    public required IReadOnlyDictionary<string, PhaseStats> Phases { get; init; }

    /// <summary>Per client, totals since the host started. The window does not apply.</summary>
    // shortcut: totals, not windowed, since a window needs a map per client per tick. Window them
    // if a view needs a rate.
    public required IReadOnlyDictionary<string, ClientBytes> BytesSent { get; init; }
}

/// <summary>
/// Times the named parts of each tick and keeps the last few ticks.
/// </summary>
/// <remarks>
/// A tick opens at the first <see cref="Phase{T}"/> after the previous one closed, and closes at
/// <see cref="Finish"/>. Opening asks the clock to begin, so drift is measured where the tick
/// starts, not where it is reported. With no clock, phases are still timed and there is no drift or
/// overrun to report. Not thread safe: <see cref="NetHost"/> calls it under its own lock.
/// </remarks>
public sealed class PhaseTimes
{
    /// <summary>Quiet time between calls of the overrun hook, so one stall does not log a line for every late tick behind it.</summary>
    public static readonly TimeSpan OverrunEvery = TimeSpan.FromSeconds(5);

    private readonly TickClock? _clock;
    private readonly Func<TimeSpan> _now;
    private readonly Action<PhaseSummary>? _onOverrun;
    private readonly int _capacity;
    private readonly Dictionary<string, Sent> _sent = [];
    private readonly List<TickRecord> _ring = [];
    private TickRecord? _open;
    private TimeSpan? _lastOverrun;

    public PhaseTimes(TickClock? clock = null, Action<PhaseSummary>? onOverrun = null, int capacity = 300)
    {
        _clock = clock;
        _now = clock?.Now ?? TickClock.MonotonicNow();
        _onOverrun = onOverrun;
        _capacity = capacity;
    }

    /// <summary>
    /// Runs <paramref name="body"/>, adds its time to the open tick under <paramref name="name"/>,
    /// and returns what it returned. Run twice in a tick, a name adds up.
    /// </summary>
    // F5: it returns what the body returns so a stage can wrap an expression.
    public T Phase<T>(string name, Func<T> body)
    {
        var tick = _open ??= Begin();
        var start = _now();
        try
        {
            return body();
        }
        finally
        {
            tick.Phases[name] = tick.Phases.GetValueOrDefault(name) + (_now() - start);
        }
    }

    public void Phase(string name, Action body) =>
        Phase(name, () =>
        {
            body();
            return 0;
        });

    /// <summary>Closes the open tick. Calls the overrun hook when it ran past the period.</summary>
    public void Finish()
    {
        var tick = _open ??= Begin();
        _open = null;
        tick.Length = _now() - tick.Start;
        _ring.Add(tick);
        if (_ring.Count > _capacity)
        {
            _ring.RemoveAt(0);
        }

        if (_clock is { } clock && tick.Length > clock.Period)
        {
            ReportOverrun();
        }
    }

    /// <summary>
    /// Counts <paramref name="bytes"/> sent to <paramref name="client"/> under component
    /// <paramref name="column"/>. A null column is the rest of the message: network ids, headers
    /// and the despawn list.
    /// </summary>
    public void CountSent(string client, string? column, int bytes)
    {
        if (!_sent.TryGetValue(client, out var sent))
        {
            _sent[client] = sent = new Sent();
        }

        if (column is null)
        {
            sent.Overhead += bytes;
        }
        else
        {
            sent.Columns[column] = sent.Columns.GetValueOrDefault(column) + bytes;
        }
    }

    public void ForgetClient(string client) => _sent.Remove(client);

    public PhaseSummary Summary(int last = 30)
    {
        var window = _ring.Count > last ? _ring.GetRange(_ring.Count - last, last) : _ring;
        return Summarise(window, _sent);
    }

    // F5: starting a tick and handing it back is one step. The callers would otherwise each repeat
    // the null check on the open tick.
    private TickRecord Begin()
    {
        _clock?.Begin();
        return new TickRecord(_now(), _clock?.Drift);
    }

    private void ReportOverrun()
    {
        if (_onOverrun is null)
        {
            return;
        }

        var at = _now();
        if (_lastOverrun is { } last && at - last < OverrunEvery)
        {
            return;
        }

        _lastOverrun = at;
        _onOverrun(Summary());
    }

    private static PhaseSummary Summarise(List<TickRecord> window, Dictionary<string, Sent> sent)
    {
        var count = window.Count;
        var totals = new Dictionary<string, TimeSpan>();
        var maxes = new Dictionary<string, TimeSpan>();
        var lengthSum = TimeSpan.Zero;
        var lengthMax = TimeSpan.Zero;
        var driftSum = TimeSpan.Zero;
        var driftMax = TimeSpan.Zero;
        var drifted = 0;

        foreach (var tick in window)
        {
            lengthSum += tick.Length;
            lengthMax = TimeSpan.FromTicks(Math.Max(lengthMax.Ticks, tick.Length.Ticks));
            if (tick.Drift is { } drift)
            {
                drifted++;
                driftSum += drift;
                driftMax = TimeSpan.FromTicks(Math.Max(driftMax.Ticks, drift.Ticks));
            }

            foreach (var (name, took) in tick.Phases)
            {
                totals[name] = totals.GetValueOrDefault(name) + took;
                maxes[name] = maxes.TryGetValue(name, out var seen) && seen >= took ? seen : took;
            }
        }

        return new PhaseSummary
        {
            Ticks = count,
            TickAverage = count == 0 ? TimeSpan.Zero : lengthSum / count,
            TickMax = lengthMax,
            TickP95 = NearestRank95(window),
            DriftAverage = drifted == 0 ? TimeSpan.Zero : driftSum / drifted,
            DriftMax = driftMax,
            Phases = totals.ToDictionary(
                entry => entry.Key,
                entry => new PhaseStats(entry.Value, entry.Value / count, maxes[entry.Key])),
            BytesSent = sent.ToDictionary(
                entry => entry.Key,
                entry => new ClientBytes(new Dictionary<string, int>(entry.Value.Columns), entry.Value.Overhead)),
        };
    }

    // Nearest rank: the smallest length that at least 95 percent of the ticks are at or under.
    private static TimeSpan NearestRank95(List<TickRecord> window)
    {
        if (window.Count == 0)
        {
            return TimeSpan.Zero;
        }

        var lengths = window.Select(tick => tick.Length).Order().ToList();
        return lengths[((lengths.Count * 95 + 99) / 100) - 1];
    }

    private sealed class Sent
    {
        public Dictionary<string, int> Columns { get; } = [];

        public int Overhead { get; set; }
    }

    private sealed class TickRecord(TimeSpan start, TimeSpan? drift)
    {
        public TimeSpan Start { get; } = start;

        public TimeSpan? Drift { get; } = drift;

        public Dictionary<string, TimeSpan> Phases { get; } = [];

        public TimeSpan Length { get; set; }
    }
}
