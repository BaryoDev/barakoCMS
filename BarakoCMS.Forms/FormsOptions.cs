namespace BarakoCMS.Forms;

/// <summary>Settings for the Forms module, bound from <c>Modules:Forms</c>.</summary>
public sealed class FormsOptions
{
    /// <summary>The rate limiter policy the submit endpoint uses.</summary>
    public const string RateLimitPolicy = "forms";

    /// <summary>Submissions one client IP may make per window, across every form. Default 5.</summary>
    public int PermitLimit { get; set; } = 5;

    /// <summary>The rate limit window in seconds. Default 600, ten minutes.</summary>
    public int WindowSeconds { get; set; } = 600;

    /// <summary>The longest string a submitted field may hold. Default 10000 characters.</summary>
    public int MaxFieldLength { get; set; } = 10_000;

    public TurnstileOptions Turnstile { get; set; } = new();

    /// <summary>The rate limiter policy the code request endpoint uses.</summary>
    public const string EmailCodeRateLimitPolicy = "forms-email-code";

    public FormEmailVerificationOptions EmailVerification { get; set; } = new();
}

/// <summary>
/// Limits for the one-time codes a form sends to verify an email field. They apply only to a form
/// that has verification turned on. A value below 1 is read as 1.
/// </summary>
public sealed class FormEmailVerificationOptions
{
    /// <summary>How long a code works for. Default 10 minutes.</summary>
    public int CodeLifetimeMinutes { get; set; } = 10;

    /// <summary>Checks one code survives, right or wrong. Default 5.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Code requests one client IP may make per window, across every form. Default 5.</summary>
    public int RequestsPerClient { get; set; } = 5;

    /// <summary>The window for <see cref="RequestsPerClient"/> in seconds. Default 600.</summary>
    public int RequestWindowSeconds { get; set; } = 600;

    /// <summary>Codes sent to one address per window, across every form of a tenant. Default 5.</summary>
    public int CodesPerAddress { get; set; } = 5;

    /// <summary>Codes one form sends per window, across every address. Default 100.</summary>
    public int CodesPerForm { get; set; } = 100;

    /// <summary>The window for the two stored limits in minutes. Default 60.</summary>
    public int WindowMinutes { get; set; } = 60;
}

/// <summary>Cloudflare Turnstile verification. Off unless <see cref="Enabled"/> is true.</summary>
public sealed class TurnstileOptions
{
    public bool Enabled { get; set; }

    /// <summary>The site secret. Set it through the environment, never in a committed file.</summary>
    public string? SecretKey { get; set; }

    public string VerifyUrl { get; set; } = "https://challenges.cloudflare.com/turnstile/v0/siteverify";
}
