using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Contacts;

/// <summary>Anonymous delivery serves published contacts, Public fields only, per tenant.</summary>
[Collection("Integration2")]
public class ContactsPublicDeliveryTests(IntegrationTestFixture factory)
{
    private readonly ContactsTestHost _host = new(factory);

    private static async Task<Guid> PublishedAsync(HttpClient admin, string name, bool published, string? email = null)
    {
        var id = await ContactsTestHost.CreatedIdAsync(await ContactsTestHost.CreateAsync(admin, name, email));
        var put = await admin.PutAsJsonAsync($"/api/contacts/{id}", new
        {
            kind = "Person",
            displayName = name,
            email,
            phone = "+63 900 000 0000",
            address = "1 Sample Street",
            isPublished = published,
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        return id;
    }

    [Fact]
    public async Task A_published_contact_is_served_without_email_phone_or_address()
    {
        var tenant = await _host.TenantAsync();
        await _host.DeclareTypeAsync(tenant, deliverable: true, d =>
        {
            // A type that marks the channels Public by mistake still cannot serve them.
            foreach (var field in d.Fields.Where(f => f.Name is "email" or "phone" or "address"))
            {
                field.Sensitivity = barakoCMS.Models.SensitivityLevel.Public;
            }
        });
        var admin = await _host.AdminAsync(tenant);
        var anonymous = _host.Anonymous(tenant);
        var email = ContactsTestHost.UniqueEmail();
        var id = await PublishedAsync(admin, "Ana Reyes", published: true, email);

        var one = await anonymous.GetAsync($"/api/public/contacts/{id}");
        var body = await one.Content.ReadAsStringAsync();

        one.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("Ana Reyes");
        body.Should().NotContain(email).And.NotContain("Sample Street").And.NotContain("900 000");

        one.Headers.CacheControl!.ToString().Should().Be("public, max-age=60");
        one.Headers.Vary.Should().Contain("X-Tenant");

        var listed = await anonymous.GetAsync("/api/public/contacts");
        listed.Headers.CacheControl!.ToString().Should().Be("public, max-age=60");
        listed.Headers.Vary.Should().Contain("X-Tenant");
        var list = await ContactsTestHost.Json(listed);
        list.GetProperty("items").GetArrayLength().Should().Be(1);
        list.GetRawText().Should().NotContain(email);
    }

    [Fact]
    public async Task An_unpublished_merged_or_undeliverable_contact_answers_404()
    {
        var closed = await _host.TenantAsync();
        var adminClosed = await _host.AdminAsync(closed);
        var publishedInClosed = await PublishedAsync(adminClosed, "Closed Tenant", published: true);

        var open = await _host.TenantAsync();
        await _host.DeclareTypeAsync(open, deliverable: true);
        var admin = await _host.AdminAsync(open);
        var anonymous = _host.Anonymous(open);
        var draft = await PublishedAsync(admin, "Draft", published: false);
        var survivor = await PublishedAsync(admin, "Survivor", published: true);
        var duplicate = await PublishedAsync(admin, "Duplicate", published: true);
        (await admin.PostAsJsonAsync($"/api/contacts/{duplicate}/merge", new { intoId = survivor }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Positive control: the survivor of the same tenant is served, so the 404s below are the rule.
        (await anonymous.GetAsync($"/api/public/contacts/{survivor}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await anonymous.GetAsync($"/api/public/contacts/{draft}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await anonymous.GetAsync($"/api/public/contacts/{duplicate}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _host.Anonymous(closed).GetAsync($"/api/public/contacts/{publishedInClosed}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        var list = await ContactsTestHost.Json(await anonymous.GetAsync("/api/public/contacts"));
        list.GetProperty("totalItems").GetInt32().Should().Be(1);
        var closedList = await ContactsTestHost.Json(await _host.Anonymous(closed).GetAsync("/api/public/contacts"));
        closedList.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_published_contact_of_one_tenant_is_not_served_by_another()
    {
        var tenantA = await _host.TenantAsync();
        var tenantB = await _host.TenantAsync();
        await _host.DeclareTypeAsync(tenantA, deliverable: true);
        await _host.DeclareTypeAsync(tenantB, deliverable: true);
        var id = await PublishedAsync(await _host.AdminAsync(tenantA), "Ana", published: true);

        (await _host.Anonymous(tenantA).GetAsync($"/api/public/contacts/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _host.Anonymous(tenantB).GetAsync($"/api/public/contacts/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
