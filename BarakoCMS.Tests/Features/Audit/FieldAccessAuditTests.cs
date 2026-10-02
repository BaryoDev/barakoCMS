using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Audit;

/// <summary>
/// A field's role list decides who reads its values, the same as its level does. The row for a
/// sensitivity change held the level only, so widening the list left a row that showed no change,
/// and a field added as Sensitive left a row that did not say so.
/// </summary>
[Collection("Sequential")]
public class FieldAccessAuditTests
{
    private readonly IntegrationTestFixture _factory;

    public FieldAccessAuditTests(IntegrationTestFixture factory) => _factory = factory;

    private async Task<HttpClient> AdminAsync()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("Admin", "SuperAdmin"));
        return client;
    }

    private async Task<ContentTypeDefinition> TypeAsync(params FieldDefinition[] extra)
    {
        var name = "audfield-" + Guid.NewGuid().ToString("n")[..12];
        var fields = new List<FieldDefinition> { new() { Name = "Title", DisplayName = "Title", Type = "string" } };
        fields.AddRange(extra);

        var definition = new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = name,
            Fields = fields,
        };

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(definition);
        await session.SaveChangesAsync();
        return definition;
    }

    [Fact]
    public async Task Widening_a_fields_role_list_records_the_list_before_and_after()
    {
        var type = await TypeAsync(new FieldDefinition
        {
            Name = "Salary",
            DisplayName = "Salary",
            Type = "string",
            Sensitivity = SensitivityLevel.Sensitive,
            VisibleToRoles = new List<string> { "HR" },
        });
        var client = await AdminAsync();

        var changed = await client.PutAsJsonAsync(
            $"/api/content-types/{type.Name}/fields/Salary/sensitivity",
            new { sensitivity = "Sensitive", visibleToRoles = new[] { "HR", "Payroll" }, mask = "Last4" });
        changed.StatusCode.Should().Be(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync());

        var rows = await AuditRows.ForTargetAsync(
            _factory, "contenttype.field.sensitivity.changed", type.Id.ToString());

        rows.Should().HaveCount(1);
        var row = rows[0];
        row.Text("from").Should().Be("Sensitive");
        row.Text("to").Should().Be("Sensitive");
        row.Strings("visibleToRolesFrom").Should().Equal("HR");
        row.Strings("visibleToRolesTo").Should().Equal("HR", "Payroll");
        row.Text("maskFrom").Should().Be("Default");
        row.Text("maskTo").Should().Be("Last4");
    }

    [Fact]
    public async Task Adding_a_sensitive_field_records_its_level_and_role_list()
    {
        var type = await TypeAsync();
        var client = await AdminAsync();

        var added = await client.PostAsJsonAsync($"/api/content-types/{type.Name}/fields", new
        {
            fieldName = "Diagnosis",
            displayName = "Diagnosis",
            type = "string",
            sensitivity = "Sensitive",
            visibleToRoles = new[] { "Clinician" },
        });
        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync());

        var rows = await AuditRows.ForTargetAsync(_factory, "contenttype.field_added", type.Name);

        rows.Should().HaveCount(1);
        rows[0].Text("field").Should().Be("Diagnosis");
        rows[0].Text("sensitivity").Should().Be("Sensitive");
        rows[0].Strings("visibleToRoles").Should().Equal("Clinician");
    }
}
