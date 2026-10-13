using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Public;

/// <summary>
/// Issue #804: a runner checks a registration with a reference and a last name or email, without an
/// account, and gets back the named fields and nothing personal.
/// </summary>
/// <remarks>
/// Every test sends from its own address, so the per visitor limit of one test cannot answer
/// another's request with 429, and every reference is new, so the per reference limit cannot either.
/// </remarks>
[Collection("Sequential")]
public class PublicLookupTests
{
    private readonly IntegrationTestFixture _factory;

    public PublicLookupTests(IntegrationTestFixture factory) => _factory = factory;

    private static int _ip;

    private static string NextIp() => $"198.51.100.{Interlocked.Increment(ref _ip) % 250 + 1}";

    private HttpClient Anonymous(string? ip = null, string? tenant = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ip ?? NextIp());
        if (tenant is not null) client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        return client;
    }

    private static ContentTypeDefinition Registration(string type, LookupDefinition? lookup = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = type,
        DisplayName = type,
        IsPubliclyDeliverable = false,
        Fields =
        [
            new FieldDefinition { Name = "Reference", Type = "string", Sensitivity = SensitivityLevel.Public },
            new FieldDefinition { Name = "LastName", Type = "string", Sensitivity = SensitivityLevel.Sensitive },
            new FieldDefinition { Name = "Email", Type = "email", Sensitivity = SensitivityLevel.Hidden },
            new FieldDefinition { Name = "Status", Type = "string", Sensitivity = SensitivityLevel.Public },
            new FieldDefinition { Name = "BibNumber", Type = "string", Sensitivity = SensitivityLevel.Public },
            new FieldDefinition { Name = "ClaimToken", Type = "token", Sensitivity = SensitivityLevel.Hidden },
        ],
        Lookup = lookup ?? new LookupDefinition
        {
            MatchField = "Reference",
            VerifyFields = ["LastName", "Email"],
            ReturnFields = ["Status", "BibNumber"],
        },
    };

    private static Content Runner(string type, string reference, string lastName, ContentStatus status = ContentStatus.Draft) => new()
    {
        Id = Guid.NewGuid(),
        ContentType = type,
        Status = status,
        Data = new Dictionary<string, object>
        {
            ["Reference"] = reference,
            ["LastName"] = lastName,
            ["Email"] = $"{lastName.Replace(" ", "").ToLowerInvariant()}@example.test",
            ["Status"] = "Paid",
            ["BibNumber"] = "B-1042",
            ["ClaimToken"] = "k7m2q9x4w8r3t6y1v5n0p2s4",
        },
    };

    private async Task<(string Type, string Reference)> SeedAsync(
        LookupDefinition? lookup = null, string? tenant = null, ContentStatus status = ContentStatus.Draft)
    {
        var type = $"reg{Guid.NewGuid():N}"[..16];
        var reference = $"CMF-{Guid.NewGuid():N}"[..14].ToUpperInvariant();

        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        await using var session = tenant is null ? store.LightweightSession() : store.LightweightSession(tenant);
        session.Store(Registration(type, lookup));
        session.Store(Runner(type, reference, "Dela Cruz", status));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (type, reference);
    }

    private static Task<HttpResponseMessage> LookupAsync(HttpClient client, string type, string reference, string verify) =>
        client.PostAsJsonAsync($"/api/public/{type}/lookup", new { reference, verify }, TestContext.Current.CancellationToken);

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("data").Clone();
    }

    private static async Task<string> RefusalAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return $"{(int)response.StatusCode}|{response.Content.Headers.ContentType}|{body}";
    }

    [Fact]
    public async Task The_right_reference_and_last_name_return_the_named_fields_and_nothing_personal()
    {
        var (type, reference) = await SeedAsync();

        var data = await DataAsync(await LookupAsync(Anonymous(), type, reference, "Dela Cruz"));

        data.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["Status", "BibNumber"]);
        data.GetProperty("Status").GetString().Should().Be("Paid");
        data.GetProperty("BibNumber").GetString().Should().Be("B-1042");
    }

    [Fact]
    public async Task The_identifier_matches_any_verify_field_ignoring_case_and_spaces()
    {
        var (type, reference) = await SeedAsync();

        var byEmail = await DataAsync(await LookupAsync(Anonymous(), type, $"  {reference.ToLowerInvariant()} ", " DELACRUZ@EXAMPLE.TEST "));

        byEmail.GetProperty("BibNumber").GetString().Should().Be("B-1042");
    }

    [Fact]
    public async Task A_wrong_name_answers_exactly_as_an_unknown_reference_and_an_unknown_type_do()
    {
        var (type, reference) = await SeedAsync();
        var client = Anonymous();

        (await LookupAsync(client, type, reference, "Dela Cruz")).StatusCode.Should().Be(HttpStatusCode.OK, "positive control");

        var wrongName = await RefusalAsync(await LookupAsync(client, type, reference, "Santos"));
        var unknownReference = await RefusalAsync(await LookupAsync(client, type, "CMF-NOSUCHREF", "Dela Cruz"));
        var unknownType = await RefusalAsync(await LookupAsync(client, "nosuchtype", reference, "Dela Cruz"));

        wrongName.Should().StartWith("404|");
        unknownReference.Should().Be(wrongName);
        unknownType.Should().Be(wrongName);
    }

    [Fact]
    public async Task The_reference_alone_or_the_name_alone_returns_nothing()
    {
        var (type, reference) = await SeedAsync();
        var client = Anonymous();

        (await LookupAsync(client, type, reference, "")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await LookupAsync(client, type, "", "Dela Cruz")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await LookupAsync(client, type, reference, reference)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the reference is not a verify field, so sending it twice proves nothing");
    }

    [Fact]
    public async Task A_type_with_no_lookup_answers_none()
    {
        var type = $"plain{Guid.NewGuid():N}"[..16];
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession())
        {
            var def = Registration(type);
            def.Lookup = null;
            def.IsPubliclyDeliverable = true;
            session.Store(def);
            session.Store(Runner(type, "CMF-PLAIN0001", "Dela Cruz", ContentStatus.Published));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await LookupAsync(Anonymous(), type, "CMF-PLAIN0001", "Dela Cruz")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_archived_entry_is_not_found()
    {
        var (type, reference) = await SeedAsync(status: ContentStatus.Archived);

        (await LookupAsync(Anonymous(), type, reference, "Dela Cruz")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_field_made_sensitive_after_the_lookup_was_set_is_left_out()
    {
        var (type, reference) = await SeedAsync();
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        var client = Anonymous();

        (await DataAsync(await LookupAsync(client, type, reference, "Dela Cruz")))
            .TryGetProperty("BibNumber", out _).Should().BeTrue("positive control: the field is returned while Public");

        await using (var session = store.LightweightSession())
        {
            var def = await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type, TestContext.Current.CancellationToken);
            def.Fields.Single(f => f.Name == "BibNumber").Sensitivity = SensitivityLevel.Sensitive;
            session.Store(def);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var data = await DataAsync(await LookupAsync(client, type, reference, "Dela Cruz"));
        data.EnumerateObject().Select(p => p.Name).Should().Equal("Status");
    }

    [Fact]
    public async Task The_reveal_field_is_returned_only_while_the_singleton_setting_is_on()
    {
        var settings = $"set{Guid.NewGuid():N}"[..16];
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        var settingEntry = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = settings,
            Status = ContentStatus.Published,
            Data = new Dictionary<string, object> { ["KitReleaseOpen"] = false },
        };

        await using (var session = store.LightweightSession())
        {
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = settings,
                DisplayName = settings,
                IsSingleton = true,
                Fields = [new FieldDefinition { Name = "KitReleaseOpen", Type = "bool", Sensitivity = SensitivityLevel.Public }],
            });
            session.Store(settingEntry);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var (type, reference) = await SeedAsync(new LookupDefinition
        {
            MatchField = "Reference",
            VerifyFields = ["LastName"],
            ReturnFields = ["Status"],
            Reveal = new LookupReveal { Fields = ["ClaimToken"], SettingType = settings, SettingField = "KitReleaseOpen" },
        });
        var client = Anonymous();

        (await DataAsync(await LookupAsync(client, type, reference, "Dela Cruz")))
            .TryGetProperty("ClaimToken", out _).Should().BeFalse("the setting is off");

        await SetSettingAsync(store, settingEntry.Id, entry => entry.Data["KitReleaseOpen"] = true);

        (await DataAsync(await LookupAsync(client, type, reference, "Dela Cruz")))
            .GetProperty("ClaimToken").GetString().Should().Be("k7m2q9x4w8r3t6y1v5n0p2s4");

        await SetSettingAsync(store, settingEntry.Id, entry => entry.Status = ContentStatus.Draft);

        (await DataAsync(await LookupAsync(client, type, reference, "Dela Cruz")))
            .TryGetProperty("ClaimToken", out _).Should().BeFalse("a setting that is not Published does not open the reveal");
    }

    private static async Task SetSettingAsync(IDocumentStore store, Guid id, Action<Content> change)
    {
        await using var session = store.LightweightSession();
        var entry = await session.LoadAsync<Content>(id, TestContext.Current.CancellationToken);
        change(entry!);
        session.Store(entry!);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_lookup_does_not_reach_another_tenants_entries()
    {
        var tenantA = $"lka-{Guid.NewGuid():N}"[..14];
        var tenantB = $"lkb-{Guid.NewGuid():N}"[..14];
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession())
        {
            session.Store(new Tenant { Id = Guid.NewGuid(), Slug = tenantA, Name = tenantA, Status = TenantStatus.Active });
            session.Store(new Tenant { Id = Guid.NewGuid(), Slug = tenantB, Name = tenantB, Status = TenantStatus.Active });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var (type, reference) = await SeedAsync(tenant: tenantA);
        await using (var session = store.LightweightSession(tenantB))
        {
            session.Store(Registration(type));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await LookupAsync(Anonymous(tenant: tenantA), type, reference, "Dela Cruz")).StatusCode
            .Should().Be(HttpStatusCode.OK, "positive control: the entry's own tenant finds it");
        (await LookupAsync(Anonymous(tenant: tenantB), type, reference, "Dela Cruz")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "the same type in another tenant holds no such entry");
    }

    [Fact]
    public async Task Repeated_misses_from_one_address_are_throttled()
    {
        var (type, _) = await SeedAsync();
        var client = Anonymous();
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 12; i++)
        {
            statuses.Add((await LookupAsync(client, type, $"CMF-MISS{i:D2}{Guid.NewGuid():N}"[..16], "Santos")).StatusCode);
        }

        statuses.Should().HaveCount(12);
        statuses.Take(10).Should().AllSatisfy(s => s.Should().Be(HttpStatusCode.NotFound));
        statuses.Skip(10).Should().AllSatisfy(s => s.Should().Be(HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task One_reference_is_throttled_across_addresses_whether_or_not_it_exists()
    {
        var (type, reference) = await SeedAsync();
        var held = new List<HttpStatusCode>();
        var unheld = new List<HttpStatusCode>();

        for (var i = 0; i < 6; i++)
        {
            held.Add((await LookupAsync(Anonymous(), type, reference, "Santos")).StatusCode);
            unheld.Add((await LookupAsync(Anonymous(), type, "CMF-NOBODY" + type, "Santos")).StatusCode);
        }

        held.Should().HaveCount(6);
        held.Take(5).Should().AllSatisfy(s => s.Should().Be(HttpStatusCode.NotFound));
        held[5].Should().Be(HttpStatusCode.TooManyRequests);
        unheld.Should().Equal(held, "a held reference and one nobody holds are throttled the same way");

        var refused = await LookupAsync(Anonymous(), type, reference, "Dela Cruz");
        refused.StatusCode.Should().Be(
            HttpStatusCode.TooManyRequests, "the right name does not get past a reference that is out of attempts");
        refused.Headers.RetryAfter?.Delta.Should().Be(TimeSpan.FromMinutes(15),
            "a client that obeys Retry-After should not retry into a window that is still closed");
    }

    private async Task<HttpClient> AdminAsync()
    {
        var client = Anonymous();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    private static Task<HttpResponseMessage> SetAsync(HttpClient client, string type, object? lookup) =>
        client.PutAsJsonAsync($"/api/content-types/{type}/lookup", new { lookup }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Setting_a_lookup_refuses_a_sensitive_hidden_or_token_field_in_the_returned_set()
    {
        var (type, reference) = await SeedAsync();
        var admin = await AdminAsync();

        var accepted = await SetAsync(admin, type, new
        {
            matchField = "reference",
            verifyFields = new[] { "lastname" },
            returnFields = new[] { "bibnumber" },
        });
        accepted.StatusCode.Should().Be(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        (await DataAsync(await LookupAsync(Anonymous(), type, reference, "Dela Cruz")))
            .EnumerateObject().Select(p => p.Name).Should().Equal("BibNumber");

        foreach (var field in new[] { "LastName", "Email", "ClaimToken" })
        {
            var refused = await SetAsync(admin, type, new
            {
                matchField = "Reference",
                verifyFields = new[] { "LastName" },
                returnFields = new[] { "Status", field },
            });
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, field);
            (await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain(field);
        }

        await using var check = _factory.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var stored = await check.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type, TestContext.Current.CancellationToken);
        stored.Lookup!.ReturnFields.Should().Equal("BibNumber");

        var described = barakoCMS.Features.ContentType.ContentTypeResponse.From(stored);
        described.Lookup!.MatchField.Should().Be("Reference", "stored as the type spells it, and listed with the type");
        described.Lookup.ReturnFields.Should().Equal("BibNumber");
    }

    [Fact]
    public async Task Setting_a_lookup_refuses_a_match_field_that_verifies_itself_and_a_reveal_gated_on_a_type_that_is_not_a_singleton()
    {
        var (type, _) = await SeedAsync();
        var admin = await AdminAsync();

        (await SetAsync(admin, type, new
        {
            matchField = "Reference",
            verifyFields = new[] { "Reference" },
            returnFields = new[] { "Status" },
        })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await SetAsync(admin, type, new
        {
            matchField = "Reference",
            verifyFields = new[] { "LastName" },
            returnFields = new[] { "Status" },
            reveal = new { fields = new[] { "ClaimToken" }, settingType = type, settingField = "Status" },
        })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await SetAsync(admin, type, new
        {
            matchField = "Reference",
            verifyFields = Array.Empty<string>(),
            returnFields = new[] { "Status" },
        })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Removing_the_lookup_stops_the_type_answering()
    {
        var (type, reference) = await SeedAsync();
        var admin = await AdminAsync();
        var client = Anonymous();

        (await LookupAsync(client, type, reference, "Dela Cruz")).StatusCode.Should().Be(HttpStatusCode.OK, "positive control");

        (await SetAsync(admin, type, null)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await LookupAsync(client, type, reference, "Dela Cruz")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_editor_without_the_public_delivery_capability_cannot_set_a_lookup()
    {
        var (type, _) = await SeedAsync();
        var editor = Anonymous();
        editor.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("Editor"));

        (await SetAsync(editor, type, new
        {
            matchField = "Reference",
            verifyFields = new[] { "LastName" },
            returnFields = new[] { "Status" },
        })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await SetAsync(Anonymous(), type, null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
