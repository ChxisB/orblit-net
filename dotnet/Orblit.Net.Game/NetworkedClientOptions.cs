namespace Orblit.Net.Game;

/// <summary>What a <see cref="NetworkedClient"/> is built from.</summary>
public sealed class NetworkedClientOptions
{
    /// <summary>The id the server knows this client by.</summary>
    public required string ClientId { get; init; }

    /// <summary>The link to the server.</summary>
    public required ITransport Transport { get; init; }

    /// <summary>
    /// How far behind the server to show other entities. Zero shows each tick as it lands. A delay blends
    /// each value in a straight line, rotation included, so an angle that wraps from 359 to 1 swings back
    /// through 180 for a moment.
    /// </summary>
    public TimeSpan InterpolationDelay { get; init; }

    /// <summary>Time since the client started. Leave it out for a stopwatch.</summary>
    public Func<TimeSpan>? Clock { get; init; }

    /// <summary>The variables of the game. It must be the schema the server was built from.</summary>
    public NetVarSchema? Vars { get; init; }
}
