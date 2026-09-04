namespace barakoCMS.Infrastructure.Jobs;

/// <summary>Exponential backoff with a cap: base, 2x base, 4x base, up to the max.</summary>
public static class JobBackoff
{
    /// <param name="attempt">How many attempts have failed so far, counting the one that just did.</param>
    public static TimeSpan DelayFor(int attempt, int baseSeconds, int maxSeconds)
    {
        if (attempt < 1) attempt = 1;

        // Past 2^30 the doubling has long since hit any sane cap, and shifting by more than 62
        // would wrap the long.
        var exponent = Math.Min(attempt - 1, 30);
        var seconds = Math.Min((long)baseSeconds << exponent, maxSeconds);
        return TimeSpan.FromSeconds(seconds);
    }
}
