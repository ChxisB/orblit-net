using System.Globalization;
using System.Text.Json;
using Orblit.Core;

namespace Orblit.Net.Tests;

/// <summary>What the cross-join tests compare: the entities with network ids 1 to 4, as lines.</summary>
internal static class CrossScene
{
    public const int Ticks = 100;

    private const int LastId = 4;

    public static IReadOnlyList<string> Describe(Peer peer, Func<ulong, ulong?> entityFor)
    {
        var lines = new List<string>();
        for (ulong id = 1; id <= LastId; id++)
        {
            if (entityFor(id) is { } entity)
            {
                var health = peer.World.Has(entity, peer.Health) ? Column.Int(peer.World, entity, peer.Health) : (int?)null;
                lines.Add(Line(id, Column.Floats(peer.World, entity, peer.Position), health));
            }
        }

        return lines;
    }

    /// <summary>Reads the JSON the Dart peer prints into the same lines.</summary>
    public static IReadOnlyList<string> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return
        [
            .. document.RootElement.GetProperty("entities").EnumerateArray().Select(entity => Line(
                entity.GetProperty("id").GetUInt64(),
                [.. entity.GetProperty("position").EnumerateArray().Select(value => (float)value.GetDouble())],
                entity.GetProperty("health") is { ValueKind: JsonValueKind.Number } health ? health.GetInt32() : null)),
        ];
    }

    private static string Line(ulong id, float[] position, int? health) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{id} position {position[0]:R} {position[1]:R} {position[2]:R} health {health?.ToString(CultureInfo.InvariantCulture) ?? "none"}");
}

/// <summary>
/// A host that plays the scene: three entities that move every tick, one that gains health at tick
/// 10, one spawned at tick 30 and one removed at tick 60.
/// </summary>
internal sealed class CrossHost : IDisposable
{
    private readonly Peer _peer = new(reverseRegistrationOrder: false);
    private readonly Dictionary<ulong, ulong> _live = [];

    public CrossHost()
    {
        Net = new NetHost(new NetHostOptions
        {
            World = _peer.World,
            Set = _peer.Set,
            NetworkId = _peer.NetworkId,
        });
        for (var i = 0; i < 3; i++)
        {
            Spawn();
        }
    }

    public NetHost Net { get; }

    public IReadOnlyList<string> Describe() =>
        CrossScene.Describe(_peer, id => _live.TryGetValue(id, out var entity) ? entity : null);

    public void Step(int tick)
    {
        var world = _peer.World;
        if (tick == 10)
        {
            world.Add<int>(_live[1], _peer.Health, [100]);
        }

        if (tick == 30)
        {
            Spawn();
        }

        if (tick == 60)
        {
            Net.Despawn(_live[2]);
            _live.Remove(2);
        }

        foreach (var (id, entity) in _live)
        {
            Column.SetFloat(world, entity, _peer.Position, 0, id + (tick * 0.5f));
            Column.SetFloat(world, entity, _peer.Position, 1, tick % 7);
            Column.SetFloat(world, entity, _peer.Position, 2, -(float)id);
        }

        if (tick > 10)
        {
            Column.SetInt(world, _live[1], _peer.Health, 100 - tick);
        }

        Net.Publish();
    }

    public void Dispose()
    {
        Net.Dispose();
        _peer.Dispose();
    }

    private void Spawn()
    {
        var entity = _peer.World.CreateEntity();
        var id = Net.Spawn(entity);
        _peer.World.Add<float>(entity, _peer.Position, [0, 0, 0]);
        _live[id] = entity;
    }
}
