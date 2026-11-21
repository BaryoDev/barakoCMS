namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// How long after a rotation the same refresh token may be presented once more (#1391). A client
/// whose answer was lost on the network still holds the old token, and its retry is not reuse.
/// Set <c>Auth:RefreshRetryWindowSeconds</c>: default 30, 0 turns the retry off, at most 300.
/// </summary>
/// <remarks>
/// Never fatal: <see cref="Read"/> says what it changed and <see cref="RefreshRetryWindowCheck"/>
/// logs it once at start. A value above the limit is clamped to it. A negative value or one that is
/// not a whole number falls back to the default.
/// </remarks>
public static class RefreshRetryWindow
{
    public const string Key = "Auth:RefreshRetryWindowSeconds";

    public const int DefaultSeconds = 30;

    /// <summary>Longest window a deployment may ask for.</summary>
    public const int CeilingSeconds = 300;

    /// <summary>The window, with a line for each change made to the configured value.</summary>
    public static TimeSpan Read(IConfiguration configuration, out IReadOnlyList<string> warnings)
    {
        var notes = new List<string>();
        var seconds = Seconds(configuration[Key], notes);
        warnings = notes;
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>The window, for a caller that does not report the warnings.</summary>
    public static TimeSpan Read(IConfiguration configuration) => Read(configuration, out _);

    private static int Seconds(string? raw, List<string> notes)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultSeconds;

        if (!System.Numerics.BigInteger.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            notes.Add($"{Key} is '{raw}', which is not a whole number, so the default {DefaultSeconds} is used.");
            return DefaultSeconds;
        }

        if (parsed.Sign < 0)
        {
            notes.Add($"{Key} is {parsed}, below 0, so the retry is off (0).");
            return 0;
        }

        if (parsed > CeilingSeconds)
        {
            notes.Add($"{Key} is {parsed}, above {CeilingSeconds}, so {CeilingSeconds} is used.");
            return CeilingSeconds;
        }

        return (int)parsed;
    }
}

/// <summary>Logs, once at start, any <c>Auth:RefreshRetryWindowSeconds</c> value that was changed.</summary>
internal sealed class RefreshRetryWindowCheck(
    IConfiguration configuration, ILogger<RefreshRetryWindowCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        RefreshRetryWindow.Read(configuration, out var warnings);
        foreach (var warning in warnings)
            logger.LogWarning("{Warning}", warning);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
