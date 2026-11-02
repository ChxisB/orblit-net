using System.Buffers.Binary;
using System.Text;
using Orblit.VectorMath;

namespace Orblit.Net.Game;

/// <summary>
/// Writes the body of a call and reads it back. A call is a name and up to <see cref="MaxArgs"/>
/// arguments, each a bool, an int, a float, a string, a <see cref="Vector3"/> or a <see cref="NetEntity"/>.
/// </summary>
/// <remarks>
/// The bytes of a call come from a client, so reading never throws. It returns null for a body that
/// is short, names a kind it does not know, claims more bytes than it has or carries bytes past its
/// last argument.
/// </remarks>
internal static class NetCodec
{
    public const int MaxArgs = 16;

    private const int VectorBytes = 3 * sizeof(double);

    /// <summary>The kinds an argument can be. The numbers are the wire format, so they never change.</summary>
    private enum ArgKind : byte
    {
        Bool = 1,
        Int = 2,
        Float = 3,
        String = 4,
        Vector = 5,
        Entity = 6,
    }

    /// <summary>True when a call can carry an argument of this type.</summary>
    public static bool Carries(Type type) =>
        type == typeof(bool) || type == typeof(int) || type == typeof(float) || type == typeof(string)
        || type == typeof(Vector3) || type == typeof(NetEntity);

    /// <exception cref="ArgumentException">
    /// The name is empty or over 255 bytes, there are more than <see cref="MaxArgs"/> arguments, or one
    /// is of a type a call cannot carry.
    /// </exception>
    public static byte[] EncodeCall(string name, IReadOnlyList<object> args)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        if (nameBytes.Length is 0 or > byte.MaxValue)
        {
            throw new ArgumentException("A call name is 1 to 255 bytes of UTF-8.", nameof(name));
        }

        if (args.Count > MaxArgs)
        {
            throw new ArgumentException($"A call takes at most {MaxArgs} arguments.", nameof(args));
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)nameBytes.Length);
        writer.Write(nameBytes);
        writer.Write((byte)args.Count);
        foreach (var arg in args)
        {
            WriteArg(writer, arg);
        }

        writer.Flush();
        return stream.ToArray();
    }

    public static CallMessage? DecodeCall(ReadOnlySpan<byte> bytes)
    {
        var cursor = new Cursor(bytes);
        if (!TryReadName(ref cursor, out var name) || !cursor.TryTake(1, out var count) || count[0] > MaxArgs)
        {
            return null;
        }

        var args = new object[count[0]];
        for (var i = 0; i < args.Length; i++)
        {
            if (TryReadArg(ref cursor) is not { } arg)
            {
                return null;
            }

            args[i] = arg;
        }

        return cursor.Done ? new CallMessage(name, args) : null;
    }

    private static void WriteArg(BinaryWriter writer, object? arg)
    {
        switch (arg)
        {
            case bool flag:
                writer.Write((byte)ArgKind.Bool);
                writer.Write(flag);
                break;
            case int number:
                writer.Write((byte)ArgKind.Int);
                writer.Write(number);
                break;
            case float number:
                writer.Write((byte)ArgKind.Float);
                writer.Write(number);
                break;
            case string text:
                WriteText(writer, ArgKind.String, text);
                break;
            case NetEntity entity:
                WriteText(writer, ArgKind.Entity, entity.Id);
                break;
            case Vector3 vector:
                writer.Write((byte)ArgKind.Vector);
                writer.Write(vector.X);
                writer.Write(vector.Y);
                writer.Write(vector.Z);
                break;
            default:
                throw new ArgumentException(
                    $"A call argument is a bool, an int, a float, a string, a Vector3 or a NetEntity, not {(arg is null ? "null" : "a " + arg.GetType().Name)}.");
        }
    }

    private static void WriteText(BinaryWriter writer, ArgKind kind, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        writer.Write((byte)kind);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static bool TryReadName(ref Cursor cursor, out string name)
    {
        name = string.Empty;
        if (!cursor.TryTake(1, out var length) || length[0] == 0 || !cursor.TryTake(length[0], out var bytes))
        {
            return false;
        }

        name = Encoding.UTF8.GetString(bytes);
        return true;
    }

    private static object? TryReadArg(ref Cursor cursor)
    {
        if (!cursor.TryTake(1, out var tag))
        {
            return null;
        }

        var kind = (ArgKind)tag[0];
        var size = kind is ArgKind.String or ArgKind.Entity ? ReadLength(ref cursor) : FixedSize(kind);
        if (!cursor.TryTake(size, out var payload))
        {
            return null;
        }

        return kind switch
        {
            ArgKind.Bool => payload[0] != 0,
            ArgKind.Int => BinaryPrimitives.ReadInt32LittleEndian(payload),
            ArgKind.Float => BinaryPrimitives.ReadSingleLittleEndian(payload),
            ArgKind.String => Encoding.UTF8.GetString(payload),
            ArgKind.Entity => new NetEntity(Encoding.UTF8.GetString(payload)),
            _ => new Vector3(
                BinaryPrimitives.ReadDoubleLittleEndian(payload),
                BinaryPrimitives.ReadDoubleLittleEndian(payload[sizeof(double)..]),
                BinaryPrimitives.ReadDoubleLittleEndian(payload[(2 * sizeof(double))..])),
        };
    }

    // -1 for a kind that is not on the wire. TryTake refuses a negative count.
    private static int FixedSize(ArgKind kind) => kind switch
    {
        ArgKind.Bool => 1,
        ArgKind.Int or ArgKind.Float => sizeof(int),
        ArgKind.Vector => VectorBytes,
        _ => -1,
    };

    private static int ReadLength(ref Cursor cursor) =>
        cursor.TryTake(sizeof(int), out var bytes) ? BinaryPrimitives.ReadInt32LittleEndian(bytes) : -1;

    private ref struct Cursor(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _at;

        public readonly bool Done => _at == _bytes.Length;

        public bool TryTake(int count, out ReadOnlySpan<byte> taken)
        {
            if (count < 0 || count > _bytes.Length - _at)
            {
                taken = default;
                return false;
            }

            taken = _bytes.Slice(_at, count);
            _at += count;
            return true;
        }
    }
}
