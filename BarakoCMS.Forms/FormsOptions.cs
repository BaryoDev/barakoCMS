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

    /// <summary>
    /// The limit <see cref="PerForm"/> gives this slug, matched in any case, or null when it names
    /// no such form. <c>Form</c> is the configured spelling, not the one asked for.
    /// </summary>
    internal (string Form, int PermitLimit, int WindowSeconds)? OwnLimit(string? slug)
    {
        foreach (var (form, own) in PerForm)
        {
            if (own is not null && string.Equals(form, slug, StringComparison.OrdinalIgnoreCase))
            {
                return (form, own.PermitLimit ?? PermitLimit, own.WindowSeconds ?? WindowSeconds);
            }
        }

        return null;
    }

    /// <summary>
    /// Refuses a <see cref="PerForm"/> value that is not a whole number above zero, naming the setting.
    /// </summary>
    /// <remarks>
    /// Run when the module registers its services, so a mistyped limit stops the host. Left to the
    /// binder it would fail on the first submission instead, inside the limiter, for every form. The
    /// rules are the ones core applies to its own limits. The shared <see cref="PermitLimit"/> and
    /// <see cref="WindowSeconds"/> are not checked here: they have always been raised to 1 when lower,
    /// and a host that sets one to zero starts today.
    /// </remarks>
    /// <param name="configuration">The module's own section, <c>Modules:Forms</c>.</param>
    internal static void RequireValidPerForm(IConfiguration configuration)
    {
        foreach (var form in configuration.GetSection(nameof(PerForm)).GetChildren())
        {
            foreach (var key in new[] { nameof(FormRateLimit.PermitLimit), nameof(FormRateLimit.WindowSeconds) })
            {
                var raw = form[key];
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var setting = $"Modules:Forms:{nameof(PerForm)}:{form.Key}:{key}";
                if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
                {
                    throw new InvalidOperationException($"{setting} is '{raw}', which is not a whole number.");
                }

                if (value <= 0)
                {
                    throw new InvalidOperationException($"{setting} is {value}, and it must be greater than zero.");
                }
            }
        }
    }

    /// <summary>The rate limiter policy the code request endpoint uses.</summary>
    public const string EmailCodeRateLimitPolicy = "forms-email-code";

    public FormEmailVerificationOptions EmailVerification { get; set; } = new();

    /// <summary>
    /// Refuses an <c>EmailVerification</c> setting below 1, naming it, so the host does not start
    /// with a limit that reads as off and behaves as a lockout.
    /// </summary>
    /// <param name="configuration">The module's own section, <c>Modules:Forms</c>.</param>
    public static void RequireValidEmailVerification(IConfiguration configuration)
    {
        var settings = configuration.GetSection(nameof(EmailVerification)).Get<FormEmailVerificationOptions>()
            ?? new FormEmailVerificationOptions();

        foreach (var (name, value) in settings.Values())
        {
            if (value < 1)
            {
                throw new InvalidOperationException(
                    $"Modules:Forms:{nameof(EmailVerification)}:{name} is {value}, and it must be at least 1. "
                  + "A limit cannot be turned off by setting it to zero. To stop verifying, turn it off on the form.");
            }
        }
    }
}

/// <summary>
/// One form's own submit limit. A value left out takes the shared one from <see cref="FormsOptions"/>,
/// as configured.
/// </summary>
public sealed class FormRateLimit
{
    /// <summary>Submissions one client IP may make to this form per window.</summary>
    public int? PermitLimit { get; set; }

    /// <summary>The window in seconds.</summary>
    public int? WindowSeconds { get; set; }
}

/// <summary>
/// Limits for the one-time codes a form sends to verify an email field. They apply only to a form
/// that has verification turned on. A value below 1 stops the host at startup, see
/// <see cref="FormsOptions.RequireValidEmailVerification"/>.
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

    /// <summary>
    /// How long a code request waits for the email provider before it gives up and answers. Default
    /// 10 seconds. The provider is stopped through its cancellation token, so this holds for a
    /// provider that honours one.
    /// </summary>
    public int SendTimeoutSeconds { get; set; } = 10;

    internal IEnumerable<(string Name, int Value)> Values() =>
    [
        (nameof(CodeLifetimeMinutes), CodeLifetimeMinutes),
        (nameof(MaxAttempts), MaxAttempts),
        (nameof(RequestsPerClient), RequestsPerClient),
        (nameof(RequestWindowSeconds), RequestWindowSeconds),
        (nameof(CodesPerAddress), CodesPerAddress),
        (nameof(CodesPerForm), CodesPerForm),
        (nameof(WindowMinutes), WindowMinutes),
        (nameof(SendTimeoutSeconds), SendTimeoutSeconds),
    ];
}

/// <summary>Cloudflare Turnstile verification. Off unless <see cref="Enabled"/> is true.</summary>
public sealed class TurnstileOptions
{
    public bool Enabled { get; set; }

    /// <summary>The site secret. Set it through the environment, never in a committed file.</summary>
    public string? SecretKey { get; set; }

    public string VerifyUrl { get; set; } = "https://challenges.cloudflare.com/turnstile/v0/siteverify";
}
