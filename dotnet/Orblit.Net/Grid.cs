namespace Orblit.Net;

/// <summary>One square of a <see cref="Grid"/>, by how many cells it is from the origin.</summary>
public readonly record struct Cell(long X, long Z);

/// <summary>
/// Square cells on the ground plane. Interest and active cells share one, so the cell size lives in
/// one place.
/// </summary>
public sealed class Grid
{
    public Grid(double cellSize)
    {
        if (!double.IsFinite(cellSize) || cellSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Not above zero");
        }

        CellSize = cellSize;
    }

    public double CellSize { get; }

    /// <summary>The cell holding (<paramref name="x"/>, <paramref name="z"/>), or null when either is not a finite number.</summary>
    public Cell? CellOf(double x, double z) =>
        double.IsFinite(x) && double.IsFinite(z)
            ? new Cell((long)Math.Floor(x / CellSize), (long)Math.Floor(z / CellSize))
            : null;
}
