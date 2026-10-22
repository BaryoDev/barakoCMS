using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Contacts;

/// <summary>Links join a person to an organization, and a merge folds a duplicate into the survivor.</summary>
[Collection("Sequential")]
public class ContactsMergeAndLinkTests(IntegrationTestFixture factory)
{
    private readonly ContactsTestHost _host = new(factory);

    private static async Task<Guid> CreateAsync(HttpClient client, string name, string? email = null, string kind = "Person") =>
        await ContactsTestHost.CreatedIdAsync(await ContactsTestHost.CreateAsync(client, name, email, kind));

    [Fact]
    public async Task A_person_is_linked_to_an_organization_and_the_link_reads_from_both_sides()
    {
        var tenant = await _host.TenantAsync();
        var admin = await _host.AdminAsync(tenant);
        var person = await CreateAsync(admin, "Ana");
        var org = await CreateAsync(admin, "Acme", kind: "Organization");

        var linked = await admin.PostAsJsonAsync($"/api/contacts/{person}/links", new { organizationId = org, role = "Treasurer" });

        linked.StatusCode.Should().Be(HttpStatusCode.Created);
        foreach (var side in new[] { person, org })
        {
            var links = await ContactsTestHost.Json(await admin.GetAsync($"/api/contacts/{side}/links"));
            links.GetProperty("items").GetArrayLength().Should().Be(1);
            links.GetProperty("items")[0].GetProperty("role").GetString().Should().Be("Treasurer");
        }
    }

    [Fact]
    public async Task Linking_the_same_pair_again_changes_the_role_and_does_not_add_a_row()
    {
        var tenant = await _host.TenantAsync();
        var admin = await _host.AdminAsync(tenant);
        var person = await CreateAsync(admin, "Ana");
        var org = await CreateAsync(admin, "Acme", kind: "Organization");
        (await admin.PostAsJsonAsync($"/api/contacts/{person}/links", new { organizationId = org, role = "Buyer" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var again = await admin.PostAsJsonAsync($"/api/contacts/{person}/links", new { organizationId = org, role = "Owner" });

        again.StatusCode.Should().Be(HttpStatusCode.OK);
        var links = await ContactsTestHost.Json(await admin.GetAsync($"/api/contacts/{person}/links"));
        links.GetProperty("items").GetArrayLength().Should().Be(1);
        links.GetProperty("items")[0].GetProperty("role").GetString().Should().Be("Owner");
    }

    [Fact]
    public async Task A_link_must_join_a_person_to_an_organization()
    {
        var tenant = await _host.TenantAsync();
        var admin = await _host.AdminAsync(tenant);
        var one = await CreateAsync(admin, "Ana");
        var two = await CreateAsync(admin, "Ben");

        var linked = await admin.PostAsJsonAsync($"/api/contacts/{one}/links", new { organizationId = two });

        linked.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_removed_link_is_gone_and_deleting_a_contact_removes_its_links()
    {
        var tenant = await _host.TenantAsync();
        var admin = await _host.AdminAsync(tenant);
        var person = await CreateAsync(admin, "Ana");
        var org = await CreateAsync(admin, "Acme", kind: "Organization");
        var other = await CreateAsync(admin, "Globex", kind: "Organization");
        await admin.PostAsJsonAsync($"/api/contacts/{person}/links", new { organizationId = org });
        await admin.PostAsJsonAsync($"/api/contacts/{person}/links", new { organizationId = other });

        (await admin.DeleteAsync($"/api/contacts/{person}/links/{org}")).IsSuccessStatusCode.Should().BeTrue();
        var left = await ContactsTestHost.Json(await admin.GetAsync($"/api/contacts/{person}/links"));
        left.GetProperty("items").GetArrayLength().Should().Be(1);

        (await admin.DeleteAsync($"/api/contacts/{person}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var orgLinks = await ContactsTestHost.Json(await admin.GetAsync($"/api/contacts/{other}/links"));
        orgLinks.GetProperty("totalItems").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task A_merge_keeps_what_the_survivor_has_fills_what_it_lacks_and_moves_the_links()
    {
        var tenant = await _host.TenantAsync();
        await _host.DeclareTypeAsync(tenant, deliverable: false, d =>
        {
            d.Fields.Add(new() { Name = "nickname", Type = "string" });
            d.Fields.Add(new() { Name = "team", Type = "string" });
        });
        var admin = await _host.AdminAsync(tenant);
        var org = await CreateAsync(admin, "Acme", kind: "Organization");
        var survivor = await ContactsTestHost.CreatedIdAsync(await admin.PostAsJsonAsync("/api/contacts", new
        {
            kind = "Person",
            displayName = "Ana Reyes",
            phone = "+63 111",
            address = "Survivor Street",
            fields = new Dictionary<string, object> { ["nickname"] = "Ana" },
        }));
        var duplicateEmail = ContactsTestHost.UniqueEmail();
        var duplicate = await ContactsTestHost.CreatedIdAsync(await admin.PostAsJsonAsync("/api/contacts", new
        {
            kind = "Person",
            displayName = "A. Reyes",
            email = duplicateEmail,
            phone = "+63 222",
            address = "Duplicate Street",
            fields = new Dictionary<string, object> { ["nickname"] = "Annie", ["team"] = "Sales" },
        }));
        await admin.PostAsJsonAsync($"/api/contacts/{duplicate}/links", new { organizationId = org, role = "Buyer" });

        var merged = await admin.PostAsJsonAsync($"/api/contacts/{duplicate}/merge", new { intoId = survivor });

        merged.StatusCode.Should().Be(HttpStatusCode.OK, await merged.Content.ReadAsStringAsync());
        var kept = await ContactsTestHost.Json(await admin.GetAsync($"/api/contacts/{survivor}"));
        kept.GetProperty("displayName").GetString().Should().Be("Ana Reyes");
        kept.GetProperty("email").GetString().Should().Be(duplicateEmail);
        kept.GetProperty("phone").GetString().Should().Be("+63 111");
        kept.GetProperty("address").GetString().Should().Be("Survivor Street");
        var fields = kept.GetProperty("fields");
        fields.GetProperty("nickname").GetString().Should().Be("Ana");
        fields.GetProperty("team").GetString().Should().Be("Sales");

        var links = await ContactsTestHost.Json(await admin.GetAsync($"/api/contacts/{survivor}/links"));
        links.GetProperty("items").GetArrayLength().Should().Be(1);
        links.GetProperty("items")[0].GetProperty("role").GetString().Should().Be("Buyer");

        (await admin.GetAsync($"/api/contacts/{duplicate}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var listed = await ContactsTestHost.Json(await admin.GetAsync("/api/contacts?pageSize=50"));
        var ids = listed.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        ids.Should().NotBeEmpty();
        ids.Should().Contain(survivor).And.NotContain(duplicate);
    }

    [Fact]
    public async Task A_contact_with_links_cannot_change_kind()
    {
        var tenant = await _host.TenantAsync();
        var admin = await _host.AdminAsync(tenant);
        var person = await CreateAsync(admin, "Ana");
        var org = await CreateAsync(admin, "Acme", kind: "Organization");
        var loner = await CreateAsync(admin, "Solo");
        await admin.PostAsJsonAsync($"/api/contacts/{person}/links", new { organizationId = org });

        (await admin.PutAsJsonAsync($"/api/contacts/{person}", new { kind = "Organization", displayName = "Ana" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.PutAsJsonAsync($"/api/contacts/{loner}", new { kind = "Organization", displayName = "Solo" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var unchanged = await ContactsTestHost.Json(await admin.GetAsync($"/api/contacts/{person}"));
        unchanged.GetProperty("kind").GetString().Should().Be("Person");
    }

    [Fact]
    public async Task A_merged_contact_cannot_be_deleted()
    {
        var tenant = await _host.TenantAsync();
        var admin = await _host.AdminAsync(tenant);
        var survivor = await CreateAsync(admin, "Ana");
        var duplicate = await CreateAsync(admin, "A.");
        (await admin.PostAsJsonAsync($"/api/contacts/{duplicate}/merge", new { intoId = survivor }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await admin.DeleteAsync($"/api/contacts/{duplicate}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.GetAsync($"/api/contacts/{survivor}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_merge_is_the_way_out_of_a_duplicate_email_conflict()
    {
        var tenant = await _host.TenantAsync();
        var admin = await _host.AdminAsync(tenant);
        var email = ContactsTestHost.UniqueEmail();
        var first = await CreateAsync(admin, "Ana", email);
        (await ContactsTestHost.CreateAsync(admin, "Ana again", email)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var withoutEmail = await CreateAsync(admin, "Ana again");

        var merged = await admin.PostAsJsonAsync($"/api/contacts/{withoutEmail}/merge", new { intoId = first });

        merged.StatusCode.Should().Be(HttpStatusCode.OK);
        var held = await ContactsTestHost.Json(await admin.GetAsync($"/api/contacts?email={Uri.EscapeDataString(email)}"));
        held.GetProperty("totalItems").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task A_person_and_an_organization_cannot_be_merged_and_a_contact_cannot_merge_into_itself()
    {
        var tenant = await _host.TenantAsync();
        var admin = await _host.AdminAsync(tenant);
        var person = await CreateAsync(admin, "Ana");
        var org = await CreateAsync(admin, "Acme", kind: "Organization");

        (await admin.PostAsJsonAsync($"/api/contacts/{person}/merge", new { intoId = org }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.PostAsJsonAsync($"/api/contacts/{person}/merge", new { intoId = person }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.GetAsync($"/api/contacts/{person}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
