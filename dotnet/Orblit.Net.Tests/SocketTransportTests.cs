namespace Orblit.Net.Tests;

public sealed class SocketTransportTests : IAsyncLifetime
{
    private readonly EchoServer _server = new();
    private readonly List<SocketTransport> _opened = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var transport in _opened)
        {
            await transport.CloseAsync();
        }

        await _server.DisposeAsync();
    }

    private async Task<SocketTransport> Connected()
    {
        var transport = await SocketTransport.ConnectAsync(_server.Echo);
        _opened.Add(transport);
        return transport;
    }

    [Fact]
    public async Task ABinaryMessageComesBackWhole()
    {
        var transport = await Connected();
        var reply = Wait.Hear(transport, 1);

        transport.Send([1, 2, 3]);

        Assert.Equal([1, 2, 3], Assert.Single(await reply));
    }

    [Fact]
    public async Task ALargeMessageIsNotSplit()
    {
        var transport = await Connected();
        var big = new byte[200000];
        for (var i = 0; i < big.Length; i++)
        {
            big[i] = (byte)(i % 251);
        }

        var reply = Wait.Hear(transport, 1);

        transport.Send(big);

        Assert.Equal(big, Assert.Single(await reply));
    }

    [Fact]
    public async Task AMessageIsCopiedWhenSentSoTheBufferCanBeReused()
    {
        var transport = await Connected();
        var buffer = new byte[] { 7, 7, 7 };
        var reply = Wait.Hear(transport, 1);

        transport.Send(buffer);
        Array.Clear(buffer);

        Assert.Equal([7, 7, 7], Assert.Single(await reply));
    }

    [Fact]
    public async Task TextFramesAreDroppedAndBinaryOnesKeepTheirOrder()
    {
        var transport = await Connected();
        var replies = Wait.Hear(transport, 2);

        transport.Send([1]);
        transport.Send([2]);

        Assert.Equal([new byte[] { 1 }, [2]], await replies);
    }

    [Fact]
    public async Task TheTransportEndsWhenThePeerCloses()
    {
        var transport = await Connected();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Listen(_ => { }, onDone: () => ended.TrySetResult());

        transport.Send([EchoServer.HangUp]);
        await transport.Done.WaitAsync(Wait.Patience, TestContext.Current.CancellationToken);
        await ended.Task.WaitAsync(Wait.Patience, TestContext.Current.CancellationToken);

        Assert.False(transport.IsOpen);
        transport.Send([1]);
    }

    [Fact]
    public async Task ClosingEndsTheListenersAndASecondCloseDoesNothing()
    {
        var transport = await Connected();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Listen(_ => { }, onDone: () => ended.TrySetResult());

        await transport.CloseAsync();
        await ended.Task.WaitAsync(Wait.Patience, TestContext.Current.CancellationToken);

        Assert.False(transport.IsOpen);
        await transport.CloseAsync();
    }

    [Fact]
    public async Task ClosingDoesNotWaitForAPeerThatHasStoppedReading()
    {
        var transport = await Connected();
        transport.Send([EchoServer.Stall]);

        await transport.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(transport.IsOpen);
    }

    [Fact]
    public async Task ConnectingToAPortNobodyListensOnFailsBeforeTheTimeout()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => SocketTransport.ConnectAsync(_server.Dead, TimeSpan.FromSeconds(3)));

        Assert.IsNotType<TimeoutException>(error);
    }

    [Fact]
    public async Task AnAddressTheTransportCannotUseFailsAsAnException()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => SocketTransport.ConnectAsync(new Uri("ftp://127.0.0.1:1")));
    }

    [Fact]
    public async Task ConnectingToAPeerThatNeverAnswersTimesOut()
    {
        await Assert.ThrowsAsync<TimeoutException>(
            () => SocketTransport.ConnectAsync(_server.Blackhole, TimeSpan.FromMilliseconds(200)));
    }
}
