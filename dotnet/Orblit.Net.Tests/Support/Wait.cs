namespace Orblit.Net.Tests;

internal static class Wait
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>Waits for a condition rather than sleeping a guessed time, so a slow machine does not flake it.</summary>
    public static async Task Until(Func<bool> done, string reason)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (done())
            {
                return;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Timed out waiting for {reason}.");
    }

    /// <summary>The next <paramref name="count"/> messages the transport hears, once they have all arrived.</summary>
    public static Task<List<byte[]>> Hear(SocketTransport transport, int count)
    {
        var heard = new List<byte[]>();
        var done = new TaskCompletionSource<List<byte[]>>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Listen(message =>
        {
            lock (heard)
            {
                heard.Add(message);
                if (heard.Count == count)
                {
                    done.TrySetResult(heard);
                }
            }
        });
        return done.Task.WaitAsync(Patience);
    }
}
