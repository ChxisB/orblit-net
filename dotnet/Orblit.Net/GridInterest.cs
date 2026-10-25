using System.Buffers.Binary;
using Orblit.Core;

namespace Orblit.Net;

/// <summary>
/// Tells a client about the entities within a ring of cells of its own.
/// </summary>
/// <remarks>
/// An entity's place is its position component, read from the capture: the first and third floats
/// of a vector of three, or the first two of a vector of two. A client's own entities are its focus,
/// and it sees around each of them. An entity with no finite position is seen only by its owner.
/// </remarks>
public sealed class GridInterest : IInterestRule
{
    private readonly ReplicationSet _set;
    private readonly ComponentType _position;
    private readonly Grid _grid;
    private readonly int _ring;
    private readonly int _bit;
    private readonly Dictionary<ulong, Cell> _cells = [];
    private readonly OrderedDictionary<string, List<Cell>> _focus = [];

    /// <param name="set">The replication set the host uses.</param>
    /// <param name="position">A replicated component of two or more float32s.</param>
    /// <param name="grid">The cells. The active cells share one, so the cell size lives in one place.</param>
    /// <param name="ring">
    /// How many cells out from a focus cell count as near. Zero is the focus cell alone.
    /// </param>
    public GridInterest(ReplicationSet set, ComponentType position, Grid grid, int ring = 1)
    {
        _bit = set.BitOf(position)
            ?? throw new ArgumentException($"{position.Name} does not replicate.", nameof(position));
        if (position.Kind != ComponentKind.Float32 || position.Arity < 2)
        {
            throw new ArgumentException($"{position.Name} wants two or more float32s.", nameof(position));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(ring);
        _set = set;
        _position = position;
        _grid = grid;
        _ring = ring;
    }

    /// <summary>The cells clients stood in at the last <see cref="Observe"/>, each once.</summary>
    public IEnumerable<Cell> FocusCells => _focus.Values.SelectMany(cells => cells).Distinct();

    public void Observe(WorldSnapshot world, IReadOnlyDictionary<string, IReadOnlyList<ulong>> focus)
    {
        _cells.Clear();
        foreach (var group in world.Groups)
        {
            if ((group.Mask & (1UL << _bit)) != 0)
            {
                ReadCells(group);
            }
        }

        _focus.Clear();
        foreach (var (clientId, networkIds) in focus)
        {
            _focus[clientId] = [.. networkIds.Where(_cells.ContainsKey).Select(id => _cells[id])];
        }
    }

    public bool IsRelevant(string clientId, ulong networkId) =>
        _cells.TryGetValue(networkId, out var cell)
        && _focus.TryGetValue(clientId, out var near)
        && near.Any(focus => Near(focus.X, cell.X) && Near(focus.Z, cell.Z));

    private bool Near(long a, long b)
    {
        // The absolute value of the smallest long overflows, so that gap is far.
        var gap = unchecked(a - b);
        return gap != long.MinValue && Math.Abs(gap) <= _ring;
    }

    private void ReadCells(SnapshotGroup group)
    {
        var before = _set.StrideOf(group.Mask & ((1UL << _bit) - 1));
        var second = _position.Arity == 2 ? 4 : 8;
        for (var i = 0; i < group.Length; i++)
        {
            var row = group.RowAt(i);
            var cell = _grid.CellOf(
                BinaryPrimitives.ReadSingleLittleEndian(row[before..]),
                BinaryPrimitives.ReadSingleLittleEndian(row[(before + second)..]));
            if (cell is { } found)
            {
                _cells[group.NetworkIds[i]] = found;
            }
        }
    }
}
