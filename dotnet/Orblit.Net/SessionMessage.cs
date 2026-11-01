using System.Buffers.Binary;
using System.Text;

namespace Orblit.Net;

/// <summary>
/// The messages around a session: who a client is, what the game says, and why a client is sent
/// away. The engine carries the bytes and reads nothing from the credentials or the body.
/// </summary>
public static class SessionMessage
{
    private const uint JoinMagic = 0x4F52424A; // 'ORBJ'
    private const uint GoodbyeMagic = 0x4F524247; // 'ORBG'
    private const uint BodyMagic = 0x4F52424D; // 'ORBM'
    private const int HeaderSize = 4;

    private static readonly UTF8Encoding Lenient = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>A client telling the authority who it is. The credentials are the game's own.</summary>
    public static byte[] EncodeJoin(ReadOnlySpan<byte> credentials) => Tagged(JoinMagic, credentials);

    /// <summary>The credentials in a join, or null if this is not one.</summary>
    public static byte[]? DecodeJoin(ReadOnlySpan<byte> message) => Untagged(JoinMagic, message);

    /// <summary>A message of the game's own, in either direction.</summary>
    public static byte[] EncodeMessage(ReadOnlySpan<byte> body) => Tagged(BodyMagic, body);

    /// <summary>The body of a game message, or null if this is not one.</summary>
    public static byte[]? DecodeMessage(ReadOnlySpan<byte> message) => Untagged(BodyMagic, message);

    /// <summary>The authority telling a client why it is being sent away.</summary>
    public static byte[] EncodeGoodbye(string reason) => Tagged(GoodbyeMagic, Encoding.UTF8.GetBytes(reason));

    /// <summary>The reason in a goodbye, or null if this is not one. Bad UTF-8 reads as replacement characters.</summary>
    public static string? DecodeGoodbye(ReadOnlySpan<byte> message) =>
        Untagged(GoodbyeMagic, message) is { } body ? Lenient.GetString(body) : null;

    private static byte[] Tagged(uint magic, ReadOnlySpan<byte> body)
    {
        var message = new byte[HeaderSize + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(message, magic);
        body.CopyTo(message.AsSpan(HeaderSize));
        return message;
    }

    private static byte[]? Untagged(uint magic, ReadOnlySpan<byte> message) =>
        message.Length >= HeaderSize && BinaryPrimitives.ReadUInt32LittleEndian(message) == magic
            ? message[HeaderSize..].ToArray()
            : null;
}
