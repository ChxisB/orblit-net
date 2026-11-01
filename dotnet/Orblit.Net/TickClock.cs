using System.Diagnostics;

namespace Orblit.Net;

/// <summary>
/// A fixed-rate schedule that measures how late each tick starts.
/// </summary>
/// <remarks>
/// The next tick is due one period after the last one was due, not one period after it ran, so a
/// late tick does not shift the ones behind it. When the clock falls more than two periods
/// behind, it restarts from now instead of running ticks back to back, which would stall the host
/// further.
/// </remarks>
public sealed class TickClock
{
    private TimeSpan? _due;

    /// <param name="period">How long one tick lasts.</param>
    /// <param name="now">
    /// Must be monotonic. A wall clock that resyncs reads as lag that never happened. Defaults to
    /// <see cref="MonotonicNow"/>.
    /// </param>
    public TickClock(TimeSpan period, Func<TimeSpan>? now = null)
    {
        Period = period;
        Now = now ?? MonotonicNow();
    }

    public TimeSpan Period { get; }

    public Func<TimeSpan> Now { get; }

    /// <summary>
    /// How late the last tick started, never negative: the clock only advises, and a tick that
    /// starts early is not late. Null until the second tick, since the first one only sets the
    /// schedule.
    /// </summary>
    public TimeSpan? Drift { get; private set; }

    /// <summary>How long until a tick is due. Zero when one is due now.</summary>
    public TimeSpan UntilNext()
    {
        if (_due is not { } due)
        {
            return TimeSpan.Zero;
        }

        var left = due - Now();
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    /// <summary>Starts a tick.</summary>
    public void Begin()
    {
        var current = Now();
        if (_due is not { } due)
        {
            _due = current + Period;
            return;
        }

        var behind = current - due;
        Drift = behind < TimeSpan.Zero ? TimeSpan.Zero : behind;
        _due = behind > Period * 2 ? current + Period : due + Period;
    }

    /// <summary>Time since this call, read off a stopwatch so it never goes backwards.</summary>
    public static Func<TimeSpan> MonotonicNow()
    {
        var start = Stopwatch.GetTimestamp();
        return () => Stopwatch.GetElapsedTime(start);
    }
}
