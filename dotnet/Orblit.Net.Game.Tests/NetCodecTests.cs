using System.Text;
using Orblit.VectorMath;

namespace Orblit.Net.Game.Tests;

/// <summary>
/// The body of a call is read from bytes a client chose, so these tests feed it every way a body can
/// be wrong and check it says no without throwing.
/// </summary>
public sealed class NetCodecTests
{
    private const byte Call = 3;

    public static TheoryData<byte[]> BadBodies => new()
    {
        { [] },
        { [Call] },
        { [Call, 0] },
        { [Call, 0, 0] },
        { [Call, 5, (byte)'a'] },
        { [Call, 1, (byte)'a'] },
        { [Call, 1, (byte)'a', 17] },
        { [Call, 1, (byte)'a', 1] },
        { [Call, 1, (byte)'a', 1, 0, 0] },
        { [Call, 1, (byte)'a', 1, 99, 0] },
        { [Call, 1, (byte)'a', 1, 2, 1, 2] },
        { [Call, 1, (byte)'a', 1, 5, 0, 0, 0, 0, 0, 0, 0, 0] },
        { [Call, 1, (byte)'a', 1, 4] },
        { [Call, 1, (byte)'a', 1, 6] },
        { [Call, 1, (byte)'a', 1, 4, 1, 0] },
        { [Call, 1, (byte)'a', 1, 4, 255, 255, 255, 255] },
        { [Call, 1, (byte)'a', 1, 4, 0, 0, 0, 128] },
        { [Call, 1, (byte)'a', 1, 4, 10, 0, 0, 0, (byte)'a'] },
        { [Call, 1, (byte)'a', 1, 4, 5, 0, 0, 0, (byte)'a'] },
        { [Call, 1, (byte)'a', 1, 6, 255, 255, 255, 127] },
        { [Call, 1, (byte)'a', 0, 7] },
        { [Call, 1, (byte)'a', 1, 1, 1, 7] },
    };

    [Fact]
    public void EveryKindOfArgumentReadsBackAsItWasWritten()
    {
        object[] args = [true, -7, 1.5f, "héllo ✓", new Vector3(1.5, -2, 3.25), new NetEntity("crate")];

        var call = Assert.IsType<CallMessage>(NetWire.Read(NetWire.Call("hit", args)));

        Assert.Equal("hit", call.Name);
        Assert.Equal(args, call.Args);
    }

    [Fact]
    public void ACallWithNoArgumentsReadsBack()
    {
        var call = Assert.IsType<CallMessage>(NetWire.Read(NetWire.Call("ping", [])));

        Assert.Equal("ping", call.Name);
        Assert.Empty(call.Args);
    }

    [Fact]
    public void TheEdgesOfEachKindReadBack()
    {
        object[] args = [int.MinValue, int.MaxValue, float.NegativeInfinity, false, string.Empty, new string('x', 10_000)];

        var call = Assert.IsType<CallMessage>(NetWire.Read(NetWire.Call("edges", args)));

        Assert.Equal(args, call.Args);
    }

    [Fact]
    public void ANameOfTwoHundredAndFiftyFiveBytesIsTheLongestAllowed()
    {
        var name = new string('n', 255);

        Assert.Equal(name, Assert.IsType<CallMessage>(NetWire.Read(NetWire.Call(name, []))).Name);
        Assert.Throws<ArgumentException>(() => NetWire.Call(name + "n", []));
        Assert.Throws<ArgumentException>(() => NetWire.Call(new string('é', 128), []));
    }

    [Fact]
    public void AnEmptyNameIsRefused() => Assert.Throws<ArgumentException>(() => NetWire.Call(string.Empty, []));

    [Fact]
    public void SixteenArgumentsAreTheMostACallCarries()
    {
        var sixteen = Enumerable.Range(0, 16).Select(number => (object)number).ToArray();

        Assert.Equal(sixteen, Assert.IsType<CallMessage>(NetWire.Read(NetWire.Call("many", sixteen))).Args);
        Assert.Throws<ArgumentException>(() => NetWire.Call("many", [.. sixteen, 16]));
    }

    [Theory]
    [InlineData(1.5, "Double")]
    [InlineData(1L, "Int64")]
    [InlineData((byte)1, "Byte")]
    public void ATypeACallCannotCarryIsNamedInTheError(object arg, string typeName)
    {
        var error = Assert.Throws<ArgumentException>(() => NetWire.Call("x", [arg]));

        Assert.Contains(typeName, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullArgumentIsRefused()
    {
        var error = Assert.Throws<ArgumentException>(() => NetWire.Call("x", [null!]));

        Assert.Contains("null", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BadBodies))]
    public void ABadBodyReadsAsNothingAndDoesNotThrow(byte[] body) => Assert.Null(NetWire.Read(body));

    [Fact]
    public void SeventeenArgumentsInABodyAreRefusedAndSixteenAreNot()
    {
        Assert.NotNull(NetWire.Read(IntsBody(16)));
        Assert.Null(NetWire.Read(IntsBody(17)));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(255, true)]
    public void AnyByteThatIsNotZeroReadsAsTrue(byte value, bool expected)
    {
        var call = Assert.IsType<CallMessage>(NetWire.Read([Call, 1, (byte)'a', 1, 1, value]));

        Assert.Equal(expected, Assert.IsType<bool>(Assert.Single(call.Args)));
    }

    [Fact]
    public void BytesPastTheLastArgumentAreRefused()
    {
        var body = NetWire.Call("a", [1]);

        Assert.NotNull(NetWire.Read(body));
        Assert.Null(NetWire.Read([.. body, 0]));
    }

    [Fact]
    public void EachKindHasItsOwnNumberOnTheWire()
    {
        var body = NetWire.Call("a", [true, 1, 1f, "s", new Vector3(0, 0, 0), new NetEntity("e")]);

        Assert.Equal(1, body[4]);
        Assert.Equal(2, body[6]);
        Assert.Equal(3, body[11]);
        Assert.Equal(4, body[16]);
        Assert.Equal(5, body[22]);
        Assert.Equal(6, body[22 + 25]);
        Assert.Equal("e", Encoding.UTF8.GetString(body.AsSpan(^1)));
    }

    private static byte[] IntsBody(int count) =>
        [Call, 1, (byte)'a', (byte)count, .. Enumerable.Range(0, count).SelectMany(_ => new byte[] { 2, 0, 0, 0, 0 })];

    [Fact]
    public void ASpawnBodyIsNotACall() =>
        Assert.IsNotType<CallMessage>(NetWire.Read(NetWire.Owner(1, "p1")));
}
