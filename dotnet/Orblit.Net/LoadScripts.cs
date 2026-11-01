namespace Orblit.Net;

/// <summary>Where a scripted client puts its own entity on a tick, or null to send nothing that tick.</summary>
public delegate float[]? LoadScript(int client, int tick);

/// <summary>The scripts a load run can play.</summary>
public static class LoadScripts
{
    public static IReadOnlyDictionary<string, LoadScript> All { get; } = new Dictionary<string, LoadScript>
    {
        ["still"] = Still,
        ["walk"] = Walk,
        ["random"] = Seeded,
        ["scatter"] = Scatter,
    };

    private static float[]? Still(int client, int tick) => null;

    private static float[]? Walk(int client, int tick) => [client + (tick * 0.1f), 0, 0];

    // Seeded from the client and tick, so a run repeats. The client is multiplied by a large odd
    // number so that client 0 at a late tick and client 1 at tick 0 get different seeds.
    private static float[]? Seeded(int client, int tick)
    {
        var random = new Random(unchecked((client * -1640531535) + tick));
        return [(float)(random.NextDouble() * 100), 0, (float)(random.NextDouble() * 100)];
    }

    // A place fixed by the client, 1000 units across, with a slow drift along x.
    private static float[]? Scatter(int client, int tick)
    {
        var random = new Random(client);
        return [(float)((random.NextDouble() * 1000) + (tick * 0.1)), 0, (float)(random.NextDouble() * 1000)];
    }
}
