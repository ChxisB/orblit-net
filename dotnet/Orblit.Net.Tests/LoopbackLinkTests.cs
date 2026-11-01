namespace Orblit.Net.Tests;

public sealed class LoopbackLinkTests
{
    [Fact]
    public async Task MessagesWithLatencyArriveInTheOrderTheyWereSent()
    {
        const int count = 300;
        var link = new LoopbackLink(TimeSpan.FromMilliseconds(5));
        var heard = new List<int>();
        using var listening = link.Client.Listen(message => heard.Add(message[0] | (message[1] << 8)));

        for (var i = 0; i < count; i++)
        {
            link.Host.Send([(byte)i, (byte)(i >> 8)]);
        }

        await link.IdleAsync();

        Assert.Equal(Enumerable.Range(0, count), heard);
    }
}
