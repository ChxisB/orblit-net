namespace Orblit.Net.Game;

/// <summary>One variable of one entity, as the bytes its column holds.</summary>
internal sealed class VarCell(NetVarKind kind)
{
    /// <summary>Replaced and never changed in place, so a read sees one whole value.</summary>
    public byte[] Bytes { get; set; } = new byte[NetVarCodec.ByteSize(kind)];
}

/// <summary>
/// The values a game reads and writes through its handles, one cell for each entity and name. The
/// game's frame is the only thing that touches it. The columns are where the wire is, and the end
/// that owns the store copies between the two on each side of the game's update.
/// </summary>
internal sealed class VarStore(NetVarSchema schema)
{
    private readonly Dictionary<string, Dictionary<string, VarCell>> _cells = [];

    public NetVarSchema Schema { get; } = schema;

    public VarCell Cell(string entityId, string name, NetVarKind kind)
    {
        if (!_cells.TryGetValue(entityId, out var named))
        {
            named = [];
            _cells[entityId] = named;
        }

        if (!named.TryGetValue(name, out var cell))
        {
            cell = new VarCell(kind);
            named[name] = cell;
        }

        return cell;
    }

    public VarCell? Find(string entityId, string name) =>
        _cells.TryGetValue(entityId, out var named) && named.TryGetValue(name, out var cell) ? cell : null;

    /// <summary>Sets the value of a declared variable, making its cell if the game has not asked for it yet.</summary>
    public void Write(string entityId, string name, byte[] bytes) =>
        Cell(entityId, name, Schema.KindOf(name) ?? throw new ArgumentException($"\"{name}\" is not declared.", nameof(name))).Bytes = bytes;

    /// <summary>Drops an entity's cells, so one that comes back under the same id starts from zero.</summary>
    public void Forget(string entityId) => _cells.Remove(entityId);
}
