using Microsoft.Extensions.Configuration;

namespace barakoCMS.Infrastructure.Filters;

/// <summary>How long an inbound Idempotency-Key is honoured, and how much of a response is kept to replay.</summary>
internal sealed class IdempotencyOptions
{
    public const string KeyHoursKey = "Idempotency:KeyHours";
    public const string MaxStoredResponseBytesKey = "Idempotency:MaxStoredResponseBytes";

    public const int DefaultKeyHours = 24;
    public const int MinKeyHours = 1;

    /// <summary>Thirty days. Past that a client is not retrying, it is reusing a key for new work.</summary>
    public const int MaxKeyHours = 720;

    public const int DefaultMaxStoredResponseBytes = 64 * 1024;
    public const int MaxMaxStoredResponseBytes = 1024 * 1024;

    public int KeyHours { get; init; } = DefaultKeyHours;

    /// <summary>
    /// The largest response body kept for replay. A bigger one is not stored, and a replay of its key
    /// answers 409. Zero keeps no bodies at all.
    /// </summary>
    public int MaxStoredResponseBytes { get; init; } = DefaultMaxStoredResponseBytes;

    public TimeSpan KeyLifetime => TimeSpan.FromHours(KeyHours);

    public static IdempotencyOptions FromConfiguration(IConfiguration configuration) => new()
    {
        KeyHours = configuration.GetValue(KeyHoursKey, DefaultKeyHours),
        MaxStoredResponseBytes = configuration.GetValue(MaxStoredResponseBytesKey, DefaultMaxStoredResponseBytes),
    };

    public void Validate()
    {
        if (KeyHours is < MinKeyHours or > MaxKeyHours)
            throw new InvalidOperationException(
                $"{KeyHoursKey} must be between {MinKeyHours} and {MaxKeyHours}, and is {KeyHours}.");
        if (MaxStoredResponseBytes is < 0 or > MaxMaxStoredResponseBytes)
            throw new InvalidOperationException(
                $"{MaxStoredResponseBytesKey} must be between 0 and {MaxMaxStoredResponseBytes}, and is {MaxStoredResponseBytes}.");
    }
}
