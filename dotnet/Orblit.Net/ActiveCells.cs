namespace Orblit.Net;

/// <summary>
/// Which cells of a <see cref="Net.Grid"/> are worth simulating: those within a radius of cells of
/// a focus, and for a hold after they stop being.
/// </summary>
/// <remarks>
/// It skips nothing itself. A system asks and decides, since stepping means different things to
/// physics, to AI and to growth. The answer is as of the last <see cref="Update"/>, so every system
/// in a tick sees the same cells.
/// </remarks>
public sealed class ActiveCells
{
    private readonly Func<TimeSpan> _now;

    // Insertion order is what a system walking Cells sees, so it must not depend on hashing.
    private OrderedDictionary<Cell, TimeSpan> _activeUntil = [];

    /// <param name="grid">The cells.</param>
    /// <param name="radius">How many cells out from a focus cell are active. Zero is the focus cell alone.</param>
    /// <param name="hold">
    /// How long a cell stays active once nothing is near it. A focus that steps back and forth over
    /// a border does not stop and start a cell.
    /// </param>
    /// <param name="clock">Must be monotonic. Defaults to <see cref="TickClock.MonotonicNow"/>.</param>
    public ActiveCells(Grid grid, int radius, TimeSpan hold, Func<TimeSpan>? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(radius);
        ArgumentOutOfRangeException.ThrowIfLessThan(hold, TimeSpan.Zero);
        Grid = grid;
        Radius = radius;
        Hold = hold;
        _now = clock ?? TickClock.MonotonicNow();
    }

    public Grid Grid { get; }

    public int Radius { get; }

    public TimeSpan Hold { get; }

    /// <summary>The cells that are active as of the last <see cref="Update"/>.</summary>
    public IEnumerable<Cell> Cells => _activeUntil.Keys;

    public bool IsActive(Cell cell) => _activeUntil.ContainsKey(cell);

    /// <summary>
    /// Whether the cell holding (<paramref name="x"/>, <paramref name="z"/>) is active. A place that
    /// is not a finite number is in no cell, so it is not active.
    /// </summary>
    public bool IsActiveAt(double x, double z) => Grid.CellOf(x, z) is { } cell && IsActive(cell);

    /// <summary>
    /// Takes the cells the focus entities stand in, once a tick. A cell near any of them is active
    /// through the hold from now. A cell is dropped once that has passed.
    /// </summary>
    public void Update(IEnumerable<Cell> focus)
    {
        var now = _now();
        // shortcut: the map is rebuilt each update, which is fine for thousands of cells.
        // Keep the expiry times in a sorted list if it shows up in the frame profile.
        var kept = new OrderedDictionary<Cell, TimeSpan>();
        foreach (var (cell, until) in _activeUntil)
        {
            if (until >= now)
            {
                kept[cell] = until;
            }
        }

        _activeUntil = kept;
        foreach (var centre in focus)
        {
            for (var dx = -Radius; dx <= Radius; dx++)
            {
                for (var dz = -Radius; dz <= Radius; dz++)
                {
                    _activeUntil[new Cell(centre.X + dx, centre.Z + dz)] = now + Hold;
                }
            }
        }
    }
}
