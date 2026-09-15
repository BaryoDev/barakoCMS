using System.Threading.Channels;
using Marten;
using NpgsqlTypes;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// Locks an account after too many failed sign-in attempts, and tells its owner once per lock.
/// Password sign-in and MFA verification both count toward the same lock, so both come through here.
/// </summary>
/// <remarks>
/// <para>
/// A sign-in against a locked account answers exactly as a wrong password does (#640), so the
/// owner's registered address is the one place a lock is reported.
/// </para>
/// <para>
/// The lock is set by a conditional update that applies only when the account is not already locked,
/// and the notice goes out only when that update changed the row. Concurrent failures each see the
/// counter past the threshold, and without the condition every one of them sent a notice.
/// </para>
/// <para>
/// The notice is queued, not sent on the request. A send that took as long as the provider took made
/// the attempt that locks an account measurably slower than any other refused sign-in, which is an
/// answer to "does this account exist" read off a stopwatch. <see cref="LockoutNoticeSender"/> sends
/// it after the response, from its own scope.
/// </para>
/// </remarks>
internal sealed class AccountLockout
{
    public const int MaxFailedAttempts = 5;

    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly IDocumentStore _store;
    private readonly LockoutNoticeSender _notices;

    public AccountLockout(IDocumentStore store, LockoutNoticeSender notices)
    {
        _store = store;
        _notices = notices;
    }

    /// <summary>
    /// Locks <paramref name="user"/> for <see cref="LockoutDuration"/> unless it is locked already.
    /// </summary>
    /// <returns>True when this call set the lock, and so queued the owner's notice.</returns>
    public async Task<bool> TryLockAsync(User user, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var until = now.Add(LockoutDuration);

        await using var connection = _store.Storage.Database.CreateConnection();
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        // The value is written through Marten's serializer so it reads back exactly as a Patch would
        // have stored it. The comparison runs in the same statement as the write, so two requests
        // racing past the threshold cannot both see the account unlocked.
        command.CommandText =
            $"update {_store.Options.DatabaseSchemaName}.mt_doc_users "
          + "set data = jsonb_set(data, '{LockoutUntil}', @until) "
          + "where id = @id and (data ->> 'LockoutUntil' is null or (data ->> 'LockoutUntil')::timestamptz <= @now)";
        command.Parameters.AddWithValue("until", NpgsqlDbType.Jsonb, _store.Options.Serializer().ToJson(until));
        command.Parameters.AddWithValue("id", user.Id);
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);

        if (await command.ExecuteNonQueryAsync(ct) == 0)
        {
            return false;
        }

        _notices.Enqueue(user.Id, user.Email);
        return true;
    }
}

/// <summary>
/// Sends lockout notices in the background, one at a time, each from its own DI scope.
/// </summary>
/// <remarks>
/// Bounded, because the queue is filled by anonymous requests. One notice per lock and the per-IP
/// auth limit keep it small in practice. When it is full a notice is dropped and logged rather than
/// the request waiting. Notices still queued when the host stops are not sent.
/// </remarks>
internal sealed class LockoutNoticeSender : BackgroundService
{
    internal const int Capacity = 1000;

    internal static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    private readonly Channel<(Guid UserId, string Email)> _queue =
        Channel.CreateBounded<(Guid UserId, string Email)>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<LockoutNoticeSender> _logger;

    public LockoutNoticeSender(IServiceScopeFactory scopes, IConfiguration config, ILogger<LockoutNoticeSender> logger)
    {
        _scopes = scopes;
        _config = config;
        _logger = logger;
    }

    public void Enqueue(Guid userId, string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        if (!_queue.Writer.TryWrite((userId, email)))
        {
            _logger.LogError("The lockout notice queue is full; not sending the notice for user {UserId}", userId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var (userId, email) in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await SendAsync(userId, email, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task SendAsync(Guid userId, string email, CancellationToken stoppingToken)
    {
        var appName = System.Net.WebUtility.HtmlEncode(_config["Branding:AppName"] ?? "BarakoCMS");
        var minutes = (int)AccountLockout.LockoutDuration.TotalMinutes;
        var body =
            $"<p>Your {appName} account was locked for {minutes} minutes after too many failed sign-in attempts.</p>"
          + "<p>While it is locked, sign-in answers as if the password were wrong, even when it is right. "
          + "Wait for the lock to pass, or sign in with an emailed code instead.</p>"
          + "<p>If these attempts were not yours, somebody may be guessing your password. Consider changing it "
          + "once you are back in.</p>";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(SendTimeout);

        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<IEmailService>();
            await sender.SendEmailAsync(email, $"Your {appName} account was locked", body, timeout.Token);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Could not send the lockout notice for user {UserId}", userId);
        }
    }
}
