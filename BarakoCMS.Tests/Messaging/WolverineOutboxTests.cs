using System.Net;
using System.Net.Http.Json;
using barakoCMS.Infrastructure.Http;
using barakoCMS.Infrastructure.Messaging;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Models;
using BarakoCMS.Tests.Features.Workflows;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests.Messaging;

/// <summary>
/// The #687 spike, question by question. The same questions <c>TransactionalEnqueueTests</c> asks
/// of the hand-rolled queue, asked of Wolverine's outbox underneath a FastEndpoints endpoint.
/// </summary>
[Collection("Sequential")]
public class WolverineOutboxTests
{
    private readonly IntegrationTestFixture _fixture;

    public WolverineOutboxTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static readonly TimeSpan HandledTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(3);

    /// <summary>The control for the two rollback tests: the lookup below can find a handled message.</summary>
    [Fact]
    public async Task A_message_published_in_a_request_that_commits_is_handled()
    {
        var marker = Marker();

        var response = await _fixture.CreateClient().PostAsync(
            $"/api/_test/messages/publish-then-commit?message={marker}", null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await WaitUntilAsync(() => Recorder(_fixture).Has($"{marker}@default"), HandledTimeout,
            $"the committed message {marker} was never handled in the default tenant");
    }

    [Fact]
    public async Task A_message_published_in_a_request_that_throws_before_commit_is_never_stored_or_handled()
    {
        var control = Marker();
        var marker = Marker();
        var client = _fixture.CreateClient();

        var committed = await client.PostAsync(
            $"/api/_test/messages/publish-then-commit?message={control}", null, TestContext.Current.CancellationToken);
        committed.StatusCode.Should().Be(HttpStatusCode.OK);
        await WaitUntilAsync(() => Recorder(_fixture).Has($"{control}@default"), HandledTimeout, "the control proves the lookup works");

        await PostExpectingFailureAsync(client, $"/api/_test/messages/publish-then-throw?message={marker}");
        await Task.Delay(SettleTime, TestContext.Current.CancellationToken);

        Recorder(_fixture).Has($"{marker}@default").Should().BeFalse("the request never committed, so nothing may have run");
        (await EnvelopesHoldingAsync(marker)).Should().BeEmpty("the outbox row rides the request's transaction");
    }

    /// <summary>
    /// The harder direction: PublishAsync returned successfully, then the write it belongs to failed
    /// at commit on a unique index. The message must go with it.
    /// </summary>
    [Fact]
    public async Task A_message_whose_request_fails_at_commit_is_rolled_back_with_the_rest_of_the_write()
    {
        var marker = Marker();

        await PostExpectingFailureAsync(_fixture.CreateClient(), $"/api/_test/messages/publish-then-fail-commit?message={marker}");
        await Task.Delay(SettleTime, TestContext.Current.CancellationToken);

        Recorder(_fixture).Has($"{marker}@default").Should().BeFalse("the duplicate role and the message were one transaction");
        (await EnvelopesHoldingAsync(marker)).Should().BeEmpty();
    }

    /// <summary>
    /// Question 5. The tenant rides the envelope through the outbox and into the handler, on Marten
    /// conjoined tenancy, which is what we run.
    /// </summary>
    [Fact]
    public async Task A_message_carries_the_tenant_of_the_request_that_published_it()
    {
        var alpha = await TenantAsync();
        var marker = Marker();

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant", alpha);
        var response = await client.PostAsync(
            $"/api/_test/messages/publish-then-commit?message={marker}", null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await WaitUntilAsync(() => Recorder(_fixture).Has($"{marker}@{alpha}"), HandledTimeout,
            $"the handler never saw {marker} in tenant {alpha}");
        Recorder(_fixture).Has($"{marker}@default").Should().BeFalse("it must not also have run in the default partition");
    }

    /// <summary>Retry with backoff, then the dead letter table, the same shape as JobOptions.</summary>
    [Fact]
    public async Task A_message_that_keeps_failing_is_dead_lettered_after_max_attempts()
    {
        var marker = Marker();

        var response = await _fixture.CreateClient().PostAsync(
            $"/api/_test/messages/publish-failing?message={marker}", null, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dead = await WaitForDeadLetterAsync(marker);

        dead.ExceptionType.Should().Contain(nameof(InvalidOperationException));
        dead.ExceptionMessage.Should().Contain(AlwaysFailsHandler.Reason);
        Recorder(_fixture).Count($"attempt:{marker}").Should().Be(barakoCMS.Infrastructure.Jobs.JobOptions.DefaultMaxAttempts,
            "every attempt before the last was counted and retried, and nothing ran after the dead letter");
        (await IncomingEnvelopesHoldingAsync(marker)).Should().Be(0, "a dead letter is out of the inbox, not parked in it");
    }

    /// <summary>
    /// Question 2. Wolverine's tables live in their own schema, beside the Marten store rather than
    /// inside it, and the object that can script them for a migration is the message store.
    /// </summary>
    /// <remarks>
    /// This asserts the shape of the storage, not that a deploy would be safe. It cannot: the test
    /// database is built fresh by the current build, so there is no drift for an assert to find.
    /// Drift against a database that already exists is what `scripts/upgrade-check.sh` measures, and
    /// on this branch that check fails, on fourteen Marten ALTER statements the migration does not
    /// carry yet. Do not read a pass here as an upgrade being clean.
    /// </remarks>
    [Fact]
    public async Task Wolverine_storage_is_its_own_database_in_its_own_schema()
    {
        var tables = await ScalarListAsync(
            "select table_name from information_schema.tables where table_schema = @schema order by table_name",
            new NpgsqlParameter("schema", MessagingSetup.SchemaName));

        tables.Should().NotBeEmpty();
        tables.Should().Contain("wolverine_incoming_envelopes")
            .And.Contain("wolverine_outgoing_envelopes")
            .And.Contain("wolverine_dead_letters");

        var inPublic = await ScalarListAsync(
            "select table_name from information_schema.tables where table_schema = 'public' and table_name like 'wolverine%'");
        inPublic.Should().BeEmpty("the public schema is the one the 3.x upgrade reasons about; nothing of Wolverine's belongs in it");

        // Beside the Marten store, not inside it. db-assert discovers two databases on this branch,
        // Marten's Main and Wolverine's WolverineEnvelopeStorage, and reports on both, so the gate
        // from #951 does see these tables and db-patch can script them. What it does not do is put
        // them in Main's object list, which is why asking Main about them answers nothing.
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        store.Storage.Database.AllObjects()
            .Where(o => o.Identifier.Schema == MessagingSetup.SchemaName)
            .Should().BeEmpty("Wolverine's tables belong to its own database, so Main must not claim them");

        var messageStore = _fixture.Services.GetRequiredService<Wolverine.Persistence.Durability.IMessageStore>();
        messageStore.Should().BeAssignableTo<Weasel.Core.Migrations.IDatabase>(
            "the migration for an upgrade has to come from somewhere, and this is the object that can script it");
        var wolverineObjects = ((Weasel.Core.Migrations.IDatabase)messageStore).AllObjects().ToList();
        wolverineObjects.Should().NotBeEmpty();
        wolverineObjects.Select(o => o.Identifier.Name).Should().Contain("wolverine_incoming_envelopes");

        var outPath = Environment.GetEnvironmentVariable("BARAKO_WOLVERINE_DDL_OUT");
        if (!string.IsNullOrEmpty(outPath))
        {
            await using var writer = new StreamWriter(outPath);
            var migrator = new Weasel.Postgresql.PostgresqlMigrator();
            foreach (var schemaObject in wolverineObjects)
            {
                schemaObject.WriteCreateStatement(migrator, writer);
            }
        }
    }

    /// <summary>
    /// Question 1 on a real path. A webhook delivery published through the outbox is delivered by
    /// WebhookAction from a scope bound to the envelope's tenant, and its delivery row lands in that
    /// tenant's partition.
    /// </summary>
    [Fact]
    public async Task A_webhook_published_through_the_outbox_is_delivered_and_recorded_in_its_tenant()
    {
        var alpha = await TenantAsync();
        var host = WebhookHost();
        var key = $"wh-{Guid.NewGuid():N}";

        var content = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "wolverine-probe",
            Status = ContentStatus.Published,
            Data = new Dictionary<string, object> { ["title"] = "outbox" },
        };
        await using (var session = Store(host).LightweightSession(alpha))
        {
            session.Store(content);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var listener = new RecordingListener();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant", alpha);
        var response = await client.PostAsync(
            $"/api/_test/messages/publish-webhook?contentId={content.Id}&key={key}&url={Uri.EscapeDataString(listener.Url)}",
            null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await WaitUntilAsync(() => Recorder(host).Has($"webhook:{key}:ok"), HandledTimeout, "the webhook was never delivered");

        listener.WasCalled.Should().BeTrue();
        listener.LastHeaders.Should().ContainKey("Idempotency-Key").WhoseValue.Should().Be(key);
        var deliveryId = Guid.Parse(listener.LastHeaders[WebhookSigning.DeliveryHeader]);

        await using var inAlpha = Store(host).QuerySession(alpha);
        var delivery = await inAlpha.LoadAsync<WebhookDelivery>(deliveryId, TestContext.Current.CancellationToken);
        delivery.Should().NotBeNull("the delivery row was written through the tenant's session");
        delivery!.ResponseStatus.Should().Be(200);

        await using var inDefault = Store(host).QuerySession();
        (await inDefault.LoadAsync<WebhookDelivery>(deliveryId, TestContext.Current.CancellationToken))
            .Should().BeNull("the row belongs to alpha and must not be visible from the default partition");
    }

    /// <summary>A permanent refusal is recorded once and never retried, the runner's Retryable rule kept.</summary>
    [Fact]
    public async Task A_webhook_to_a_refused_url_fails_once_and_is_not_retried()
    {
        var host = WebhookHost();
        var key = $"wh-{Guid.NewGuid():N}";

        var content = new Content { Id = Guid.NewGuid(), ContentType = "wolverine-probe", Status = ContentStatus.Published };
        await using (var session = Store(host).LightweightSession())
        {
            session.Store(content);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await host.CreateClient().PostAsync(
            $"/api/_test/messages/publish-webhook?contentId={content.Id}&key={key}&url={Uri.EscapeDataString("ftp://example.com/hook")}",
            null, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await WaitUntilAsync(() => Recorder(host).Has($"webhook:{key}:permanent"), HandledTimeout, "the refusal was never recorded");
        await Task.Delay(SettleTime, TestContext.Current.CancellationToken);

        Recorder(host).Count($"webhook:{key}:").Should().Be(1, "a permanent failure is not a retry candidate");
        (await DeadLettersHoldingAsync(key)).Should().BeNull("it was handled, not failed, so it is not a dead letter either");
    }

    private static string Marker() => $"msg-{Guid.NewGuid():N}";

    private static MessageRecorder Recorder(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<MessageRecorder>();

    private static IDocumentStore Store(WebApplicationFactory<Program> host) =>
        host.Services.GetRequiredService<IDocumentStore>();

    private static WebApplicationFactory<Program>? _webhookHost;

    /// <summary>
    /// A host whose outbound guard lets the loopback listener through. Kept for the rest of the run,
    /// like every derived host in this suite.
    /// </summary>
    private WebApplicationFactory<Program> WebhookHost() =>
        _webhookHost ??= _fixture.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<OutboundAddressGuard>();
            services.AddSingleton(new OutboundAddressGuard(isBlocked: _ => false));
        }));

    private static async Task PostExpectingFailureAsync(HttpClient client, string url)
    {
        try
        {
            var response = await client.PostAsync(url, null, TestContext.Current.CancellationToken);
            response.IsSuccessStatusCode.Should().BeFalse("the request is meant to fail after publishing");
        }
        catch (HttpRequestException) { }
        catch (InvalidOperationException) { }
        catch (Npgsql.PostgresException) { }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new Xunit.Sdk.XunitException($"Timed out after {timeout}: {because}");
    }

    private sealed record DeadLetter(string ExceptionType, string ExceptionMessage);

    private async Task<DeadLetter> WaitForDeadLetterAsync(string marker)
    {
        var deadline = DateTime.UtcNow + HandledTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var found = await DeadLettersHoldingAsync(marker);
            if (found is not null) return found;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new Xunit.Sdk.XunitException(
            $"No dead letter holding {marker} within {HandledTimeout}; {Recorder(_fixture).Count($"attempt:{marker}")} attempt(s) recorded");
    }

    private async Task<DeadLetter?> DeadLettersHoldingAsync(string marker)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(
            $"select exception_type, exception_message from {MessagingSetup.SchemaName}.wolverine_dead_letters "
            + "where position(convert_to(@m, 'UTF8') in body) > 0", conn);
        cmd.Parameters.AddWithValue("m", marker);
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        if (!await reader.ReadAsync(TestContext.Current.CancellationToken)) return null;
        return new DeadLetter(reader.GetString(0), reader.GetString(1));
    }

    private async Task<int> IncomingEnvelopesHoldingAsync(string marker)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(
            $"select count(*) from {MessagingSetup.SchemaName}.wolverine_incoming_envelopes where position(convert_to(@m, 'UTF8') in body) > 0", conn);
        cmd.Parameters.AddWithValue("m", marker);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Every Wolverine row, in any of its three tables, whose body carries the marker.</summary>
    private async Task<List<string>> EnvelopesHoldingAsync(string marker)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        var schema = MessagingSetup.SchemaName;
        await using var cmd = new NpgsqlCommand(
            $"select 'incoming' from {schema}.wolverine_incoming_envelopes where position(convert_to(@m, 'UTF8') in body) > 0 "
            + $"union all select 'outgoing' from {schema}.wolverine_outgoing_envelopes where position(convert_to(@m, 'UTF8') in body) > 0 "
            + $"union all select 'dead' from {schema}.wolverine_dead_letters where position(convert_to(@m, 'UTF8') in body) > 0", conn);
        cmd.Parameters.AddWithValue("m", marker);
        var found = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken)) found.Add(reader.GetString(0));
        return found;
    }

    private async Task<List<string>> ScalarListAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddRange(parameters);
        var found = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken)) found.Add(reader.GetString(0));
        return found;
    }

    private async Task<string> TenantAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"wolv-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return slug;
    }
}
