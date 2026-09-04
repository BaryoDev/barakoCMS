using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BarakoCMS.Forms;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using ContentDoc = barakoCMS.Models.Content;

namespace BarakoCMS.Tests.Features.Forms;

/// <summary>
/// The Forms module over real HTTP: the anonymous submit path and every protection on it, the
/// admin reads, the gates, and the tenant boundary. Issue #110.
/// </summary>
[Collection("Sequential")]
public class FormSubmissionTests
{
    private readonly IntegrationTestFixture _factory;

    private static int _ipCounter;

    public FormSubmissionTests(IntegrationTestFixture factory) => _factory = factory;

    /// <summary>TEST-NET-2, one address per call so each caller gets its own rate-limit bucket.</summary>
    private static string FreshIp() => $"198.51.100.{Interlocked.Increment(ref _ipCounter) % 200 + 20}";

    private static string FreshName() => $"contact-{Guid.NewGuid():N}"[..20];

    private async Task<HttpClient> AdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("Admin"));
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, FreshIp());
        return client;
    }

    private static HttpClient Anonymous(IntegrationTestFixture factory, string? ip = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ip ?? FreshIp());
        return client;
    }

    private static object ContactForm(string name, params string[] notify) => new
    {
        name,
        displayName = "Contact us",
        fields = new object[]
        {
            new { name = "name", type = "string", required = true },
            new { name = "email", type = "email", required = true },
            new { name = "message", type = "text", required = false },
        },
        notifyAddresses = notify,
        enabled = true,
    };

    private async Task<string> DefineAsync(HttpClient admin, params string[] notify)
    {
        var name = FreshName();
        var created = await admin.PostAsJsonAsync("/api/forms", ContactForm(name, notify), TestContext.Current.CancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        return name;
    }

    private static Task<HttpResponseMessage> SubmitAsync(HttpClient anon, string name, object body) =>
        anon.PostAsJsonAsync($"/api/public/forms/{name}", body, TestContext.Current.CancellationToken);

    private async Task<int> StoredCountAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.Query<FormSubmission>().CountAsync(s => s.FormName == name, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_submission_is_stored_Sensitive_and_read_back_by_the_admin()
    {
        var admin = await AdminClient();
        var name = await DefineAsync(admin);

        var marker = $"hello-{Guid.NewGuid():N}";
        var response = await SubmitAsync(Anonymous(_factory), name,
            new { name = "Ana", email = "ana@example.com", message = marker });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("id").GetGuid();

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/forms/{name}/submissions", TestContext.Current.CancellationToken);
        list.GetProperty("totalItems").GetInt32().Should().Be(1);
        var item = list.GetProperty("items")[0];
        item.GetProperty("id").GetGuid().Should().Be(id);
        item.GetProperty("data").GetProperty("message").GetString().Should().Be(marker);

        var one = await admin.GetAsync($"/api/forms/{name}/submissions/{id}", TestContext.Current.CancellationToken);
        one.StatusCode.Should().Be(HttpStatusCode.OK);
        (await one.Content.ReadAsStringAsync()).Should().Contain(marker);

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var stored = await session.LoadAsync<FormSubmission>(id, TestContext.Current.CancellationToken);
        stored.Should().NotBeNull();
        stored!.Sensitivity.Should().Be(SensitivityLevel.Sensitive, "a submission is personal data from the moment it exists");
    }

    [Fact]
    public async Task An_unknown_field_is_400_and_nothing_is_stored()
    {
        var name = await DefineAsync(await AdminClient());

        var response = await SubmitAsync(Anonymous(_factory), name,
            new { name = "Ana", email = "ana@example.com", phone = "+63 900 000 0000" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("phone");
        (await StoredCountAsync(name)).Should().Be(0);
    }

    [Fact]
    public async Task A_missing_required_field_and_a_wrong_type_are_400()
    {
        var name = await DefineAsync(await AdminClient());
        var anon = Anonymous(_factory);

        var missing = await SubmitAsync(anon, name, new { name = "Ana" });
        var wrongType = await SubmitAsync(anon, name, new { name = "Ana", email = "not-an-address" });

        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await missing.Content.ReadAsStringAsync()).Should().Contain("email");
        wrongType.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StoredCountAsync(name)).Should().Be(0);
    }

    [Fact]
    public async Task A_value_in_the_honeypot_field_is_a_silent_202_that_stores_nothing()
    {
        var name = await DefineAsync(await AdminClient());

        // "website" is the default honeypot field name; the form does not declare it.
        var response = await SubmitAsync(Anonymous(_factory), name,
            new { name = "Bot", email = "bot@example.com", website = "https://spam.example" });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted,
            "a bot must not be told which field gave it away");
        (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .TryGetProperty("id", out _).Should().BeTrue("the acknowledgement has the shape of a real one");
        (await StoredCountAsync(name)).Should().Be(0, "a honeypot hit is dropped");
        _factory.Email.Messages.Should().NotContain(m => m.Body.Contains("bot@example.com"),
            "nobody is told about a dropped submission");
    }

    [Fact]
    public async Task An_empty_honeypot_field_is_an_ordinary_submission()
    {
        var name = await DefineAsync(await AdminClient());

        var response = await SubmitAsync(Anonymous(_factory), name,
            new { name = "Ana", email = "ana@example.com", website = "" });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await StoredCountAsync(name)).Should().Be(1, "a hidden input posts an empty string and that is a person");
    }

    [Fact]
    public async Task The_sixth_submission_in_a_minute_from_one_address_is_429_while_another_address_is_fine()
    {
        var name = await DefineAsync(await AdminClient());
        var ip = FreshIp();
        var anon = Anonymous(_factory, ip);
        var body = new { name = "Ana", email = "ana@example.com" };

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
            statuses.Add((await SubmitAsync(anon, name, body)).StatusCode);

        statuses.Take(5).Should().OnlyContain(s => s == HttpStatusCode.Accepted,
            "the default budget is five a minute per address");
        statuses[5].Should().Be(HttpStatusCode.TooManyRequests);

        var other = await SubmitAsync(Anonymous(_factory), name, body);
        other.StatusCode.Should().Be(HttpStatusCode.Accepted, "the limit is per address, not per form");
        (await StoredCountAsync(name)).Should().Be(6, "five from the first address and one from the second");
    }

    [Fact]
    public async Task A_33_KB_body_is_413_and_nothing_is_stored()
    {
        var name = await DefineAsync(await AdminClient());
        var anon = Anonymous(_factory);

        var json = JsonSerializer.Serialize(new { name = "Ana", email = "ana@example.com", message = new string('x', 33 * 1024) });
        Encoding.UTF8.GetByteCount(json).Should().BeGreaterThan(32 * 1024, "the body has to be over the cap for this to prove anything");

        var response = await anon.PostAsync($"/api/public/forms/{name}",
            new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await StoredCountAsync(name)).Should().Be(0);
    }

    [Fact]
    public async Task A_field_over_the_per_field_cap_is_400()
    {
        var name = await DefineAsync(await AdminClient());

        // Under the 32 KB body cap and over the 4000 character field cap.
        var response = await SubmitAsync(Anonymous(_factory), name,
            new { name = "Ana", email = "ana@example.com", message = new string('m', 4001) });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("message");
        (await StoredCountAsync(name)).Should().Be(0);
    }

    /// <summary>
    /// The control is what makes the 404s mean something: a public content type sharing the
    /// form's name is served by the same routes, and the submission still is not.
    /// </summary>
    [Fact]
    public async Task A_submission_is_absent_from_every_public_delivery_route()
    {
        var admin = await AdminClient();
        var name = await DefineAsync(admin);
        var marker = $"secret-{Guid.NewGuid():N}";

        var submitted = await SubmitAsync(Anonymous(_factory), name, new { name = "Ana", email = "ana@example.com", message = marker });
        submitted.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var id = (await submitted.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetGuid();

        var anon = Anonymous(_factory);

        // Before the control exists, the type is unknown to delivery and every route is 404.
        (await anon.GetAsync($"/api/public/{name}", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await anon.GetAsync($"/api/public/{name}/{id}", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await anon.GetAsync($"/api/public/{name}/search?q={marker}", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await anon.GetAsync($"/api/public/forms/{name}", TestContext.Current.CancellationToken)).IsSuccessStatusCode.Should().BeFalse("the submit route has no read");

        // The control: a deliverable content type with the form's name, holding one public entry.
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = name,
                DisplayName = name,
                IsPubliclyDeliverable = true,
                Fields = [new FieldDefinition { Name = "slug", Type = "slug" }, new FieldDefinition { Name = "title", Type = "string" }],
            });
            session.Store(new ContentDoc
            {
                Id = Guid.NewGuid(),
                ContentType = name,
                Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public,
                Data = new Dictionary<string, object> { ["slug"] = "control", ["title"] = "served" },
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var list = await anon.GetAsync($"/api/public/{name}", TestContext.Current.CancellationToken);
        list.StatusCode.Should().Be(HttpStatusCode.OK, "the control entry proves the delivery route is live for this name");
        var listBody = await list.Content.ReadAsStringAsync();
        listBody.Should().Contain("served");
        listBody.Should().NotContain(marker, "a submission is not content and never lists");
        listBody.Should().NotContain(id.ToString(), "not even by id");

        (await anon.GetAsync($"/api/public/{name}/control", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await anon.GetAsync($"/api/public/{name}/{id}", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a submission id is not a slug of anything");
        // The search echoes the query back, so the assertion is on the results rather than the body.
        var search = await anon.GetFromJsonAsync<JsonElement>($"/api/public/{name}/search?q={marker}", TestContext.Current.CancellationToken);
        search.GetProperty("count").GetInt32().Should().Be(0, "a submission is not searchable content either");
        search.GetProperty("results").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task The_CSV_has_a_header_and_one_row_per_submission()
    {
        var admin = await AdminClient();
        var name = await DefineAsync(admin);
        var anon = Anonymous(_factory);

        (await SubmitAsync(anon, name, new { name = "Ana", email = "ana@example.com", message = "first, with a comma" })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await SubmitAsync(anon, name, new { name = "Ben", email = "ben@example.com", message = "=SUM(A1)" })).StatusCode.Should().Be(HttpStatusCode.Accepted);

        var response = await admin.GetAsync($"/api/forms/{name}/submissions.csv", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        var lines = (await response.Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(3, "a header and one row per submission");
        lines[0].Should().Be("id,submittedAt,name,email,message");
        lines.Skip(1).Should().Contain(l => l.Contains("\"first, with a comma\""), "a comma in a value is quoted");
        lines.Skip(1).Should().Contain(l => l.Contains("'=SUM(A1)"), "a formula is defused before a spreadsheet runs it");
    }

    [Fact]
    public async Task A_submission_emails_every_notify_address()
    {
        var admin = await AdminClient();
        var notify = $"inbox-{Guid.NewGuid():N}@example.com";
        var name = await DefineAsync(admin, notify);
        var marker = $"note-{Guid.NewGuid():N}";

        (await SubmitAsync(Anonymous(_factory), name, new { name = "Ana", email = "ana@example.com", message = marker }))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);

        var sent = _factory.Email.Messages.Where(m => m.To == notify).ToList();
        sent.Should().ContainSingle();
        sent[0].Subject.Should().Contain("Contact us");
        sent[0].Body.Should().Contain(marker);
    }

    [Fact]
    public async Task A_failing_email_provider_does_not_stop_the_202_or_the_store()
    {
        var host = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IEmailService>();
            s.AddSingleton<IEmailService, ThrowingEmailService>();
        }));

        var admin = host.CreateClient();
        admin.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("Admin"));
        admin.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, FreshIp());
        var name = FreshName();
        (await admin.PostAsJsonAsync("/api/forms", ContactForm(name, "ops@example.com"), TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var anon = host.CreateClient();
        anon.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, FreshIp());
        var response = await anon.PostAsJsonAsync($"/api/public/forms/{name}",
            new { name = "Ana", email = "ana@example.com" }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, "the mail is best effort and the submission is the point");
        (await StoredCountAsync(name)).Should().Be(1);
    }

    private sealed class ThrowingEmailService : IEmailService
    {
        public Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the provider is down");
    }

    [Fact]
    public async Task A_disabled_or_unknown_form_is_404_to_the_public()
    {
        var admin = await AdminClient();
        var name = await DefineAsync(admin);
        var body = new { name = "Ana", email = "ana@example.com" };

        var disabled = ContactForm(name);
        var update = await admin.PutAsJsonAsync($"/api/forms/{name}", new
        {
            displayName = "Contact us",
            fields = new object[] { new { name = "name", type = "string", required = true }, new { name = "email", type = "email", required = true } },
            notifyAddresses = Array.Empty<string>(),
            enabled = false,
        }, TestContext.Current.CancellationToken);
        update.StatusCode.Should().Be(HttpStatusCode.OK, await update.Content.ReadAsStringAsync());

        var anon = Anonymous(_factory);
        (await SubmitAsync(anon, name, body)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SubmitAsync(anon, "no-such-form", body)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        _ = disabled;
    }

    [Fact]
    public async Task A_form_may_not_declare_the_honeypot_field_and_a_name_is_unique_per_tenant()
    {
        var admin = await AdminClient();
        var name = await DefineAsync(admin);

        var clash = await admin.PostAsJsonAsync("/api/forms", new
        {
            name = FreshName(),
            fields = new object[] { new { name = "website", type = "url", required = false } },
        }, TestContext.Current.CancellationToken);
        var duplicate = await admin.PostAsJsonAsync("/api/forms", ContactForm(name), TestContext.Current.CancellationToken);
        var unknownType = await admin.PostAsJsonAsync("/api/forms", new
        {
            name = FreshName(),
            fields = new object[] { new { name = "age", type = "years", required = false } },
        }, TestContext.Current.CancellationToken);

        clash.StatusCode.Should().Be(HttpStatusCode.BadRequest, "real visitors could never submit it");
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        unknownType.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Deleting_a_form_deletes_its_submissions()
    {
        var admin = await AdminClient();
        var name = await DefineAsync(admin);
        (await SubmitAsync(Anonymous(_factory), name, new { name = "Ana", email = "ana@example.com" })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await StoredCountAsync(name)).Should().Be(1);

        (await admin.DeleteAsync($"/api/forms/{name}", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await StoredCountAsync(name)).Should().Be(0, "a mailbox nobody can read is personal data nobody should keep");
        (await admin.GetAsync($"/api/forms/{name}", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The gates, with role names that are fresh GUIDs, so the legacy fallback cannot be what admits
    /// a caller: whatever it reaches, it reaches on the capability.
    /// </summary>
    [Fact]
    public async Task Designing_forms_and_reading_submissions_are_two_grants_and_a_name_alone_is_neither()
    {
        var name = await DefineAsync(await AdminClient());

        var designer = await CallerHolding(FormsCapabilities.ManageForms);
        var reader = await CallerHolding(FormsCapabilities.ViewFormSubmissions);
        var nobody = await CallerHolding();
        var anon = Anonymous(_factory);

        (await designer.GetAsync("/api/forms", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await designer.GetAsync($"/api/forms/{name}/submissions", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "manage_forms designs forms and does not read what people sent");
        (await designer.GetAsync($"/api/forms/{name}/submissions.csv", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await reader.GetAsync($"/api/forms/{name}/submissions", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await reader.GetAsync($"/api/forms/{name}/submissions.csv", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await reader.GetAsync("/api/forms", TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reader.SendAsync(Probe("POST", "/api/forms"), TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reader.SendAsync(Probe("DELETE", $"/api/forms/{name}"), TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        foreach (var (verb, route) in new[]
        {
            ("GET", "/api/forms"), ("POST", "/api/forms"), ("GET", $"/api/forms/{name}"), ("PUT", $"/api/forms/{name}"),
            ("DELETE", $"/api/forms/{name}"), ("GET", $"/api/forms/{name}/submissions"),
            ("GET", $"/api/forms/{name}/submissions/{Guid.NewGuid()}"), ("GET", $"/api/forms/{name}/submissions.csv"),
        })
        {
            (await nobody.SendAsync(Probe(verb, route), TestContext.Current.CancellationToken)).StatusCode
                .Should().Be(HttpStatusCode.Forbidden, "{0} {1} must refuse a role holding nothing", verb, route);
            (await anon.SendAsync(Probe(verb, route), TestContext.Current.CancellationToken)).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized, "{0} {1} must refuse an anonymous caller", verb, route);
        }
    }

    [Fact]
    public async Task One_tenants_submissions_are_invisible_to_another_tenant_with_the_same_form_name()
    {
        var tenantA = await TenantAsync();
        var tenantB = await TenantAsync();
        var adminA = await MemberAsync(tenantA);
        var adminB = await MemberAsync(tenantB);
        var name = FreshName();

        (await adminA.PostAsJsonAsync("/api/forms", ContactForm(name), TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await adminB.PostAsJsonAsync("/api/forms", ContactForm(name), TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.Created,
            "a form name is unique per tenant, not across the deployment");

        var anonA = Anonymous(_factory);
        anonA.DefaultRequestHeaders.Add("X-Tenant", tenantA);
        var marker = $"only-a-{Guid.NewGuid():N}";
        (await SubmitAsync(anonA, name, new { name = "Ana", email = "ana@example.com", message = marker })).StatusCode.Should().Be(HttpStatusCode.Accepted);

        var listA = await adminA.GetFromJsonAsync<JsonElement>($"/api/forms/{name}/submissions", TestContext.Current.CancellationToken);
        var listB = await adminB.GetFromJsonAsync<JsonElement>($"/api/forms/{name}/submissions", TestContext.Current.CancellationToken);
        var csvB = await adminB.GetStringAsync($"/api/forms/{name}/submissions.csv", TestContext.Current.CancellationToken);

        listA.GetProperty("totalItems").GetInt32().Should().Be(1);
        listA.GetProperty("items")[0].GetProperty("data").GetProperty("message").GetString().Should().Be(marker);
        listB.GetProperty("totalItems").GetInt32().Should().Be(0, "the other tenant's mailbox is empty");
        csvB.Should().NotContain(marker);
    }

    private static HttpRequestMessage Probe(string verb, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(verb), path);
        if (verb is "POST" or "PUT")
            request.Content = new StringContent("~", Encoding.UTF8, "application/json");
        return request;
    }

    private async Task<HttpClient> CallerHolding(params string[] capabilities)
    {
        var unique = $"Forms Caller {Guid.NewGuid():N}";

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new Role { Id = Guid.NewGuid(), Name = unique, SystemCapabilities = capabilities.ToList() };
        session.Store(role);

        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId,
            Username = $"forms-{userId:n}",
            Email = $"forms-{userId:n}@example.com",
            RoleIds = [role.Id],
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: [unique], userId: userId.ToString()));
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, FreshIp());
        return client;
    }

    private async Task<string> TenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"club-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return slug;
    }

    private async Task<HttpClient> MemberAsync(string tenantSlug)
    {
        var userId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"ft-{Guid.NewGuid():n}"[..14],
                Email = $"ft-{Guid.NewGuid():n}@example.com",
                RoleIds = [barakoCMS.Models.SystemRoles.SuperAdminRoleId],
            });
            session.Store(new Membership
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                TenantSlug = tenantSlug,
                Status = MembershipStatus.Active,
                RoleIds = [barakoCMS.Models.SystemRoles.SuperAdminRoleId],
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(
                roles: ["SuperAdmin"],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = tenantSlug }));
        client.DefaultRequestHeaders.Add("X-Tenant", tenantSlug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, FreshIp());
        return client;
    }
}
