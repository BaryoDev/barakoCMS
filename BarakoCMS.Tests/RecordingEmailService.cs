using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using barakoCMS.Core.Interfaces;

namespace BarakoCMS.Tests;

/// <summary>
/// The test host's email transport. It delivers nothing and records what it was asked to send.
/// </summary>
/// <remarks>
/// Registered by <see cref="IntegrationTestFixture"/> in place of the Resend provider, which throws
/// on every call because no API key is configured. Nothing is asserted about mail having been sent:
/// this exists so a test can read a token that only ever exists in an email, which is the only way
/// to drive registration the way a person does.
/// </remarks>
public sealed class RecordingEmailService : IEmailService
{
    public sealed record Sent(string To, string Subject, string Body);

    private readonly ConcurrentQueue<Sent> _sent = new();

    public IReadOnlyCollection<Sent> Messages => _sent.ToArray();

    public Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        _sent.Enqueue(new Sent(to.Trim().ToLowerInvariant(), subject, body));
        return Task.CompletedTask;
    }

    /// <summary>The most recent registration token emailed to <paramref name="email"/>, or null.</summary>
    public string? LastVerificationTokenFor(string email)
    {
        var to = email.Trim().ToLowerInvariant();

        return _sent.Reverse()
            .Where(s => s.To == to)
            .Select(s => TokenPattern.Match(s.Body))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .FirstOrDefault();
    }

    /// <summary>
    /// {32 hex}.{base64url secret}, the shape EmailVerificationToken.Create produces. Anchored on the
    /// hex-and-dot prefix so it cannot match ordinary prose in the message around it.
    /// </summary>
    private static readonly Regex TokenPattern =
        new(@"\b([0-9a-f]{32}\.[A-Za-z0-9_-]{20,})", RegexOptions.Compiled);
}
