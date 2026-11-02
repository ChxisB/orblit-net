using System.Runtime.InteropServices;
using Orblit.Core;
using Orblit.Scene;
using Orblit.VectorMath;

namespace Orblit.Net.Game;

/// <summary>
/// What a networked scene puts in a <see cref="World"/>: a network id, the entity's place and a column
/// for each declared variable.
/// </summary>
/// <remarks>
/// Both ends build it the same way. The replication set orders components by name, so the wire
/// carries no schema and the two worlds may hand out different component ids.
/// <para>
/// The place is nine floats, position then rotation in degrees then scale, which is the order and
/// the unit <see cref="TransformComponent"/> keeps. It is a float column because the host
/// interpolates and delta-compresses those and leaves the rest alone.
/// </para>
/// <para>
/// Every column is owner-writable. Who may write is decided by who owns the entity, and only an
/// entity with owner authority can have an owner.
/// </para>
/// </remarks>
internal sealed class NetLayout
{
    private const int TransformLength = 9;
    private const string TransformName = "orblit.net.transform";
    private const string VarPrefix = "orblit.net.var.";
    private readonly World _world;

    public NetLayout(World world, NetVarSchema vars)
    {
        _world = world;
        NetworkId = world.RegisterComponent("orblit.net.id", ComponentKind.Int64);
        Transform = world.RegisterComponent(TransformName, ComponentKind.Float32, TransformLength);
        Columns = vars.Declared.ToDictionary(
            static pair => pair.Key,
            pair => world.RegisterComponent(
                VarPrefix + pair.Key,
                NetVarCodec.ColumnKind(pair.Value),
                NetVarCodec.Arity(pair.Value)));
        var types = Columns.Values.Prepend(Transform).ToArray();
        Set = new ReplicationSet(types, types.Select(static type => type.Name));
    }

    public ComponentType NetworkId { get; }

    public ComponentType Transform { get; }

    /// <summary>The column of each declared variable, by the variable's name.</summary>
    public IReadOnlyDictionary<string, ComponentType> Columns { get; }

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

    /// <summary>A variable's bytes, copied so they can be read after the world's lock is let go. Null when the entity has no such column.</summary>
    public byte[]? ReadVar(ulong entity, string name) =>
        _world.TryGetBytes(entity, Columns[name], out var bytes) ? bytes.ToArray() : null;

    public void WriteVar(ulong entity, string name, byte[] bytes)
    {
        if (_world.TryGetBytes(entity, Columns[name], out var column))
        {
            bytes.CopyTo(column);
        }
    }

    /// <summary>Gives an entity the column of a variable, at zero.</summary>
    public void AddVar(ulong entity, string name) => _world.Add<byte>(entity, Columns[name], new byte[Columns[name].ByteSize]);
}
