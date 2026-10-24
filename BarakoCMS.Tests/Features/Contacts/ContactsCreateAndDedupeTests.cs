using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Contacts;

/// <summary>A contact is created once per email in a tenant, and the second attempt says who has it.</summary>
[Collection("Integration3")]
public class ContactsCreateAndDedupeTests(IntegrationTestFixture factory)
{
    private readonly ContactsTestHost _host = new(factory);

    [Fact]
    public async Task A_person_is_created_and_read_back_with_every_channel()
    {
        var tenant = await _host.TenantAsync();
        var admin = await _host.AdminAsync(tenant);
        var email = ContactsTestHost.UniqueEmail();

        var id = await ContactsTestHost.CreatedIdAsync(await ContactsTestHost.CreateAsync(admin, "Ana Reyes", email));

        var read = await ContactsTestHost.Json(await admin.GetAsync($"/api/contacts/{id}"));
        read.GetProperty("displayName").GetString().Should().Be("Ana Reyes");
        read.GetProperty("kind").GetString().Should().Be("Person");
        read.GetProperty("email").GetString().Should().Be(email);
        read.GetProperty("phone").GetString().Should().NotBeNullOrEmpty();
        read.GetProperty("address").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_second_contact_with_the_same_email_in_other_letters_is_refused_naming_the_first()
    {
        var tenant = await _host.TenantAsync();
        var admin = await _host.AdminAsync(tenant);
        var email = ContactsTestHost.UniqueEmail();
        var first = await ContactsTestHost.CreatedIdAsync(await ContactsTestHost.CreateAsync(admin, "First", email));

        var second = await ContactsTestHost.CreateAsync(admin, "Second", $"  {email.ToUpperInvariant()} ");

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await second.Content.ReadAsStringAsync()).Should().Contain(first.ToString());
    }

    [Fact]
    public async Task Two_creates_of_one_email_that_race_leave_one_contact_and_never_a_500()
    {
        var tenant = await _host.TenantAsync();
        var admin = await _host.AdminAsync(tenant);
        var email = ContactsTestHost.UniqueEmail();

        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(i => ContactsTestHost.CreateAsync(admin, $"Racer {i}", email)));

        results.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        results.Where(r => r.StatusCode != HttpStatusCode.Created)
            .Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Conflict);

        var held = await ContactsTestHost.Json(await admin.GetAsync($"/api/contacts?email={Uri.EscapeDataString(email)}"));
        held.GetProperty("totalItems").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Contacts_without_an_email_are_never_duplicates_of_each_other()
    {
        var tenant = await _host.TenantAsync();
        var admin = await _host.AdminAsync(tenant);

        var one = await ContactsTestHost.CreateAsync(admin, "No Email One");
        var two = await ContactsTestHost.CreateAsync(admin, "No Email Two");

        one.StatusCode.Should().Be(HttpStatusCode.Created);
        two.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Updating_a_contact_to_an_email_another_holds_is_refused()
    {
        var tenant = await _host.TenantAsync();
        var admin = await _host.AdminAsync(tenant);
        var taken = ContactsTestHost.UniqueEmail();
        await ContactsTestHost.CreatedIdAsync(await ContactsTestHost.CreateAsync(admin, "Holder", taken));
        var other = await ContactsTestHost.CreatedIdAsync(await ContactsTestHost.CreateAsync(admin, "Other", ContactsTestHost.UniqueEmail()));

        var put = await admin.PutAsJsonAsync($"/api/contacts/{other}", new { kind = "Person", displayName = "Other", email = taken });

        put.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_kind_that_is_neither_person_nor_organization_is_a_400()
    {
        var tenant = await _host.TenantAsync();
        var admin = await _host.AdminAsync(tenant);

        var created = await ContactsTestHost.CreateAsync(admin, "Odd", kind: "Robot");

        created.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Custom_fields_are_checked_against_the_tenants_contact_record_type()
    {
        var tenant = await _host.TenantAsync();
        await _host.DeclareTypeAsync(tenant, deliverable: false, d =>
            d.Fields.Add(new() { Name = "memberNumber", Type = "int", IsRequired = true }));
        var admin = await _host.AdminAsync(tenant);

        var missing = await ContactsTestHost.CreateAsync(admin, "No Number", ContactsTestHost.UniqueEmail());
        var wrongType = await ContactsTestHost.CreateAsync(admin, "Bad Number", ContactsTestHost.UniqueEmail(),
            fields: new() { ["memberNumber"] = "not a number" });
        var good = await ContactsTestHost.CreateAsync(admin, "Good Number", ContactsTestHost.UniqueEmail(),
            fields: new() { ["memberNumber"] = 42 });

        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        wrongType.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var id = await ContactsTestHost.CreatedIdAsync(good);
        var read = await ContactsTestHost.Json(await admin.GetAsync($"/api/contacts/{id}"));
        read.GetProperty("fields").GetProperty("memberNumber").GetInt32().Should().Be(42);
    }

    [Fact]
    public async Task A_standard_field_sent_inside_fields_is_a_400()
    {
        var tenant = await _host.TenantAsync();
        var admin = await _host.AdminAsync(tenant);

        var created = await ContactsTestHost.CreateAsync(admin, "Sneaky", fields: new() { ["email"] = "x@example.com" });

        created.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_list_is_paged_and_filters_by_kind()
    {
        var tenant = await _host.TenantAsync();
        var admin = await _host.AdminAsync(tenant);
        for (var i = 0; i < 3; i++)
        {
            await ContactsTestHost.CreatedIdAsync(await ContactsTestHost.CreateAsync(admin, $"Person {i}"));
        }

        await ContactsTestHost.CreatedIdAsync(await ContactsTestHost.CreateAsync(admin, "Acme", kind: "Organization"));

        var page = await ContactsTestHost.Json(await admin.GetAsync("/api/contacts?pageSize=2"));
        page.GetProperty("items").GetArrayLength().Should().Be(2);
        page.GetProperty("totalItems").GetInt32().Should().Be(4);

        var orgs = await ContactsTestHost.Json(await admin.GetAsync("/api/contacts?kind=Organization"));
        orgs.GetProperty("totalItems").GetInt32().Should().Be(1);
        orgs.GetProperty("items")[0].GetProperty("displayName").GetString().Should().Be("Acme");
    }
}
