using System;

public sealed class ReconnectPolicy
{
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maximumDelay;

    public ReconnectPolicy(TimeSpan? initialDelay = null, TimeSpan? maximumDelay = null)
    {
        _initialDelay = initialDelay ?? TimeSpan.FromSeconds(1);
        _maximumDelay = maximumDelay ?? TimeSpan.FromSeconds(15);

        if (_initialDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(initialDelay));
        if (_maximumDelay < _initialDelay)
            throw new ArgumentOutOfRangeException(nameof(maximumDelay));
    }

    public TimeSpan GetDelay(int failureCount)
    {
        int exponent = Math.Max(0, Math.Min(failureCount - 1, 20));
        double milliseconds = _initialDelay.TotalMilliseconds * Math.Pow(2, exponent);
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, _maximumDelay.TotalMilliseconds));
    }
}
