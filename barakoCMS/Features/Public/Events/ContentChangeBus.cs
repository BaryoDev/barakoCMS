using System.Text.Json;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using ContentDoc = barakoCMS.Models.Content;

namespace barakoCMS.Features.Public.Events;

/// <summary>
/// Carries a committed content change from the instance that wrote it to the stream subscribers on
/// every other instance, over Postgres LISTEN / NOTIFY (#566).
/// </summary>
/// <remarks>
/// The notice holds the tenant, the content id and the event name. It never holds a field value or
/// a masking level. A receiving instance reads the entry and the type definition again in the
/// tenant's partition and projects it through <see cref="PublicDelivery.ToPublic"/>, so masking is
/// derived from the definition as it stands now, in the one place the REST reads use it. Anything
/// that can NOTIFY on the channel can say what it likes, and a notice can be old by the time it is
/// read, so nothing in it is allowed to loosen a level. The one thing a forged notice can still do is
/// name an unpublish for a tenant entry that was never public, which hands out its id and slug to that
/// tenant's own subscribers; nothing stored says an entry was ever public short of replaying its
/// events, so this is a consequence of trusting the channel.
///
/// Best effort, as the stream always was. NOTIFY is not queued for a listener that is down or
/// reconnecting, nothing orders notices across instances, and a notice that cannot be read again is
/// dropped. The sender hears its own notices and ignores them, because it already delivered the
/// change to its own subscribers.
///
/// Off with the stream: when <c>Delivery:Events:Enabled</c> is false nothing is sent and nothing is
/// listened for. The store is resolved on use rather than injected, because this is reached from a
/// listener that the store is built with.
/// </remarks>
internal sealed class ContentChangeBus(
    IServiceProvider services,
    ContentEventsOptions options,
    ContentChangeBroadcaster broadcaster,
    ILogger<ContentChangeBus> logger) : BackgroundService
{
    public const string Channel = "barako_content_events";

    // Postgres refuses a payload of 8000 bytes or more. A change is about 100 bytes,
    // so twenty to a notice stays well inside the limit.
    internal const int ChangesPerNotice = 20;

    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);
    private const int InboxCapacity = 1000;

    private int _dropWarned;

    private readonly Guid _node = Guid.NewGuid();
    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Enabled => options.Enabled;

    /// <summary>Completes once this instance is listening, so a test can write after that point.</summary>
    internal Task Listening => _listening.Task;

    public async Task PublishAsync(string tenant, IReadOnlyList<BusChange> changes, CancellationToken ct)
    {
        foreach (var batch in changes.Chunk(ChangesPerNotice))
        {
            var notice = new Notice(_node, tenant, batch.ToList());
            var payload = JsonSerializer.Serialize(notice);

            var store = services.GetRequiredService<IDocumentStore>();
            await using var connection = store.Storage.Database.CreateConnection();
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "select pg_notify(@channel, @payload)";
            command.Parameters.AddWithValue("channel", Channel);
            command.Parameters.AddWithValue("payload", payload);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Content event bus lost its connection; other instances' changes are not streamed until it is back");
            }

            try
            {
                await Task.Delay(Retry, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        var store = services.GetRequiredService<IDocumentStore>();
        await using var connection = store.Storage.Database.CreateConnection();
        await connection.OpenAsync(ct);

        var inbox = System.Threading.Channels.Channel.CreateBounded<string>(
            new System.Threading.Channels.BoundedChannelOptions(InboxCapacity)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            _ =>
            {
                if (Interlocked.Exchange(ref _dropWarned, 1) == 0)
                {
                    logger.LogWarning("Content event bus inbox is full; the oldest notices are dropped (logged once)");
                }
            });
        connection.Notification += (_, e) => inbox.Writer.TryWrite(e.Payload);

        await using (var listen = connection.CreateCommand())
        {
            listen.CommandText = "listen " + Channel;
            await listen.ExecuteNonQueryAsync(ct);
        }

        _listening.TrySetResult();

        var pump = PumpAsync(inbox.Reader, ct);
        try
        {
            while (true)
            {
                if (!await connection.WaitAsync(KeepAlive, ct))
                {
                    // Quiet for a while: a dead connection throws here and the caller reconnects.
                    await using var ping = connection.CreateCommand();
                    ping.CommandText = "select 1";
                    await ping.ExecuteNonQueryAsync(ct);
                }
            }
        }
        finally
        {
            inbox.Writer.TryComplete();
            await pump.ConfigureAwait(false);
        }
    }

    private async Task PumpAsync(System.Threading.Channels.ChannelReader<string> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var payload in reader.ReadAllAsync(ct))
            {
                try
                {
                    await ReceiveAsync(payload, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Content event bus could not turn a notice into a stream change; it is dropped");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Handles one notice from the channel. Internal so a test can deliver one directly.</summary>
    internal async Task ReceiveAsync(string payload, CancellationToken ct)
    {
        var notice = JsonSerializer.Deserialize<Notice>(payload);
        if (notice is null || notice.Node == _node || string.IsNullOrEmpty(notice.Tenant)
            || notice.Changes is not { Count: > 0 }
            || !broadcaster.HasSubscribers(notice.Tenant))
        {
            return;
        }

        using var scope = services.CreateScopeForTenant(notice.Tenant);
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var definitions = new Dictionary<string, ContentTypeDefinition?>(StringComparer.Ordinal);
        var ready = new List<(string Name, ContentChange? Change, ContentDoc? Content, ContentTypeDefinition? Definition)>();

        foreach (var change in notice.Changes.Where(c => c is not null).Take(ChangesPerNotice))
        {
            var content = await session.LoadAsync<ContentDoc>(change.Id, ct);
            if (content is null)
            {
                continue;
            }

            if (!definitions.TryGetValue(content.ContentType, out var def))
            {
                def = await session.Query<ContentTypeDefinition>()
                    .FirstOrDefaultAsync(d => d.Name == content.ContentType, ct);
                definitions[content.ContentType] = def;
            }

            if (!PublicDelivery.IsDeliverable(def))
            {
                continue;
            }

            if (change.Name == ContentChangeEvents.Unpublished)
            {
                ready.Add((change.Name, ContentChangeListener.Unpublish(content, PublicDelivery.SlugField(def!)), null, null));
                continue;
            }

            // Public now or not at all: an entry that has since left public is covered by the
            // unpublish its own commit sent.
            if (ContentChangeListener.Project(content, def!) is not null)
            {
                ready.Add((change.Name, null, content, def));
            }
        }

        await PublishAsync(session, notice.Tenant, ready, ct);
    }

    private async Task PublishAsync(
        IDocumentSession session,
        string tenant,
        List<(string Name, ContentChange? Change, ContentDoc? Content, ContentTypeDefinition? Definition)> ready,
        CancellationToken ct)
    {
        var entries = ready
            .Where(r => r.Content is not null)
            .Select(r => (Item: ContentChangeListener.Project(r.Content!, r.Definition!)!, Definition: r.Definition!))
            .ToList();
        var checkedEntries = new Queue<PublicContentResponse>(
            await ContentChangeListener.CheckReferencesAsync(session, entries, logger, ct));

        foreach (var item in ready)
        {
            if (item.Change is not null)
            {
                broadcaster.Publish(tenant, item.Change);
                continue;
            }

            var entry = checkedEntries.Dequeue();
            broadcaster.Publish(tenant, new ContentChange(item.Name, entry.Id, entry.ContentType, entry.Slug, entry));
        }
    }

    private sealed record Notice(Guid Node, string Tenant, List<BusChange> Changes);
}

/// <summary>One change in a notice: which entry and what happened. Nothing that decides masking.</summary>
internal sealed record BusChange(Guid Id, string Name);
