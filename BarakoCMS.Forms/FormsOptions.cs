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

    /// <summary>
    /// A limit of its own for one form, keyed by the form's slug. A form named here is counted per
    /// client IP against these numbers and no longer against the shared ones above. Empty by
    /// default, which leaves every form on the shared limit.
    /// </summary>
    public Dictionary<string, FormRateLimit> PerForm { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The longest string a submitted field may hold. Default 10000 characters.</summary>
    public int MaxFieldLength { get; set; } = 10_000;

    public TurnstileOptions Turnstile { get; set; } = new();
}

/// <summary>One form's own submit limit. A value left out takes the shared default.</summary>
public sealed class FormRateLimit
{
    /// <summary>Submissions one client IP may make to this form per window. Default 5.</summary>
    public int PermitLimit { get; set; } = 5;

    /// <summary>The window in seconds. Default 600, ten minutes.</summary>
    public int WindowSeconds { get; set; } = 600;
}

/// <summary>Cloudflare Turnstile verification. Off unless <see cref="Enabled"/> is true.</summary>
public sealed class TurnstileOptions
{
    public bool Enabled { get; set; }

    /// <summary>The site secret. Set it through the environment, never in a committed file.</summary>
    public string? SecretKey { get; set; }

    public string VerifyUrl { get; set; } = "https://challenges.cloudflare.com/turnstile/v0/siteverify";
}
