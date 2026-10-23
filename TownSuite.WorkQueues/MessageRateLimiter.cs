using System.Diagnostics;

namespace TownSuite.WorkQueues;

/// <summary>
/// Token bucket behind <see cref="BatchOptions.MessagesPerSecond"/>, shared by a bus's polling
/// loops. Holds at most one second's worth of tokens (and at least one), so an idle bus can
/// deliver a short burst but never runs above the rate for long.
/// </summary>
internal sealed class MessageRateLimiter
{
    private readonly double _perSecond;
    private readonly double _capacity;
    private readonly object _lock = new();
    private double _tokens;
    private long _lastRefill;

    private MessageRateLimiter(double perSecond)
    {
        _perSecond  = perSecond;
        _capacity   = Math.Max(1.0, perSecond);
        _tokens     = 1.0;
        _lastRefill = Stopwatch.GetTimestamp();
    }

    /// <summary>The limiter for <paramref name="options"/>, or <see langword="null"/> when there is no limit.</summary>
    public static MessageRateLimiter? For(BatchOptions options) =>
        options.MessagesPerSecond > 0 ? new MessageRateLimiter(options.MessagesPerSecond) : null;

    /// <summary>Waits until one token is available and takes it.</summary>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_lock)
            {
                Refill();
                if (_tokens >= 1.0)
                {
                    _tokens -= 1.0;
                    return;
                }
                wait = TimeSpan.FromSeconds((1.0 - _tokens) / _perSecond);
            }
            await Task.Delay(wait < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : wait, cancellationToken);
        }
    }

    /// <summary>Takes up to <paramref name="max"/> tokens without waiting; returns how many it took.</summary>
    public int TryTake(int max)
    {
        if (max <= 0) return 0;
        lock (_lock)
        {
            Refill();
            int taken = (int)Math.Min(max, Math.Floor(_tokens));
            _tokens -= taken;
            return taken;
        }
    }

    /// <summary>Returns tokens that were taken but not used.</summary>
    public void Return(int count)
    {
        if (count <= 0) return;
        lock (_lock) _tokens = Math.Min(_capacity, _tokens + count);
    }

    private void Refill()
    {
        var now = Stopwatch.GetTimestamp();
        _tokens = Math.Min(_capacity, _tokens + Stopwatch.GetElapsedTime(_lastRefill, now).TotalSeconds * _perSecond);
        _lastRefill = now;
    }
}
