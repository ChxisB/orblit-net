using System.Runtime.InteropServices;
using Orblit.Core;
using Orblit.Scene;
using Orblit.VectorMath;

namespace Orblit.Net.Game;

/// <summary>
/// What a networked scene puts in a <see cref="World"/>: a network id and the entity's place.
/// </summary>
/// <remarks>
/// Both ends build it the same way. The replication set orders components by name, so the wire
/// carries no schema and the two worlds may hand out different component ids.
/// <para>
/// The place is nine floats, position then rotation in degrees then scale, which is the order and
/// the unit <see cref="TransformComponent"/> keeps. It is a float column because the host
/// interpolates and delta-compresses those and leaves the rest alone.
/// </para>
/// </remarks>
internal sealed class NetLayout
{
    private const int TransformLength = 9;
    private readonly World _world;

    public NetLayout(World world)
    {
        _world = world;
        NetworkId = world.RegisterComponent("orblit.net.id", ComponentKind.Int64);
        Transform = world.RegisterComponent("orblit.net.transform", ComponentKind.Float32, TransformLength);
        Set = new ReplicationSet([Transform], ["orblit.net.transform"]);
    }

    public ComponentType NetworkId { get; }

    public ComponentType Transform { get; }

    public ReplicationSet Set { get; }

    public static float[] Floats(TransformComponent transform) =>
    [
        (float)transform.Position.X,
        (float)transform.Position.Y,
        (float)transform.Position.Z,
        (float)transform.Rotation.X,
        (float)transform.Rotation.Y,
        (float)transform.Rotation.Z,
        (float)transform.Scale.X,
        (float)transform.Scale.Y,
        (float)transform.Scale.Z,
    ];

    public static TransformComponent TransformOf(ReadOnlySpan<float> floats) => new(
        new Vector3(floats[0], floats[1], floats[2]),
        new Vector3(floats[3], floats[4], floats[5]),
        new Vector3(floats[6], floats[7], floats[8]));

    public static byte[] Bytes(float[] floats) => MemoryMarshal.AsBytes<float>(floats).ToArray();

    /// <summary>The place of a scene entity as the column holds it. An entity with no transform sits at the origin.</summary>
    public static float[] PlaceOf(SceneEntity entity) =>
        Floats(entity[SceneComponents.Transform] as TransformComponent ?? new TransformComponent());

    /// <summary>The edit that puts <paramref name="entity"/> at <paramref name="floats"/>, or null when it is already there.</summary>
    public static SetComponent? Placing(SceneEntity entity, float[] floats)
    {
        if (PlaceOf(entity).AsSpan().SequenceEqual(floats))
        {
            return null;
        }

        var before = entity[SceneComponents.Transform] as TransformComponent;
        return new SetComponent(entity.Id, SceneComponents.Transform, before?.ToJson(), TransformOf(floats).ToJson());
    }

    /// <summary>The column's nine floats, copied so they can be read after the world's lock is let go.</summary>
    public float[] ReadColumn(ulong entity) =>
        _world.TryGetFloat32(entity, Transform, out var values) ? values.ToArray() : [];

    public void WriteColumn(ulong entity, float[] floats)
    {
        if (_world.TryGetFloat32(entity, Transform, out var values))
        {
            floats.CopyTo(values);
        }
    }
}
