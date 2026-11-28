using System.Net;
using System.Text.Json.Nodes;
using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Infrastructure.Services;
using BarakoCMS.Tests.Features.Config;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Portability;

/// <summary>
/// A token field in a config document: the definition travels, the value does not. An export leaves
/// it out for every caller, an apply generates a new one whatever the document carries, and a
/// document may not turn a stored token into another type.
/// </summary>
[Collection("Integration1")]
public class ImportTokenFieldTests
{
    private const string Forged = "forged0token0forged0token0forged";

    private readonly IntegrationTestFixture _fixture;
    private readonly ConfigCallers _callers;

    public ImportTokenFieldTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _callers = new ConfigCallers(fixture);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<string> TenantAsync() => _callers.TenantAsync();

    private Task<HttpClient> AdminOfAsync(string tenantSlug) => _callers.SuperAdminAsync(tenantSlug);

    /// <summary>A ticket type with a token field and one entry, written through the content writer.</summary>
    private async Task<(string Type, string Token)> TicketWithEntryAsync(string tenantSlug)
    {
        var type = $"ticket{Guid.NewGuid():n}"[..14];

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenantSlug);
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = "Ticket",
            Fields =
            [
                new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" },
                new FieldDefinition
                {
                    Name = "ClaimToken", DisplayName = "Claim token", Type = "token", Sensitivity = SensitivityLevel.Hidden,
                },
            ],
        });
        await session.SaveChangesAsync(Ct);

        IContentWriter writer = new ContentWriter(session, new ContentSourcingPolicyService(session), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        var created = await writer.CreateAsync(
            new ContentCreated(
                Guid.NewGuid(), type, new Dictionary<string, object> { ["Name"] = "Ana" }, ContentStatus.Published,
                Guid.NewGuid(), null, SensitivityLevel.Public, DateTime.UtcNow),
            Ct);
        await session.SaveChangesAsync(Ct);

        created.Data.Should().ContainKey("ClaimToken");
        return (type, created.Data["ClaimToken"].ToString()!);
    }

    private async Task<List<Content>> EntriesAsync(string tenantSlug, string type)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(tenantSlug);
        return (await session.Query<Content>().Where(c => c.ContentType == type).ToListAsync(Ct)).ToList();
    }

    private async Task<FieldDefinition> StoredFieldAsync(string tenantSlug, string type)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(tenantSlug);
        var def = await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type, Ct);
        return def.Fields.Single(f => f.Name == "ClaimToken");
    }

    private static async Task<(JsonObject Document, string Raw)> ExportAsync(HttpClient client)
    {
        var (exported, body) = await ConfigCallers.ExportAsync(client, "contentTypes,contents");
        var raw = body.ToString();
        exported.StatusCode.Should().Be(HttpStatusCode.OK, raw);

        var document = JsonNode.Parse(body.GetRawText())!.AsObject();
        Fields(document).Count.Should().Be(2);
        document["contents"]!.AsObject().Count.Should().Be(1, "the SuperAdmin exporting may read the entry");
        return (document, raw);
    }

    private static JsonArray Fields(JsonObject document) =>
        document["contentTypes"]!.AsObject().Single().Value!["fields"]!.AsArray();

    private static JsonObject Field(JsonObject document, string name) =>
        Fields(document).Single(f => f!["name"]!.GetValue<string>() == name)!.AsObject();

    private static JsonObject Entry(JsonObject document) => document["contents"]!.AsObject().Single().Value!.AsObject();

    private static async Task<(HttpStatusCode Status, string Body)> ImportAsync(HttpClient client, JsonObject document)
    {
        var (response, body) = await ConfigCallers.ApplyAsync(client, document);
        return (response.StatusCode, body.ToString());
    }

    [Fact]
    public async Task An_export_leaves_the_token_out_even_for_a_caller_who_may_read_it()
    {
        var tenant = await TenantAsync();
        var (_, token) = await TicketWithEntryAsync(tenant);

        var (document, raw) = await ExportAsync(await AdminOfAsync(tenant));

        Entry(document)["data"]!.AsObject().ContainsKey("Name").Should().BeTrue("the entry is exported, so the absence below is the token rule");
        Entry(document)["data"]!.AsObject().ContainsKey("ClaimToken").Should().BeFalse();
        raw.Should().NotContain(token);

        var field = Field(document, "ClaimToken");
        field["type"]!.GetValue<string>().Should().Be("token", "the definition travels");
        field["sensitivity"]!.GetValue<string>().Should().Be(nameof(SensitivityLevel.Hidden));
    }

    [Fact]
    public async Task An_apply_generates_a_new_token_whatever_the_document_carries()
    {
        var source = await TenantAsync();
        var destination = await TenantAsync();
        var (type, token) = await TicketWithEntryAsync(source);

        var (document, _) = await ExportAsync(await AdminOfAsync(source));
        Entry(document)["data"]!["ClaimToken"] = Forged;

        var (status, body) = await ImportAsync(await AdminOfAsync(destination), document);
        status.Should().Be(HttpStatusCode.OK, body);

        var entries = await EntriesAsync(destination, type);
        entries.Should().HaveCount(1);
        var landed = entries[0].Data["ClaimToken"].ToString()!;
        landed.Should().NotBe(Forged).And.NotBe(token);
        TokenFieldProbe.ShouldBeAToken(landed);
        (await StoredFieldAsync(destination, type)).Sensitivity.Should().Be(SensitivityLevel.Hidden);
    }

    [Fact]
    public async Task A_document_that_turns_a_stored_token_into_text_is_refused()
    {
        var tenant = await TenantAsync();
        var (type, _) = await TicketWithEntryAsync(tenant);
        var admin = await AdminOfAsync(tenant);

        var (document, _) = await ExportAsync(admin);
        Field(document, "ClaimToken")["type"] = "string";
        document.Remove("contents");

        var (status, body) = await ImportAsync(admin, document);

        status.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("ClaimToken").And.Contain("token type");
        (await StoredFieldAsync(tenant, type)).Type.Should().Be("token");
    }

    [Fact]
    public async Task A_document_token_field_with_no_sensitivity_is_stored_hidden()
    {
        var source = await TenantAsync();
        var destination = await TenantAsync();
        var (type, _) = await TicketWithEntryAsync(source);

        var (document, _) = await ExportAsync(await AdminOfAsync(source));
        Field(document, "ClaimToken")["sensitivity"] = nameof(SensitivityLevel.Public);

        var (status, body) = await ImportAsync(await AdminOfAsync(destination), document);

        status.Should().Be(HttpStatusCode.OK, body);
        (await StoredFieldAsync(destination, type)).Sensitivity.Should().Be(SensitivityLevel.Hidden);
    }
}
