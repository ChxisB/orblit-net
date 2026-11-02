using System.Buffers.Binary;
using System.Text;
using Orblit.Json;
using Orblit.Scene;

using JsonMap = System.Collections.Generic.IReadOnlyDictionary<string, object?>;

namespace Orblit.Net.Game;

/// <summary>What the server tells a client about the entities it shares, apart from where they are.</summary>
internal abstract record WireMessage(ulong NetworkId);

/// <summary>An entity now exists. It carries the entity whole, so a client that does not have it can make it.</summary>
internal sealed record SpawnMessage(ulong NetworkId, SceneEntity Entity) : WireMessage(NetworkId);

/// <summary>An entity has a new owner, or none.</summary>
internal sealed record OwnerMessage(ulong NetworkId, string? ClientId) : WireMessage(NetworkId);

/// <summary>
/// The bodies sent over the host's game-message channel. The first byte says what the body is, then
/// comes the network id, then the rest of the body.
/// </summary>
/// <remarks>
/// Where an entity is travels as a component column, with deltas and interpolation. These messages
/// are for what happens once: an entity appears, an entity changes hands. They are reliable and
/// ordered, so a client sees a spawn before the column that moves it. Tag 3 is kept for calls.
/// </remarks>
internal static class NetWire
{
    private const byte SpawnTag = 1;
    private const byte OwnerTag = 2;
    private const int HeaderLength = 1 + sizeof(ulong);

    public static byte[] Spawn(ulong networkId, SceneEntity entity) =>
        Body(SpawnTag, networkId, JsonText.Encode(entity.ToJson()));

    public static byte[] Owner(ulong networkId, string? clientId) => Body(OwnerTag, networkId, clientId ?? string.Empty);

    /// <summary>Reads a body, or returns null for one that is short, unknown or does not hold an entity.</summary>
    public static WireMessage? Read(byte[] body)
    {
        if (body.Length < HeaderLength)
        {
            return null;
        }

        var networkId = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(1));
        var text = Encoding.UTF8.GetString(body.AsSpan(HeaderLength));
        return body[0] switch
        {
            SpawnTag => ReadSpawn(networkId, text),
            OwnerTag => new OwnerMessage(networkId, text.Length == 0 ? null : text),
            _ => null,
        };
    }

    private static byte[] Body(byte tag, ulong networkId, string text)
    {
        var rest = Encoding.UTF8.GetBytes(text);
        var body = new byte[HeaderLength + rest.Length];
        body[0] = tag;
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(1), networkId);
        rest.CopyTo(body, HeaderLength);
        return body;
    }

    private static SpawnMessage? ReadSpawn(ulong networkId, string text)
    {
        try
        {
            return JsonText.Decode(text) is JsonMap map && SceneEntity.FromJson(map) is { } entity
                ? new SpawnMessage(networkId, entity)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
