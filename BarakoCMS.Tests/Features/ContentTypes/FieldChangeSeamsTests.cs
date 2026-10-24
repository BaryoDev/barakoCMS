using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Models;
using BarakoCMS.Tests.Features.Tenants;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// A field change reaches what names the field outside the type: block bindings follow a rename and
/// hold up a remove, and config apply keeps the field routes' rules (#1145).
/// </summary>
[Collection("Integration1")]
public class FieldChangeSeamsTests
{
    private readonly IntegrationTestFixture _fixture;

    public FieldChangeSeamsTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewName() => "seam" + Guid.NewGuid().ToString("n")[..10];

    private async Task<(string Slug, HttpClient Admin)> TenantAsync()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        return (slug, await TenantProfileSeed.AdminInAsync(_fixture, slug, Ct));
    }

    private static async Task CreateTypeAsync(HttpClient admin, string name, int entries = 1)
    {
        var res = await admin.PostAsJsonAsync("/api/content-types", new
        {
            name, displayName = name, isPubliclyDeliverable = true,
            fields = new object[] { new { name = "Title", type = "string" }, new { name = "Doctor", type = "string" } },
        }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));

        for (var i = 0; i < entries; i++)
        {
            var created = await admin.PostAsJsonAsync("/api/contents", new
            {
                contentType = name, data = new Dictionary<string, object> { ["Title"] = $"entry {i}", ["Doctor"] = $"Dr {i}" },
            }, Ct);
            created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync(Ct));
        }
    }

    private static async Task<Guid> BindingAsync(HttpClient admin, string type, string name, string? sort, Dictionary<string, string>? where)
    {
        var id = Guid.NewGuid();
        var res = await admin.PutAsJsonAsync($"/api/blocks/bindings/{id}", new { name, contentType = type, sort, limit = 3, where }, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        return id;
    }

    private async Task<BlockBinding> StoredBindingAsync(string slug, Guid id)
    {
        await using var session = _fixture.Services.GetRequiredService<IDocumentStore>().QuerySession(slug);
        return (await session.LoadAsync<BlockBinding>(id, Ct))!;
    }

    private async Task<ContentTypeDefinition> DefinitionAsync(string slug, string type)
    {
        await using var session = _fixture.Services.GetRequiredService<IDocumentStore>().QuerySession(slug);
        return await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type, Ct);
    }

    private async Task StoreAsync(string slug, params object[] documents)
    {
        await using var session = _fixture.Services.GetRequiredService<IDocumentStore>().LightweightSession(slug);
        foreach (var document in documents)
        {
            session.Store(document);
        }

        await session.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// Runs the change the response started to completion here, rather than waiting on the job
    /// queue, which a full suite run can keep busy past any wait worth writing.
    /// </summary>
    private async Task CompletedAsync(string slug, HttpResponseMessage started)
    {
        started.StatusCode.Should().Be(HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync(Ct));
        var id = (await started.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("change").GetProperty("id").GetGuid();
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var configuration = _fixture.Services.GetRequiredService<IConfiguration>();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if ((await FieldChanges.RunAsync(store, configuration, slug, id, maxBatches: null, Ct))?.State == FieldChangeState.Completed)
                {
                    return;
                }
            }
            catch (JasperFx.ConcurrencyException)
            {
                // The queued job committed the same batch first; the next run reads its progress.
            }
        }

        throw new Xunit.Sdk.XunitException($"Field change {id} did not complete.");
    }

    /// <summary>A rename stored as running and not queued, as one whose job has not reached the type yet.</summary>
    private async Task<FieldChange> RunningRenameAsync(string slug, string type)
    {
        var change = new FieldChange
        {
            Id = Guid.NewGuid(), ContentType = type, Field = "Doctor", Kind = FieldChangeKind.Rename, NewName = "Practitioner",
            State = FieldChangeState.Running, Total = 1, StartedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        await StoreAsync(slug, change);
        return change;
    }

    private async Task MarkCompletedAsync(string slug, Guid id)
    {
        await using var session = _fixture.Services.GetRequiredService<IDocumentStore>().LightweightSession(slug);
        var change = (await session.LoadAsync<FieldChange>(id, Ct))!;
        change.State = FieldChangeState.Completed;
        session.Store(change);
        await session.SaveChangesAsync(Ct);
    }

    private static Task<HttpResponseMessage> ApplyAsync(HttpClient admin, string type, ContentTypeDefinition definition) =>
        admin.PostAsJsonAsync("/api/config/apply", new
        {
            document = new { version = 1, contentTypes = new Dictionary<string, object> { [type] = definition } },
            allowDestructive = true,
        }, Ct);

    private static ContentTypeDefinition Definition(string type, params string[] fields) => new()
    {
        Name = type, DisplayName = type, IsPubliclyDeliverable = true,
        Fields = fields.Select(f => new FieldDefinition { Name = f, DisplayName = f, Type = "string" }).ToList(),
    };

    [Fact]
    public async Task A_rename_carries_the_bindings_that_name_the_field_and_no_other_tenants()
    {
        var (mine, admin) = await TenantAsync();
        var (theirs, other) = await TenantAsync();
        var type = NewName();
        await CreateTypeAsync(admin, type);
        await CreateTypeAsync(other, type);
        var sorted = await BindingAsync(admin, type, "By doctor", "-Doctor", null);
        var filtered = await BindingAsync(admin, type, "One doctor", "Title", new Dictionary<string, string> { ["doctor"] = "Dr 0", ["Title"] = "entry 0" });
        var untouched = await BindingAsync(admin, type, "By title", "Title", null);
        var foreign = await BindingAsync(other, type, "Their doctor", "-Doctor", new Dictionary<string, string> { ["Doctor"] = "Dr 0" });

        await CompletedAsync(mine, await admin.PostAsJsonAsync($"/api/content-types/{type}/fields/Doctor/change", new { newName = "Practitioner" }, Ct));

        (await StoredBindingAsync(mine, sorted)).Sort.Should().Be("-Practitioner");
        var where = (await StoredBindingAsync(mine, filtered)).Where!;
        where.Should().HaveCount(2);
        where.Should().Contain("Practitioner", "Dr 0").And.Contain("Title", "entry 0");
        (await StoredBindingAsync(mine, untouched)).Sort.Should().Be("Title");

        var theirBinding = await StoredBindingAsync(theirs, foreign);
        theirBinding.Sort.Should().Be("-Doctor", "the rename ran in the other tenant's type, which shares only a name");
        theirBinding.Where.Should().ContainKey("Doctor");
        (await DefinitionAsync(theirs, type)).Fields.Select(f => f.Name).Should().Equal("Title", "Doctor");
    }

    [Fact]
    public async Task Removing_a_field_a_binding_names_is_refused_with_409_naming_the_binding()
    {
        var (slug, admin) = await TenantAsync();
        var type = NewName();
        await CreateTypeAsync(admin, type);
        var binding = await BindingAsync(admin, type, "Doctors first", "-Doctor", null);
        var remove = new { remove = true, allowDestructive = true };

        var refused = await admin.PostAsJsonAsync($"/api/content-types/{type}/fields/Doctor/change", remove, Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("'Doctors first'").And.Contain(binding.ToString());
        (await DefinitionAsync(slug, type)).Fields.Select(f => f.Name).Should().Equal("Title", "Doctor");

        (await admin.DeleteAsync($"/api/blocks/bindings/{binding}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await CompletedAsync(slug, await admin.PostAsJsonAsync($"/api/content-types/{type}/fields/Doctor/change", remove, Ct));
        (await DefinitionAsync(slug, type)).Fields.Select(f => f.Name).Should().Equal("Title");
    }

    [Fact]
    public async Task A_binding_is_not_saved_on_a_type_while_a_field_change_runs_on_it()
    {
        var (slug, admin) = await TenantAsync();
        var type = NewName();
        await CreateTypeAsync(admin, type);
        var change = await RunningRenameAsync(slug, type);

        var refused = await admin.PutAsJsonAsync($"/api/blocks/bindings/{Guid.NewGuid()}", new { name = "Late", contentType = type, sort = "Doctor" }, Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("field change running");

        await MarkCompletedAsync(slug, change.Id);
        await BindingAsync(admin, type, "Late", "Doctor", null);
    }

    [Fact]
    public async Task Config_apply_refuses_a_type_with_a_field_change_running_as_the_field_routes_do()
    {
        var (slug, admin) = await TenantAsync();
        var type = NewName();
        await CreateTypeAsync(admin, type);
        var change = await RunningRenameAsync(slug, type);
        var route = await admin.PostAsJsonAsync($"/api/content-types/{type}/fields", new { fieldName = "Notes", type = "text" }, Ct);
        route.StatusCode.Should().Be(HttpStatusCode.Conflict, "the route's answer, which the apply has to match");

        var refused = await ApplyAsync(admin, type, Definition(type, "Title", "Doctor", "Notes"));

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict, await refused.Content.ReadAsStringAsync(Ct));
        var body = await refused.Content.ReadAsStringAsync(Ct);
        body.Should().Contain($"has a field change running ({change.Id})");
        (await DefinitionAsync(slug, type)).Fields.Select(f => f.Name).Should().Equal("Title", "Doctor");

        await MarkCompletedAsync(slug, change.Id);
        var applied = await ApplyAsync(admin, type, Definition(type, "Title", "Doctor", "Notes"));
        applied.StatusCode.Should().Be(HttpStatusCode.OK, "the positive control: " + await applied.Content.ReadAsStringAsync(Ct));
        (await DefinitionAsync(slug, type)).Fields.Select(f => f.Name).Should().Equal("Title", "Doctor", "Notes");
    }

    [Fact]
    public async Task Config_apply_refuses_leaving_out_a_field_a_binding_names_with_409()
    {
        var (slug, admin) = await TenantAsync();
        var type = NewName();
        await CreateTypeAsync(admin, type);
        var binding = await BindingAsync(admin, type, "Filtered", null, new Dictionary<string, string> { ["Doctor"] = "Dr 0" });

        var refused = await ApplyAsync(admin, type, Definition(type, "Title"));

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict, await refused.Content.ReadAsStringAsync(Ct));
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("'Filtered'").And.Contain(binding.ToString());
        (await DefinitionAsync(slug, type)).Fields.Select(f => f.Name).Should().Equal("Title", "Doctor");

        (await admin.DeleteAsync($"/api/blocks/bindings/{binding}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var applied = await ApplyAsync(admin, type, Definition(type, "Title"));
        applied.StatusCode.Should().Be(HttpStatusCode.OK, "the positive control: " + await applied.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("numbering", "the stored numbering rule")]
    [InlineData("lookup", "the stored lookup")]
    public async Task Config_apply_refuses_leaving_out_a_field_the_stored_numbering_rule_or_lookup_names(string rule, string expected)
    {
        var (slug, admin) = await TenantAsync();
        var type = NewName();
        await CreateTypeAsync(admin, type, entries: 0);
        var stored = await DefinitionAsync(slug, type);
        if (rule == "numbering")
        {
            stored.Numbering = new NumberingRule { Field = "Doctor", Prefix = "D-" };
        }
        else
        {
            stored.Lookup = new LookupDefinition { MatchField = "Doctor", VerifyFields = ["Title"], ReturnFields = ["Title"] };
        }

        await StoreAsync(slug, stored);

        var refused = await ApplyAsync(admin, type, Definition(type, "Title"));

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, await refused.Content.ReadAsStringAsync(Ct));
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain($"field 'Doctor' is named by {expected}");
        var after = await DefinitionAsync(slug, type);
        after.Fields.Select(f => f.Name).Should().Equal("Title", "Doctor");

        var kept = await ApplyAsync(admin, type, Definition(type, "Title", "Doctor"));
        kept.StatusCode.Should().Be(HttpStatusCode.OK, "the positive control: " + await kept.Content.ReadAsStringAsync(Ct));
    }
}
