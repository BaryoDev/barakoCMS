using System.Collections.Concurrent;
using barakoCMS.Core.Interfaces;

namespace BarakoCMS.Testing.Fixture;

/// <summary>
/// An email provider double. It delivers nothing and records each message, so a test can stand in
/// for a provider module and read what the core asked it to send.
/// </summary>
public sealed class RecordingEmailProvider : IEmailService
{
    private readonly ConcurrentQueue<SentEmail> _sent = new();

    public IReadOnlyList<SentEmail> Sent => [.. _sent];

    public Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        _sent.Enqueue(new SentEmail(null, to, subject, body));
        return Task.CompletedTask;
    }

    public Task SendForTenantAsync(string tenant, string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        _sent.Enqueue(new SentEmail(tenant, to, subject, body));
        return Task.CompletedTask;
    }
}

/// <summary>One message a <see cref="RecordingEmailProvider"/> was asked to send. Tenant is null when none was named.</summary>
public sealed record SentEmail(string? Tenant, string To, string Subject, string Body);
