using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Numbering;

/// <summary>An entry keeps the number the server gave it, through an update and through a rollback.</summary>
[Collection("Integration1")]
public class NumberedFieldKeptTests(IntegrationTestFixture factory)
{
    private static readonly Lock HostGate = new();
    private static WebApplicationFactory<Program>? _relaxed;

    private WebApplicationFactory<Program> RelaxedHost()
    {
        lock (HostGate)
        {
            return _relaxed ??= factory.WithSettings(new Dictionary<string, string?>
            {
                ["Lifecycle:EnforceTransitions"] = "false",
                ["Lifecycle:AllowSelfTransition:Submit"] = "true",
            });
        }
    }

    private async Task<HttpClient> AdminAsync(WebApplicationFactory<Program>? host = null)
    {
        using var scope = factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var roleIds = new List<Guid>();
        foreach (var name in new[] { "SuperAdmin", "Admin" })
        {
            var role = await s.Query<Role>().FirstOrDefaultAsync(r => r.Name == name);
            if (role is null) { role = new Role { Id = Guid.NewGuid(), Name = name }; s.Store(role); }
            roleIds.Add(role.Id);
        }

        var userId = Guid.NewGuid();
        s.Store(new User
        {
            Id = userId,
            Username = $"nk_{Guid.NewGuid():n}",
            Email = $"nk_{Guid.NewGuid():n}@example.com",
            RoleIds = roleIds,
        });
        await s.SaveChangesAsync();

        var c = (host ?? factory).CreateClient();
        c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", factory.CreateToken(roles: ["SuperAdmin", "Admin"], userId: userId.ToString()));
        return c;
    }

    private async Task<string> StoreTypeAsync(bool numbered, bool lifecycle = false)
    {
        var type = $"nk_{Guid.NewGuid():n}"[..16];
        using var scope = factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        s.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = type, DisplayName = type,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "InvoiceNo", DisplayName = "Invoice no", Type = "string" },
            ],
            Lifecycle = lifecycle
                ? new LifecycleDefinition
                {
                    States = ["Draft", "Submitted"],
                    InitialState = "Draft",
                    Transitions = [new StateTransition { Name = "Submit", From = "Draft", To = "Submitted" }],
                }
                : null,
            Numbering = numbered ? new NumberingRule { Field = "InvoiceNo", Prefix = "INV-", Padding = 4 } : null,
        });
        await s.SaveChangesAsync();
        return type;
    }

    private static async Task<Guid> CreateAsync(HttpClient client, string type)
    {
        var created = await client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Title"] = "first" },
        });
        created.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", created.StatusCode, await created.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<string?> StoredNumberAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var q = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var content = await q.LoadAsync<Content>(id);
        return content!.Data.FirstOrDefault(p => p.Key == "InvoiceNo").Value?.ToString();
    }

    private static async Task<Guid> FirstVersionIdAsync(HttpClient client, Guid id)
    {
        var body = await (await client.GetAsync($"/api/contents/{id}/history")).Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("items")[0].GetProperty("versionId").GetGuid();
    }

    [Fact]
    public async Task A_put_without_the_number_keeps_it()
    {
        var client = await AdminAsync();
        var type = await StoreTypeAsync(numbered: true);
        var id = await CreateAsync(client, type);
        var number = await StoredNumberAsync(id);
        number.Should().StartWith("INV-");

        var put = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            data = new Dictionary<string, object> { ["Title"] = "second" },
        });

        put.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", put.StatusCode, await put.Content.ReadAsStringAsync());
        (await StoredNumberAsync(id)).Should().Be(number);
    }

    [Fact]
    public async Task A_put_with_the_stored_number_is_accepted()
    {
        var client = await AdminAsync();
        var type = await StoreTypeAsync(numbered: true);
        var id = await CreateAsync(client, type);
        var number = await StoredNumberAsync(id);
        number.Should().StartWith("INV-");

        var put = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            data = new Dictionary<string, object> { ["Title"] = "second", ["InvoiceNo"] = number! },
        });

        put.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", put.StatusCode, await put.Content.ReadAsStringAsync());
        (await StoredNumberAsync(id)).Should().Be(number);
    }

    [Fact]
    public async Task A_put_with_another_number_is_refused()
    {
        var client = await AdminAsync();
        var type = await StoreTypeAsync(numbered: true);
        var id = await CreateAsync(client, type);
        var number = await StoredNumberAsync(id);
        number.Should().StartWith("INV-");

        var put = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            data = new Dictionary<string, object> { ["Title"] = "second", ["InvoiceNo"] = "INV-9999" },
        });

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await put.Content.ReadAsStringAsync()).Should().Contain("InvoiceNo");
        (await StoredNumberAsync(id)).Should().Be(number);
    }

    [Fact]
    public async Task A_rollback_to_a_version_without_the_number_keeps_it()
    {
        var client = await AdminAsync();
        var type = await StoreTypeAsync(numbered: false);
        var id = await CreateAsync(client, type);

        // Numbering is turned on after the entry was written, and the entry takes a number.
        using (var scope = factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var def = await s.Query<ContentTypeDefinition>().FirstAsync(d => d.Name == type);
            def.Numbering = new NumberingRule { Field = "InvoiceNo", Prefix = "INV-", Padding = 4 };
            s.Store(def);
            var content = (await s.LoadAsync<Content>(id))!;
            content.Data["InvoiceNo"] = "INV-0007";
            s.Store(content);
            await s.SaveChangesAsync();
        }

        var versionId = await FirstVersionIdAsync(client, id);
        var rollback = await client.PostAsync($"/api/contents/{id}/rollback/{versionId}", null);

        rollback.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", rollback.StatusCode, await rollback.Content.ReadAsStringAsync());
        (await StoredNumberAsync(id)).Should().Be("INV-0007");
    }

    [Fact]
    public async Task A_rollback_to_a_version_with_another_number_keeps_the_stored_one()
    {
        var client = await AdminAsync();
        var type = await StoreTypeAsync(numbered: true);
        var id = await CreateAsync(client, type);

        using (var scope = factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var content = (await s.LoadAsync<Content>(id))!;
            content.Data["InvoiceNo"] = "INV-0007";
            s.Store(content);
            await s.SaveChangesAsync();
        }

        var versionId = await FirstVersionIdAsync(client, id);
        var rollback = await client.PostAsync($"/api/contents/{id}/rollback/{versionId}", null);

        rollback.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", rollback.StatusCode, await rollback.Content.ReadAsStringAsync());
        (await StoredNumberAsync(id)).Should().Be("INV-0007");
    }

    [Fact]
    public async Task A_type_with_no_numbering_takes_a_value_in_the_field_as_before()
    {
        var client = await AdminAsync();
        var type = await StoreTypeAsync(numbered: false);
        var id = await CreateAsync(client, type);

        var put = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            data = new Dictionary<string, object> { ["Title"] = "second", ["InvoiceNo"] = "anything" },
        });

        put.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", put.StatusCode, await put.Content.ReadAsStringAsync());
        (await StoredNumberAsync(id)).Should().Be("anything");
    }

    [Fact]
    public async Task An_entry_with_no_number_yet_stays_without_one()
    {
        var client = await AdminAsync();
        var type = await StoreTypeAsync(numbered: false);
        var id = await CreateAsync(client, type);

        using (var scope = factory.Services.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var def = await s.Query<ContentTypeDefinition>().FirstAsync(d => d.Name == type);
            def.Numbering = new NumberingRule { Field = "InvoiceNo", Prefix = "INV-", Padding = 4 };
            s.Store(def);
            await s.SaveChangesAsync();
        }

        var put = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            data = new Dictionary<string, object> { ["Title"] = "second" },
        });

        put.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", put.StatusCode, await put.Content.ReadAsStringAsync());
        (await StoredNumberAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task A_put_with_a_blank_number_is_refused()
    {
        var client = await AdminAsync();
        var type = await StoreTypeAsync(numbered: true);
        var id = await CreateAsync(client, type);
        var number = await StoredNumberAsync(id);
        number.Should().StartWith("INV-");

        var put = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            data = new Dictionary<string, object> { ["Title"] = "second", ["InvoiceNo"] = "" },
        });

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await put.Content.ReadAsStringAsync()).Should().Contain("numbered by the server");
        (await StoredNumberAsync(id)).Should().Be(number);
    }

    [Fact]
    public async Task A_transition_with_another_number_is_refused()
    {
        var client = await AdminAsync(RelaxedHost());
        var type = await StoreTypeAsync(numbered: true, lifecycle: true);
        var id = await CreateAsync(client, type);
        var number = await StoredNumberAsync(id);
        number.Should().StartWith("INV-");

        var move = await client.PutAsJsonAsync($"/api/contents/{id}/status", new
        {
            id,
            transition = "Submit",
            data = new Dictionary<string, object> { ["InvoiceNo"] = "INV-9999" },
        });

        move.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await move.Content.ReadAsStringAsync()).Should().Contain("numbered by the server");
        (await StoredNumberAsync(id)).Should().Be(number);
    }
}
