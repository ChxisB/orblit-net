namespace Orblit.Net;

/// <summary>What a load run does. Bad values are refused when the options are made.</summary>
public sealed class LoadOptions
{
    private readonly TimeSpan _period = TimeSpan.FromMilliseconds(50);
    private readonly double? _cellSize;

    public LoadOptions(int clients, int ticks, LoadScript script)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(clients, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(ticks, 1);
        ArgumentNullException.ThrowIfNull(script);
        Clients = clients;
        Ticks = ticks;
        Script = script;
    }

    public int Clients { get; }

    public int Ticks { get; }

    public LoadScript Script { get; }

    public TimeSpan Period
    {
        get => _period;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _period = value;
        }
    }

    /// <summary>Use WebSockets on localhost instead of loopback links.</summary>
    public bool Sockets { get; init; }

    /// <summary>
    /// Stands in for the server's own work in a tick, such as physics. It runs inside
    /// <c>host.Phase("system", ...)</c>, so it shows in the summary beside the host's stages.
    /// </summary>
    public Action<int>? ServerWork { get; init; }

    /// <summary>
    /// Tells each client only about the entities within one cell of its own, with cells this many
    /// units across. Null tells every client everything.
    /// </summary>
    public double? CellSize
    {
        get => _cellSize;
        init
        {
            if (value is { } cell && (double.IsNaN(cell) || cell <= 0))
            {
                throw new ArgumentOutOfRangeException(nameof(value), cell, "The cell size must be above zero.");
            }

            _cellSize = value;
        }
    }
}
