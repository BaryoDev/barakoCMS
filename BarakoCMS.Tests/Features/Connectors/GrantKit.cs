using System.Net.Http.Headers;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Infrastructure.Sync;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Subject = BarakoCMS.Tests.Features.Connectors.RequestKit.Subject;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>Types, entries, callers and the three composing callers, for the connector grant tests.</summary>
internal static class GrantKit
{
    public const string SsnValue = "123-45-6789";
    public const string PerkValue = "member perk";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string Name(string prefix) => prefix + Guid.NewGuid().ToString("n")[..10];

    /// <summary>A Public field, a Sensitive one, a Members one, a Hidden one and one the owner list hides.</summary>
    public static List<FieldDefinition> Fields() =>
    [
        new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
        new FieldDefinition { Name = "Ssn", DisplayName = "Ssn", Type = "string", Sensitivity = SensitivityLevel.Sensitive },
        new FieldDefinition { Name = "Perk", DisplayName = "Perk", Type = "string", Sensitivity = SensitivityLevel.Members },
        new FieldDefinition { Name = "Vault", DisplayName = "Vault", Type = "string", Sensitivity = SensitivityLevel.Hidden },
        new FieldDefinition { Name = "Memo", DisplayName = "Memo", Type = "string", ShownWhenListedIn = "Viewers" },
    ];

    public static Dictionary<string, object> Data() => new()
    {
        ["Title"] = "plain title",
        ["Ssn"] = SsnValue,
        ["Perk"] = PerkValue,
        ["Vault"] = "vault value",
        ["Memo"] = "owner memo",
    };

    /// <summary>The type, an entry and a connector granted these fields of the type, in the default tenant.</summary>
    public static async Task<Subject> SeedAsync(
        IntegrationTestFixture fixture, string[] granted, SensitivityLevel entryLevel = SensitivityLevel.Public)
    {
        var subject = await RequestKit.SeedAsync(fixture, Fields(), Data(), entryLevel: entryLevel);
        await RequestKit.SetGrantsAsync(fixture, subject, granted.Select(f => subject.Type + "." + f));
        return subject;
    }

    public static RequestDefinition Definition(Subject subject, string? body, string path = "/in", Dictionary<string, string>? headers = null)
    {
        var definition = RequestKit.Definition(subject, body, path);
        if (headers is not null) definition.HeaderTemplates = headers;
        return definition;
    }

    public static async Task<Content> EntryAsync(IntegrationTestFixture fixture, Subject subject)
    {
        using var scope = fixture.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Content>(subject.EntryId, Ct))!;
    }

    public static async Task StoreAsync(IntegrationTestFixture fixture, params object[] documents)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        foreach (var document in documents) session.Store(document);
        await session.SaveChangesAsync(Ct);
    }

    /// <summary>Sets a field of the type to this level, as an owner would after the grant was made.</summary>
    public static async Task SetLevelAsync(IntegrationTestFixture fixture, string type, string field, SensitivityLevel level)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var definition = await session.Query<ContentTypeDefinition>().FirstAsync(t => t.Name == type, Ct);
        definition.Fields.First(f => f.Name == field).Sensitivity = level;
        session.Store(definition);
        await session.SaveChangesAsync(Ct);
    }

    /// <summary>What a Request step does with the definition: composes, and asks the sender to send.</summary>
    public static async Task<barakoCMS.Features.Workflows.WorkflowActionResult> RunStepAsync(
        IntegrationTestFixture fixture, Subject subject, RequestDefinition definition, GrantCountingSender sender)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(definition);
        await session.SaveChangesAsync(Ct);

        var action = new RequestAction(
            scope.ServiceProvider.GetRequiredService<IQuerySession>(),
            scope.ServiceProvider.GetRequiredService<IRequestComposer>(),
            sender,
            NullLogger<RequestAction>.Instance);
        return await action.RunAsync(
            new Dictionary<string, string> { ["Request"] = definition.Slug }, await EntryAsync(fixture, subject), Ct);
    }

    /// <summary>What a collection sync does with the definition: composes it and asks the fetcher to fetch.</summary>
    public static async Task<CollectionSyncOutcome> SyncAsync(
        IntegrationTestFixture fixture, Subject subject, RequestDefinition definition, GrantCountingFetcher fetcher)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(definition);
        await session.SaveChangesAsync(Ct);

        var runner = ActivatorUtilities.CreateInstance<CollectionSyncRunner>(scope.ServiceProvider, (IConnectorFetcher)fetcher);
        return await runner.RunAsync(new CollectionSync
        {
            Id = Guid.NewGuid(),
            Name = "Grants",
            Slug = Name("gsync"),
            ContentType = subject.Type,
            Source = SyncSource.Request,
            RequestSlug = definition.Slug,
            KeyField = "Title",
            FieldMap = new Dictionary<string, string> { ["Title"] = "id" },
        }, Ct);
    }

    /// <summary>Composes with a run's idempotency key, which the plain kit call leaves out.</summary>
    public static async Task<ComposedRequest> ComposeWithKeyAsync(
        IntegrationTestFixture fixture, Subject subject, RequestDefinition definition, string key)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var connector = await session.Query<Connector>().FirstAsync(c => c.Slug == subject.ConnectorSlug, Ct);
        var entry = await session.LoadAsync<Content>(subject.EntryId, Ct);
        return await scope.ServiceProvider.GetRequiredService<IRequestComposer>()
            .ComposeAsync(definition, connector, entry!, key, Ct);
    }

    /// <summary>A stored user in the default tenant whose one role holds these capabilities and may read these content types.</summary>
    public static async Task<(Guid UserId, string Role)> UserAsync(
        IntegrationTestFixture fixture, string[] capabilities, params string[] readable)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = Name("Grant "),
            SystemCapabilities = [.. capabilities],
            Permissions = [.. readable.Select(t => new ContentTypePermission
            {
                ContentTypeSlug = t, Read = new PermissionRule { Enabled = true },
            })],
        };
        var userId = Guid.NewGuid();
        await StoreAsync(fixture, role, new User
        {
            Id = userId, Username = $"grant-{userId:n}"[..16], Email = $"grant-{userId:n}@example.com", RoleIds = [role.Id],
        });
        return (userId, role.Name);
    }

    /// <summary>
    /// A signed-in caller in the default tenant whose one role holds these capabilities and may read
    /// these content types. The role is stored, so the checks that read a stored role see it.
    /// </summary>
    public static async Task<HttpClient> CallerAsync(
        IntegrationTestFixture fixture, string[] capabilities, params string[] readable) =>
        (await CallerOfAsync(fixture, capabilities, readable)).Client;

    /// <summary>The same caller, with the id of its stored user for the audit actor.</summary>
    public static async Task<(HttpClient Client, Guid UserId)> CallerOfAsync(
        IntegrationTestFixture fixture, string[] capabilities, params string[] readable)
    {
        var (userId, roleName) = await UserAsync(fixture, capabilities, readable);

        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", fixture.CreateToken(roles: [roleName], userId: userId.ToString()));
        client.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header, $"10.93.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}");
        return (client, userId);
    }

    public const string CostValue = "cost-4711";
    public const string NoteValue = "note-0815";

    internal sealed record LineSeed(RequestKit.Subject Subject, string Lines, string Products);

    /// <summary>A line with a Sensitive Note that points at a product with a Sensitive Cost.</summary>
    public static async Task<LineSeed> SeedLinesAsync(IntegrationTestFixture fixture, Func<string, string, string[]> grants)
    {
        var products = GrantKit.Name("qgp");
        var lines = GrantKit.Name("qgl");
        var slug = GrantKit.Name("qgc");
        var product = new Content
        {
            Id = Guid.NewGuid(), ContentType = products, Status = ContentStatus.Published,
            Data = new() { ["Name"] = "Kettle", ["Cost"] = CostValue },
        };
        var line = new Content
        {
            Id = Guid.NewGuid(), ContentType = lines, Status = ContentStatus.Published,
            Data = new() { ["Qty"] = "2", ["Note"] = NoteValue, ["Product"] = product.Id.ToString() },
        };

        await GrantKit.StoreAsync(
            fixture,
            new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = products, DisplayName = products,
                Fields =
                [
                    new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" },
                    new FieldDefinition { Name = "Cost", DisplayName = "Cost", Type = "string", Sensitivity = SensitivityLevel.Sensitive },
                ],
            },
            new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = lines, DisplayName = lines,
                Fields =
                [
                    new FieldDefinition { Name = "Qty", DisplayName = "Qty", Type = "string" },
                    new FieldDefinition { Name = "Note", DisplayName = "Note", Type = "string", Sensitivity = SensitivityLevel.Sensitive },
                    new FieldDefinition { Name = "Product", DisplayName = "Product", Type = "reference", ReferenceType = products },
                ],
            },
            new Connector
            {
                Id = Guid.NewGuid(), Name = "Query grants", Slug = slug, BaseUrl = "https://egress.example",
                Auth = ConnectorAuth.None, AllowedFields = grants(lines, products).ToList(),
            },
            product,
            line);

        return new LineSeed(new RequestKit.Subject(lines, line.Id, slug), lines, products);
    }
}
