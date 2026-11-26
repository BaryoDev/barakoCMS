using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// <c>Content:RequireContentType</c>: with it on, a new entry of a type with no definition is refused.
/// Off by default, where such an entry is accepted unvalidated.
/// </summary>
/// <remarks>
/// Every path that creates content from caller input is covered: the single create, the batch, the
/// bulk import, the portability import and the creator that workflows, the site settings sync and
/// the lines write call. The CreateTask workflow action writes without the creator, so it has its own
/// test. An update of an entry already stored under an undefined type is not changed.
/// </remarks>
[Collection("Integration1")]
public class RequireContentTypeTests(IntegrationTestFixture host)
{
    private static readonly Lock HostGate = new();
    private static WebApplicationFactory<Program>? _on;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> On()
    {
        lock (HostGate)
        {
            return _on ??= host.WithSetting("Content:RequireContentType", "true");
        }
    }

    private static string Undefined() => $"undef{Guid.NewGuid():n}"[..20];

    private async Task<HttpClient> ClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await host.StoredUserTokenAsync("Admin", "SuperAdmin"));
        return client;
    }

    private async Task<string> DefinedAsync()
    {
        var name = $"def{Guid.NewGuid():n}"[..20];
        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = name,
            Fields = [new FieldDefinition { Name = "Title", Type = "string" }],
        });
        await session.SaveChangesAsync(Ct);
        return name;
    }

    private static Dictionary<string, object> Data() => new() { ["Title"] = "x" };

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string type) =>
        client.PostAsJsonAsync("/api/contents", new { contentType = type, data = Data() }, Ct);

    private async Task<int> RowsAsync(string type)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<Content>().CountAsync(c => c.ContentType == type, Ct);
    }

    private static async Task RefusedAsync(HttpResponseMessage response, string type)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain(type).And.Contain("has no definition");
    }

    [Fact]
    public async Task Off_by_default_an_undefined_type_is_accepted()
    {
        var type = Undefined();

        var response = await CreateAsync(await ClientAsync(host), type);

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync(Ct));
        (await RowsAsync(type)).Should().Be(1);
    }

    [Fact]
    public async Task On_an_undefined_type_is_refused_naming_the_type()
    {
        var type = Undefined();

        await RefusedAsync(await CreateAsync(await ClientAsync(On()), type), type);

        (await RowsAsync(type)).Should().Be(0);
    }

    [Fact]
    public async Task On_a_defined_type_is_still_accepted()
    {
        var type = await DefinedAsync();

        var response = await CreateAsync(await ClientAsync(On()), type);

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync(Ct));
        (await RowsAsync(type)).Should().Be(1);
    }

    [Fact]
    public async Task On_an_update_of_an_entry_under_an_undefined_type_is_not_changed()
    {
        var type = Undefined();
        var created = await CreateAsync(await ClientAsync(host), type);
        created.IsSuccessStatusCode.Should().BeTrue();
        var id = Guid.Parse((await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Ct)).GetProperty("id").GetString()!);

        var response = await (await ClientAsync(On())).PutAsJsonAsync(
            $"/api/contents/{id}", new { id, data = new Dictionary<string, object> { ["Title"] = "y" } }, Ct);

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task On_the_batch_refuses_an_undefined_type_and_writes_nothing()
    {
        var type = Undefined();

        var response = await (await ClientAsync(On())).PostAsJsonAsync("/api/contents/batch", new
        {
            steps = new object[] { new { op = "create", contentType = type, id = Guid.CreateVersion7(), data = Data() } },
        }, Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.Should().BeFalse(body);
        body.Should().Contain(type).And.Contain("has no definition");
        (await RowsAsync(type)).Should().Be(0);
    }

    [Fact]
    public async Task On_the_bulk_import_refuses_each_row_of_an_undefined_type()
    {
        var type = Undefined();

        var response = await (await ClientAsync(On())).PostAsJsonAsync("/api/import/content", new
        {
            contentType = type,
            continueOnError = true,
            records = new[] { Data() },
        }, Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);
        body.Should().Contain(type).And.Contain("has no definition");
        (await RowsAsync(type)).Should().Be(0);
    }

    [Fact]
    public async Task On_the_portability_import_refuses_a_record_of_an_undefined_type()
    {
        var type = Undefined();

        var response = await (await ClientAsync(On())).PostAsJsonAsync("/api/portability/import", new
        {
            contentTypes = Array.Empty<object>(),
            contents = new[] { new { contentType = type, data = Data(), status = "Draft" } },
        }, Ct);

        var body = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.Should().BeFalse(body);
        body.Should().Contain(type).And.Contain("has no definition");
        (await RowsAsync(type)).Should().Be(0);
    }

    /// <summary>A bundle that defines a type and carries its entries is not refused for that type.</summary>
    [Fact]
    public async Task On_the_portability_import_accepts_a_record_of_a_type_the_same_bundle_defines()
    {
        var type = Undefined();

        var response = await (await ClientAsync(On())).PostAsJsonAsync("/api/portability/import", new
        {
            contentTypes = new[]
            {
                new { name = type, displayName = type, fields = new[] { new { name = "Title", type = "string" } } },
            },
            contents = new[] { new { contentType = type, data = Data(), status = "Draft" } },
        }, Ct);

        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync(Ct));
        (await RowsAsync(type)).Should().Be(1);
    }

    [Fact]
    public void The_refusal_drops_control_characters_and_cuts_a_long_name()
    {
        var message = barakoCMS.Core.Validation.RequiredContentType.Refusal("a\r\nb" + new string('x', 200));

        message.Should().NotContain("\r").And.NotContain("\n");
        message.Should().Contain("'ab" + new string('x', 62) + "...'");
    }

    /// <summary>The creator the CreateEntry workflow action, the site settings sync and the lines write call.</summary>
    [Fact]
    public async Task On_the_content_creator_refuses_an_undefined_type_and_accepts_a_defined_one()
    {
        var defined = await DefinedAsync();
        using var scope = On().Services.CreateScope();
        var creator = scope.ServiceProvider.GetRequiredService<IContentCreator>();
        var http = new DefaultHttpContext();
        var missing = Undefined();

        var refused = await creator.CheckAsync(
            new ContentCreateRequest { ContentType = missing, Data = Data() }, Guid.NewGuid(), http, batch: null, Ct);
        var accepted = await creator.CheckAsync(
            new ContentCreateRequest { ContentType = defined, Data = Data() }, Guid.NewGuid(), http, batch: null, Ct);

        refused.Should().ContainSingle().Which.Should().Contain(missing).And.Contain("has no definition");
        accepted.Should().BeEmpty();
    }

    private async Task<WorkflowActionResult> CreateTaskAsync(string type, bool requireOn)
    {
        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Content:RequireContentType"] = requireOn ? "true" : "false" })
            .Build();
        var action = new CreateTaskAction(
            session, NullLogger<CreateTaskAction>.Instance, scope.ServiceProvider.GetRequiredService<IContentWriter>(), config);

        return await action.RunAsync(
            new Dictionary<string, string> { ["ContentType"] = type, ["Title"] = "Follow up" },
            new Content { Id = Guid.NewGuid(), ContentType = "trigger", LastModifiedBy = Guid.NewGuid() },
            Ct);
    }

    [Fact]
    public async Task On_the_CreateTask_action_refuses_an_undefined_type()
    {
        var type = Undefined();

        var result = await CreateTaskAsync(type, requireOn: true);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain(type).And.Contain("has no definition");
        (await RowsAsync(type)).Should().Be(0);
    }

    [Fact]
    public async Task On_the_CreateTask_action_still_creates_a_defined_type_and_off_creates_an_undefined_one()
    {
        var defined = await DefinedAsync();
        var undefined = Undefined();

        (await CreateTaskAsync(defined, requireOn: true)).Succeeded.Should().BeTrue();
        (await CreateTaskAsync(undefined, requireOn: false)).Succeeded.Should().BeTrue();
    }

    /// <summary>The validator counts a type the account owns as defined, so the action does too.</summary>
    [Fact]
    public async Task On_the_CreateTask_action_creates_a_type_the_account_owns()
    {
        var name = $"acct{Guid.NewGuid():n}"[..20];
        using (var scope = host.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new AccountContentType
            {
                Id = Guid.NewGuid(),
                AccountId = TenantAccount.DefaultId,
                Name = name,
                Definition = new ContentTypeDefinition { Id = Guid.NewGuid(), Name = name, DisplayName = name, Fields = [] },
            });
            await session.SaveChangesAsync(Ct);
        }

        var result = await CreateTaskAsync(name, requireOn: true);

        result.Succeeded.Should().BeTrue(result.Error);
    }

    [Fact]
    public async Task On_the_CreateTask_action_finds_a_defined_type_named_in_another_case()
    {
        var defined = await DefinedAsync();

        var result = await CreateTaskAsync(defined.ToUpperInvariant(), requireOn: true);

        result.Succeeded.Should().BeTrue(result.Error);
    }
}
