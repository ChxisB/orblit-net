using Orblit.Core;
using Orblit.VectorMath;

namespace Orblit.Net.Game;

/// <summary>What a variable holds.</summary>
internal enum NetVarKind
{
    Bool,
    Int,
    Float,
    Vector,
}

/// <summary>
/// Turns a variable into the bytes of its column and back. A bool and an int are not floats, so they
/// get columns the host copies as they are. A float and a vector get float columns, which the client
/// blends when it interpolates.
/// </summary>
internal static class NetVarCodec
{
    private const int VectorLength = 3;

    public static NetVarKind KindOf<T>()
        where T : notnull
    {
        if (typeof(T) == typeof(bool))
        {
            return NetVarKind.Bool;
        }

        if (typeof(T) == typeof(int))
        {
            return NetVarKind.Int;
        }

        if (typeof(T) == typeof(float))
        {
            return NetVarKind.Float;
        }

        return typeof(T) == typeof(Vector3)
            ? NetVarKind.Vector
            : throw new ArgumentException($"A network variable is a bool, an int, a float or a Vector3, not a {typeof(T).Name}.");
    }

    public static ComponentKind ColumnKind(NetVarKind kind) => kind switch
    {
        NetVarKind.Bool => ComponentKind.UInt8,
        NetVarKind.Int => ComponentKind.Int32,
        _ => ComponentKind.Float32,
    };

    public static int Arity(NetVarKind kind) => kind == NetVarKind.Vector ? VectorLength : 1;

    public static int ByteSize(NetVarKind kind) => ColumnKind(kind).BytesPerElement() * Arity(kind);

    public static byte[] Encode<T>(T value)
        where T : notnull
    {
        return value switch
        {
            bool flag => [flag ? (byte)1 : (byte)0],
            int number => BitConverter.GetBytes(number),
            float number => BitConverter.GetBytes(number),
            _ => EncodeVector((Vector3)(object)value),
        };
    }

    public static T Decode<T>(ReadOnlySpan<byte> bytes)
        where T : notnull
    {
        object value = KindOf<T>() switch
        {
            NetVarKind.Bool => bytes[0] != 0,
            NetVarKind.Int => BitConverter.ToInt32(bytes),
            NetVarKind.Float => BitConverter.ToSingle(bytes),
            _ => DecodeVector(bytes),
        };
        return (T)value;
    }

    private static byte[] EncodeVector(Vector3 vector)
    {
        var bytes = new byte[sizeof(float) * VectorLength];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), (float)vector.X);
        BitConverter.TryWriteBytes(bytes.AsSpan(sizeof(float)), (float)vector.Y);
        BitConverter.TryWriteBytes(bytes.AsSpan(sizeof(float) * 2), (float)vector.Z);
        return bytes;
    }

    private static Vector3 DecodeVector(ReadOnlySpan<byte> bytes) => new(
        BitConverter.ToSingle(bytes[..sizeof(float)]),
        BitConverter.ToSingle(bytes[sizeof(float)..]),
        BitConverter.ToSingle(bytes[(sizeof(float) * 2)..]));
}
