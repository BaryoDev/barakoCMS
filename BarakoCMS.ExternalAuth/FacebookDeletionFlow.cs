using System.Security.Cryptography;
using System.Text;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BarakoCMS.ExternalAuth;

/// <summary>What a person (or the passing of the window) decides about the account.</summary>
internal enum DeletionChoice
{
    Keep,
    Delete,
}

/// <summary>
/// The life of one Facebook deletion request: opening it, and settling the choice about the account
/// when Facebook was the person's only login.
/// </summary>
/// <remarks>
/// Another login means a non-empty password hash or an external identity row. Email sign-in (a
/// one-time code or a magic link) is deliberately not counted: it is open to every account with an
/// address on file, so counting it would leave almost no account "only Facebook" and the deletion
/// Facebook asked for would never reach the account.
///
/// Every settle rechecks the login state, so an account that gained a login or linked Facebook
/// again during the window is kept rather than erased. A request settles once: the caller saves the
/// request document with optimistic concurrency, so of two settles the first save wins, the other
/// fails as a whole (the erasure with it) and the caller reads the winner's status.
/// </remarks>
internal static class FacebookDeletionFlow
{
    public const string ChoiceDaysKey = "Facebook:DeletionChoiceDays";
    public const int DefaultChoiceDays = 30;

    /// <summary>The <see cref="SocialProfile.Source"/> the Facebook callback writes.</summary>
    public const string FacebookProfileSource = "facebook";

    public static string HashOf(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    public static TimeSpan ChoiceWindow(IConfiguration config) =>
        TimeSpan.FromDays(int.TryParse(config[ChoiceDaysKey], out var days) && days > 0 ? days : DefaultChoiceDays);

    /// <summary>Whether the user could still sign in some way other than Facebook.</summary>
    public static async Task<bool> HasOtherLoginAsync(
        IQuerySession session, User user, string? exceptLinkId, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(user.PasswordHash))
        {
            return true;
        }

        var id = user.Id;
        return await session.Query<ExternalIdentity>().AnyAsync(i => i.UserId == id && i.Id != exceptLinkId, ct);
    }

    /// <summary>
    /// Removes the Facebook link and the profile Facebook filled (queued on
    /// <paramref name="session"/>) and returns the request to store. The caller saves.
    /// </summary>
    public static async Task<FacebookDeletionRequest> OpenAsync(
        IDocumentSession session, IConfiguration config, string facebookUserId, string requestId, CancellationToken ct)
    {
        var request = new FacebookDeletionRequest { Id = requestId };
        var linkId = ExternalIdentity.KeyOf(ExternalProviders.Facebook, facebookUserId);
        var link = await session.LoadAsync<ExternalIdentity>(linkId, ct);
        if (link is null)
        {
            return request;
        }

        session.Delete(link);

        // What Facebook shared (name, photo, birthday, location) goes with the request whatever
        // happens to the account. A profile another provider last filled is not Facebook's to
        // remove here.
        var userId = link.UserId;
        session.DeleteWhere<SocialProfile>(p => p.UserId == userId && p.Source == FacebookProfileSource);

        var user = await session.LoadAsync<User>(link.UserId, ct);
        if (user is not null && !await HasOtherLoginAsync(session, user, linkId, ct))
        {
            request.Status = FacebookDeletionStatus.AwaitingChoice;
            request.UserId = user.Id;
            request.ExpiresAt = request.RequestedAt + ChoiceWindow(config);
        }

        return request;
    }

    /// <summary>
    /// Settles a request that is awaiting a choice, queueing any erasure on the session, and returns
    /// the resulting status. A request already settled is returned as it is and nothing is queued.
    /// The caller stores nothing itself: this stores the request. The caller saves.
    /// </summary>
    public static async Task<string> SettleAsync(
        IDocumentSession session, UserEraser eraser, FacebookDeletionRequest request,
        DeletionChoice choice, DateTime now, CancellationToken ct)
    {
        if (request.Status != FacebookDeletionStatus.AwaitingChoice)
        {
            return request.Status;
        }

        var user = request.UserId is { } id ? await session.LoadAsync<User>(id, ct) : null;
        string status;
        if (choice == DeletionChoice.Keep)
        {
            status = FacebookDeletionStatus.Kept;
        }
        else if (user is null)
        {
            status = FacebookDeletionStatus.Completed;
        }
        else if (await HasOtherLoginAsync(session, user, exceptLinkId: null, ct))
        {
            status = FacebookDeletionStatus.Kept;
        }
        else if (await eraser.IsProtectedAsync(session, user, ct))
        {
            status = FacebookDeletionStatus.NeedsAdmin;
        }
        else
        {
            await eraser.QueueEraseAsync(session, user, "facebook_data_deletion", ct);
            status = FacebookDeletionStatus.Completed;
        }

        request.Status = status;
        request.UserId = null;
        request.ExpiresAt = null;
        request.SettledAt = now;
        session.Store(request);
        return status;
    }

    /// <summary>
    /// Queues, on the sign-in's own session, the settling as kept of any request still awaiting a
    /// choice for this user. A person who signs in during the window has answered it: they are
    /// here. Every provider's sign-in goes through the shared path that calls this, so it does not
    /// depend on a link row being stored (an address the operator trusts signs in without one).
    /// </summary>
    /// <remarks>
    /// A statement and not a stored document, for the request's optimistic concurrency. It runs in
    /// the sign-in's commit, touches only a row still awaiting a choice, and changes the row's
    /// version. A settle that raced it and lost fails as a whole on its stale version and reads the
    /// winner, which is how a racing settle is meant to end. A settle that won leaves this
    /// matching no row, so the sign-in is never what fails.
    /// </remarks>
    public static void StageKeepOnSignIn(IDocumentSession session, Guid userId)
    {
        var table = $"{session.DocumentStore.Options.DatabaseSchemaName}.mt_doc_facebook_deletion_requests";
        session.QueueSqlCommand(
            $"""
            update {table}
            set data = (data - 'UserId' - 'ExpiresAt') || jsonb_build_object(
                    'Status', '{FacebookDeletionStatus.Kept}',
                    'SettledAt', to_char(clock_timestamp() at time zone 'utc', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"')),
                mt_version = md5(random()::text || clock_timestamp()::text)::uuid,
                mt_last_modified = transaction_timestamp()
            where data ->> 'UserId' = ? and data ->> 'Status' = '{FacebookDeletionStatus.AwaitingChoice}'
            """,
            userId.ToString());
    }

    /// <summary>
    /// Settles every request whose window has passed, as an unanswered choice to erase. Safe to run
    /// twice or on two nodes at once: a request settles once, and a node that loses the race for one
    /// skips it. Returns how many this call settled.
    /// </summary>
    public static async Task<int> SweepAsync(IServiceProvider services, DateTime now, CancellationToken ct)
    {
        var store = services.GetRequiredService<IDocumentStore>();
        List<string> due;
        await using (var query = store.QuerySession())
        {
            due = [.. await query.Query<FacebookDeletionRequest>()
                .Where(r => r.Status == FacebookDeletionStatus.AwaitingChoice && r.ExpiresAt <= now)
                .Select(r => r.Id)
                .Take(100)
                .ToListAsync(ct)];
        }

        var settled = 0;
        foreach (var id in due)
        {
            await using var scope = services.CreateAsyncScope();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var request = await session.LoadAsync<FacebookDeletionRequest>(id, ct);
            if (request is null || request.Status != FacebookDeletionStatus.AwaitingChoice)
            {
                continue;
            }

            try
            {
                await SettleAsync(session, scope.ServiceProvider.GetRequiredService<UserEraser>(), request,
                    DeletionChoice.Delete, now, ct);
                await session.SaveChangesAsync(ct);
                settled++;
            }
            catch (JasperFx.ConcurrencyException)
            {
                // Another node, or the person, settled it first. Nothing of this attempt was saved.
            }
        }

        return settled;
    }
}

/// <summary>Runs the sweep hourly. The sweep itself is what makes two nodes safe.</summary>
internal sealed class FacebookDeletionSweeper(IServiceProvider services, ILogger<FacebookDeletionSweeper> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var settled = await FacebookDeletionFlow.SweepAsync(services, DateTime.UtcNow, stoppingToken);
                    if (settled > 0)
                    {
                        logger.LogInformation("Settled {Count} Facebook deletion request(s) whose window passed", settled);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The Facebook deletion sweep failed; it runs again next interval");
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }
}
