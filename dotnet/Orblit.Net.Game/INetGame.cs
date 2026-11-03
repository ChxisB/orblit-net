namespace Orblit.Net.Game;

/// <summary>
/// A game that shares variables names them here. The editor builds the server and every client of a
/// Play session from this one schema, so the two ends cannot disagree about a column.
/// </summary>
/// <remarks>
/// A game with no variables need not implement it. The editor then builds each end from
/// <see cref="NetVarSchema.Empty"/>. The editor makes the game before any net exists, so the schema
/// must not depend on <c>Start</c>.
/// </remarks>
public interface INetGame
{
    NetVarSchema NetVars { get; }
}
