using System.Security.Claims;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The two read rules a list asks before it lets a caller filter: may this caller see the field,
/// and may they see a document at this sensitivity. The scrub asks the same two, so these are
/// also what decides what a response masks.
/// </summary>
public class SensitivityReadRuleTests
{
    // No rule here reads the schema, so no session is needed. A test that reached for one would
    // fail on the null, which is the point.
    private static ISensitivityService Service(string? mode = null) =>
        new SensitivityService(
            null!,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Sensitivity:Mode"] = mode })
                .Build());

    private static HttpContext Caller(params string[] roles) => new DefaultHttpContext
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
            roles.Select(r => new Claim(ClaimTypes.Role, r)), authenticationType: "test")),
    };

    private static FieldDefinition Field(SensitivityLevel level, params string[] visibleTo) => new()
    {
        Name = "Salary",
        Type = "number",
        Sensitivity = level,
        VisibleToRoles = visibleTo.ToList(),
    };

    [Theory]
    [InlineData(SensitivityLevel.Public, "Viewer", true)]
    [InlineData(SensitivityLevel.Sensitive, "Viewer", false)]
    [InlineData(SensitivityLevel.Sensitive, "HR", true)]
    [InlineData(SensitivityLevel.Hidden, "HR", false)]
    [InlineData(SensitivityLevel.Hidden, "SuperAdmin", true)]
    public void A_field_is_readable_by_the_roles_its_level_allows(SensitivityLevel level, string role, bool expected)
    {
        Service().MaySeeField(Field(level), Caller(role)).Should().Be(expected);
    }

    [Fact]
    public void A_field_that_names_its_roles_is_readable_by_those_and_not_by_the_level_default()
    {
        var field = Field(SensitivityLevel.Sensitive, "Finance");

        Service().MaySeeField(field, Caller("Finance")).Should().BeTrue();
        Service().MaySeeField(field, Caller("HR")).Should().BeFalse(
            "HR is the default for Sensitive, and a field that lists its own roles replaces the default");
    }

    [Theory]
    [InlineData(SensitivityLevel.Public, "Viewer", true)]
    [InlineData(SensitivityLevel.Sensitive, "Viewer", false)]
    [InlineData(SensitivityLevel.Sensitive, "HR", true)]
    [InlineData(SensitivityLevel.Hidden, "HR", false)]
    [InlineData(SensitivityLevel.Hidden, "SuperAdmin", true)]
    public void A_document_is_readable_by_the_roles_its_level_allows(SensitivityLevel level, string role, bool expected)
    {
        Service().MaySeeDocument(level, Caller(role)).Should().Be(expected);
    }

    [Fact]
    public void With_the_mode_off_every_field_and_document_is_readable()
    {
        var viewer = Caller("Viewer");

        Service().MaySeeField(Field(SensitivityLevel.Hidden), viewer).Should().BeFalse(
            "in the default mode a plain caller is refused, or the lines below prove nothing");
        Service().MaySeeDocument(SensitivityLevel.Hidden, viewer).Should().BeFalse();

        Service("Off").MaySeeField(Field(SensitivityLevel.Hidden), viewer).Should().BeTrue(
            "nothing is masked with the mode off, so nothing is withheld from a filter either");
        Service("Off").MaySeeField(Field(SensitivityLevel.Sensitive), viewer).Should().BeTrue();
        Service("Off").MaySeeDocument(SensitivityLevel.Hidden, viewer).Should().BeTrue();
        Service("Off").MaySeeDocument(SensitivityLevel.Sensitive, viewer).Should().BeTrue();
    }

    [Fact]
    public async Task With_the_mode_off_the_scrub_leaves_a_hidden_document_as_it_is()
    {
        var data = new Dictionary<string, object> { ["Salary"] = 60000 };

        var hidden = await Service("Off").ApplyAsync(
            "staff", SensitivityLevel.Hidden, data, Caller("Viewer"), TestContext.Current.CancellationToken);

        hidden.Should().BeFalse();
        data.Should().ContainKey("Salary");
    }

    [Theory]
    [InlineData(SensitivityLevel.Hidden, true)]
    [InlineData(SensitivityLevel.Sensitive, false)]
    public async Task The_scrub_clears_a_document_the_read_rule_refuses(SensitivityLevel level, bool reportedHidden)
    {
        var data = new Dictionary<string, object> { ["Salary"] = 60000 };

        var hidden = await Service().ApplyAsync(
            "staff", level, data, Caller("Viewer"), TestContext.Current.CancellationToken);

        hidden.Should().Be(reportedHidden, "only a Hidden document reports itself as hidden");
        data.Should().BeEmpty();
    }
}
