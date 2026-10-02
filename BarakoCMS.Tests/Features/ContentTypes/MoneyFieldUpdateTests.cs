using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// The update endpoint against a money field that declares a currency, including the case where
/// the caller may not see the field.
/// </summary>
/// <remarks>
/// Write-path sensitivity puts the stored value of a field the caller cannot see back into the
/// data before it is validated. If that stored amount does not fit, the refusal is about a value
/// the caller never sent and may not read, so the refusal must not carry it.
/// </remarks>
[Collection("Sequential")]
public class MoneyFieldUpdateTests
{
    private const string HiddenAmount = "4321.987";

    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public MoneyFieldUpdateTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private async Task<string> SeedTypeAsync(SensitivityLevel salarySensitivity, string? currency)
    {
        var contentType = $"pay_{Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        using var session = store.LightweightSession();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = contentType,
            DisplayName = "Pay",
            Fields =
            [
                new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" },
                new FieldDefinition
                {
                    Name = "Salary", DisplayName = "Salary", Type = "money",
                    Sensitivity = salarySensitivity, Currency = currency,
                },
            ],
        });
        await session.SaveChangesAsync();
        return contentType;
    }

    /// <summary>What a forced declaration leaves behind: the currency is on, the entries are as they were.</summary>
    private async Task DeclareCurrencyAsync(string contentType, string currency)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        using var session = store.LightweightSession();
        var def = await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == contentType);
        def.Fields.Single(f => f.Name == "Salary").Currency = currency;
        session.Store(def);
        await session.SaveChangesAsync();
    }

    /// <summary>A user who may create and update the type, holding a token that carries tokenRole.</summary>
    private async Task<string> WriterAsync(string tokenRole, string contentType)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        using var session = store.LightweightSession();

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"dbrole_{Guid.NewGuid():N}",
            Permissions =
            [
                new ContentTypePermission
                {
                    ContentTypeSlug = contentType,
                    Read = new PermissionRule { Enabled = true },
                    Create = new PermissionRule { Enabled = true },
                    Update = new PermissionRule { Enabled = true },
                    Delete = new PermissionRule { Enabled = false },
                },
            ],
        };
        session.Store(role);
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"user_{Guid.NewGuid()}",
            Email = $"{Guid.NewGuid()}@example.com",
            RoleIds = [role.Id],
        };
        session.Store(user);
        await session.SaveChangesAsync();
        return _factory.CreateToken(new[] { tokenRole }, user.Id.ToString());
    }

    private async Task<Guid> CreateAsync(string token, string contentType, Dictionary<string, object> data)
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var create = await _client.PostAsJsonAsync("/api/contents", new barakoCMS.Features.Content.Create.Request
        {
            ContentType = contentType,
            Data = data,
        });
        create.StatusCode.Should().Be(HttpStatusCode.OK, await create.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await create.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> UpdateAsync(string token, Guid id, Dictionary<string, object> data)
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.PutAsJsonAsync($"/api/contents/{id}", new barakoCMS.Features.Content.Update.Request
        {
            Id = id,
            Status = ContentStatus.Draft,
            Version = 0,
            Data = data,
        });
    }

    [Fact]
    public async Task An_update_with_an_amount_that_does_not_fit_is_refused_and_one_that_fits_is_stored()
    {
        var type = await SeedTypeAsync(SensitivityLevel.Public, "USD");
        var admin = await WriterAsync("SuperAdmin", type);
        var id = await CreateAsync(admin, type, new() { ["Name"] = "Ana", ["Salary"] = 1000.50m });

        var refused = await UpdateAsync(admin, id, new() { ["Name"] = "Ana", ["Salary"] = 1000.505m });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync()).Should().Contain("Salary").And.Contain("USD");

        var accepted = await UpdateAsync(admin, id, new() { ["Name"] = "Ana", ["Salary"] = 1100.75m });
        accepted.StatusCode.Should().Be(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync());

        using var scope = _factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Content>(id);
        stored!.Data["Salary"].Should().Be(1100.75m);
    }

    [Fact]
    public async Task A_caller_who_cannot_see_the_field_is_refused_without_being_told_the_stored_amount()
    {
        var type = await SeedTypeAsync(SensitivityLevel.Hidden, currency: null);
        var admin = await WriterAsync("SuperAdmin", type);
        var viewer = await WriterAsync($"Viewer_{Guid.NewGuid():N}", type);

        var id = await CreateAsync(admin, type, new()
        {
            ["Name"] = "Ana",
            ["Salary"] = decimal.Parse(HiddenAmount, System.Globalization.CultureInfo.InvariantCulture),
        });
        await DeclareCurrencyAsync(type, "USD");

        var refused = await UpdateAsync(viewer, id, new() { ["Name"] = "Changed" });

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the stored amount is put back before validation and no longer fits");
        var body = await refused.Content.ReadAsStringAsync();
        body.Should().Contain("USD", "the refusal is the money rule and not something else");
        body.Should().NotContain(HiddenAmount).And.NotContain("4321");
    }
}
