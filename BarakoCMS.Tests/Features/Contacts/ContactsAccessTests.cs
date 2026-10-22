using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Contacts;

/// <summary>Who may read and write contacts, and that one tenant never reaches another's.</summary>
[Collection("Sequential")]
public class ContactsAccessTests(IntegrationTestFixture factory)
{
    private readonly ContactsTestHost _host = new(factory);

    [Fact]
    public async Task Every_admin_route_refuses_an_anonymous_caller_and_one_without_the_capability()
    {
        var tenant = await _host.TenantAsync();
        var admin = await _host.AdminAsync(tenant);
        var id = await ContactsTestHost.CreatedIdAsync(await ContactsTestHost.CreateAsync(admin, "Ana", ContactsTestHost.UniqueEmail()));
        var org = await ContactsTestHost.CreatedIdAsync(await ContactsTestHost.CreateAsync(admin, "Acme", kind: "Organization"));
        var outsider = await _host.OutsiderAsync(tenant);
        var anonymous = _host.Anonymous(tenant);

        var calls = new (string Name, Func<HttpClient, Task<HttpResponseMessage>> Call)[]
        {
            ("list", c => c.GetAsync("/api/contacts")),
            ("get", c => c.GetAsync($"/api/contacts/{id}")),
            ("links", c => c.GetAsync($"/api/contacts/{id}/links")),
            ("create", c => ContactsTestHost.CreateAsync(c, "Nope", ContactsTestHost.UniqueEmail())),
            ("update", c => c.PutAsJsonAsync($"/api/contacts/{id}", new { kind = "Person", displayName = "Nope" })),
            ("merge", c => c.PostAsJsonAsync($"/api/contacts/{id}/merge", new { intoId = Guid.NewGuid() })),
            ("link", c => c.PostAsJsonAsync($"/api/contacts/{id}/links", new { organizationId = org })),
            ("unlink", c => c.DeleteAsync($"/api/contacts/{id}/links/{org}")),
            ("delete", c => c.DeleteAsync($"/api/contacts/{Guid.NewGuid()}")),
        };

        foreach (var (name, call) in calls)
        {
            (await call(anonymous)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{name} as anonymous");
            (await call(outsider)).StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{name} as a user without the capability");
        }

        // Positive controls: the same reads succeed for the admin, so the refusals above are the gate.
        (await admin.GetAsync("/api/contacts")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.GetAsync($"/api/contacts/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.GetAsync($"/api/contacts/{id}/links")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_reader_can_read_contacts_and_is_refused_every_write()
    {
        var tenant = await _host.TenantAsync();
        var admin = await _host.AdminAsync(tenant);
        var id = await ContactsTestHost.CreatedIdAsync(await ContactsTestHost.CreateAsync(admin, "Ana", ContactsTestHost.UniqueEmail()));
        var other = await ContactsTestHost.CreatedIdAsync(await ContactsTestHost.CreateAsync(admin, "Ana B", ContactsTestHost.UniqueEmail()));
        var org = await ContactsTestHost.CreatedIdAsync(await ContactsTestHost.CreateAsync(admin, "Acme", kind: "Organization"));
        await admin.PostAsJsonAsync($"/api/contacts/{id}/links", new { organizationId = org });
        var reader = await _host.ReaderAsync(tenant);

        (await reader.GetAsync("/api/contacts")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await reader.GetAsync($"/api/contacts/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await reader.GetAsync($"/api/contacts/{id}/links")).StatusCode.Should().Be(HttpStatusCode.OK);

        var writes = new (string Name, Func<Task<HttpResponseMessage>> Call)[]
        {
            ("create", () => ContactsTestHost.CreateAsync(reader, "Nope", ContactsTestHost.UniqueEmail())),
            ("update", () => reader.PutAsJsonAsync($"/api/contacts/{id}", new { kind = "Person", displayName = "Nope" })),
            ("delete", () => reader.DeleteAsync($"/api/contacts/{id}")),
            ("merge", () => reader.PostAsJsonAsync($"/api/contacts/{other}/merge", new { intoId = id })),
            ("link add", () => reader.PostAsJsonAsync($"/api/contacts/{other}/links", new { organizationId = org })),
            ("link remove", () => reader.DeleteAsync($"/api/contacts/{id}/links/{org}")),
        };

        foreach (var (name, call) in writes)
        {
            (await call()).StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{name} as a reader");
        }

        (await admin.GetAsync($"/api/contacts/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.GetAsync($"/api/contacts/{other}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var links = await ContactsTestHost.Json(await admin.GetAsync($"/api/contacts/{id}/links"));
        links.GetProperty("totalItems").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task A_contact_of_one_tenant_is_not_found_in_another()
    {
        var tenantA = await _host.TenantAsync();
        var tenantB = await _host.TenantAsync();
        var adminA = await _host.AdminAsync(tenantA);
        var adminB = await _host.AdminAsync(tenantB);
        var email = ContactsTestHost.UniqueEmail();
        var inA = await ContactsTestHost.CreatedIdAsync(await ContactsTestHost.CreateAsync(adminA, "Ana", email));

        (await adminA.GetAsync($"/api/contacts/{inA}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await adminB.GetAsync($"/api/contacts/{inA}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await adminB.PutAsJsonAsync($"/api/contacts/{inA}", new { kind = "Person", displayName = "Taken" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await adminB.DeleteAsync($"/api/contacts/{inA}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var listedB = await ContactsTestHost.Json(await adminB.GetAsync("/api/contacts"));
        listedB.GetProperty("totalItems").GetInt32().Should().Be(0);
        var listedA = await ContactsTestHost.Json(await adminA.GetAsync("/api/contacts"));
        listedA.GetProperty("totalItems").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task The_same_email_is_a_separate_contact_in_each_tenant()
    {
        var tenantA = await _host.TenantAsync();
        var tenantB = await _host.TenantAsync();
        var adminA = await _host.AdminAsync(tenantA);
        var adminB = await _host.AdminAsync(tenantB);
        var email = ContactsTestHost.UniqueEmail();

        var inA = await ContactsTestHost.CreateAsync(adminA, "Ana", email);
        var inB = await ContactsTestHost.CreateAsync(adminB, "Ana", email);

        inA.StatusCode.Should().Be(HttpStatusCode.Created);
        inB.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ContactsTestHost.CreateAsync(adminB, "Ana again", email)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Links_and_merges_cannot_reach_a_contact_of_another_tenant()
    {
        var tenantA = await _host.TenantAsync();
        var tenantB = await _host.TenantAsync();
        var adminA = await _host.AdminAsync(tenantA);
        var adminB = await _host.AdminAsync(tenantB);
        var personA = await ContactsTestHost.CreatedIdAsync(await ContactsTestHost.CreateAsync(adminA, "Ana"));
        var orgA = await ContactsTestHost.CreatedIdAsync(await ContactsTestHost.CreateAsync(adminA, "Acme", kind: "Organization"));
        var personB = await ContactsTestHost.CreatedIdAsync(await ContactsTestHost.CreateAsync(adminB, "Ben"));
        var orgB = await ContactsTestHost.CreatedIdAsync(await ContactsTestHost.CreateAsync(adminB, "Globex", kind: "Organization"));

        (await adminA.PostAsJsonAsync($"/api/contacts/{personA}/links", new { organizationId = orgA }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await adminA.PostAsJsonAsync($"/api/contacts/{personA}/links", new { organizationId = orgB }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await adminA.PostAsJsonAsync($"/api/contacts/{personA}/merge", new { intoId = personB }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await adminB.GetAsync($"/api/contacts/{personA}/links")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
