using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Orblit.Net;

/// <summary>The server half of the WebSocket upgrade, spoken over a plain stream.</summary>
internal static class Upgrade
{
    private const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const int MaxHead = 16 * 1024;

    /// <summary>The body a client that is not asking to play is answered with.</summary>
    internal const string Refusal = "This port speaks the Orblit session protocol.";

    /// <summary>
    /// Reads one request. Returns its <c>Sec-WebSocket-Key</c> when it asks for an upgrade, and null
    /// for anything else, including a head that never ends.
    /// </summary>
    public static async Task<string?> ReadKey(Stream stream, CancellationToken cancel)
    {
        // One byte at a time, so a frame the client sends right behind its request is not read here
        // and lost to the socket.
        var head = new List<byte>();
        var one = new byte[1];
        while (!CollectionsMarshal.AsSpan(head).EndsWith("\r\n\r\n"u8))
        {
            if (head.Count == MaxHead || await stream.ReadAsync(one, cancel) == 0)
            {
                return null;
            }

            head.Add(one[0]);
        }

        return KeyOf(Encoding.ASCII.GetString(CollectionsMarshal.AsSpan(head)));
    }

    public static async Task Accept(Stream stream, string key, CancellationToken cancel) =>
        await stream.WriteAsync(
            Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                + $"Sec-WebSocket-Accept: {AcceptKey(key)}\r\n\r\n"),
            cancel);

    public static async Task Refuse(Stream stream, CancellationToken cancel) =>
        await stream.WriteAsync(
            Encoding.ASCII.GetBytes(
                $"HTTP/1.1 400 Bad Request\r\nContent-Length: {Refusal.Length}\r\nConnection: close\r\n\r\n{Refusal}"),
            cancel);

    [SuppressMessage("Security", "CA5350:Do not use weak cryptographic algorithms", Justification = "RFC 6455 fixes SHA-1 for the accept key.")]
    private static string AcceptKey(string key) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + AcceptGuid)));

    private static string? KeyOf(string head)
    {
        var lines = head.Split("\r\n");
        if (!lines[0].StartsWith("GET ", StringComparison.Ordinal))
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        return Lists(headers, "Upgrade", "websocket")
            && Lists(headers, "Connection", "upgrade")
            && headers.GetValueOrDefault("Sec-WebSocket-Version") == "13"
            ? headers.GetValueOrDefault("Sec-WebSocket-Key")
            : null;
    }

    private static bool Lists(Dictionary<string, string> headers, string name, string item) =>
        headers.TryGetValue(name, out var value)
        && value.Split(',').Any(part => part.Trim().Equals(item, StringComparison.OrdinalIgnoreCase));
}
