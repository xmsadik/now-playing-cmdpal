using Xunit;
using NowPlaying.Core;

namespace NowPlaying.Tests;

public class CoalescerTests
{
    [Fact]
    public async Task BurstFiresOnce()
    {
        var count = 0;
        using var c = new Coalescer(() => Interlocked.Increment(ref count), TimeSpan.FromMilliseconds(50));
        for (var i = 0; i < 10; i++)
        {
            c.Trigger();
            await Task.Delay(5);
        }

        await Task.Delay(400);
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public async Task SeparatedTriggersFireTwice()
    {
        var count = 0;
        using var c = new Coalescer(() => Interlocked.Increment(ref count), TimeSpan.FromMilliseconds(30));
        c.Trigger();
        await Task.Delay(300);
        c.Trigger();
        await Task.Delay(300);
        Assert.Equal(2, Volatile.Read(ref count));
    }

    [Fact]
    public async Task ThrowingActionDoesNotKillTimer()
    {
        var count = 0;
        using var c = new Coalescer(
            () =>
            {
                Interlocked.Increment(ref count);
                throw new InvalidOperationException();
            },
            TimeSpan.FromMilliseconds(20));
        c.Trigger();
        await Task.Delay(200);
        c.Trigger();
        await Task.Delay(200);
        Assert.Equal(2, Volatile.Read(ref count));
    }

    [Fact]
    public async Task DisposedNeverFires()
    {
        var count = 0;
        var c = new Coalescer(() => Interlocked.Increment(ref count), TimeSpan.FromMilliseconds(30));
        c.Trigger();
        c.Dispose();
        c.Trigger();
        await Task.Delay(200);
        Assert.Equal(0, Volatile.Read(ref count));
    }
}
