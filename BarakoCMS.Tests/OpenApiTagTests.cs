using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The OpenAPI document's tags, asserted against the document the running app actually serves.
/// </summary>
/// <remarks>
/// Generators group methods by tag. Before this, FastEndpoints' path-segment auto-tagging put all
/// but three operations on the tag "Api" (every route starts /api/), so a generated client was one
/// class with every method on it. Tags now come from the endpoint's namespace, so these tests are
/// what stops a new endpoint in an unexpected namespace quietly rejoining a catch-all.
/// </remarks>
[Collection("Sequential")]
public class OpenApiTagTests
{
    private readonly IntegrationTestFixture _factory;

    public OpenApiTagTests(IntegrationTestFixture factory) => _factory = factory;

    internal const string DocumentPath = "/swagger/v1/swagger.json";

    internal static async Task<JsonDocument> FetchDocumentAsync(IntegrationTestFixture factory)
    {
        var response = await factory.CreateClient().GetAsync(DocumentPath);
        response.IsSuccessStatusCode.Should().BeTrue(
            $"{DocumentPath} should serve the OpenAPI document (got {(int)response.StatusCode})");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static IEnumerable<(string Path, string Method, List<string> Tags)> Operations(JsonDocument doc)
    {
        foreach (var path in doc.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                var tags = operation.Value.TryGetProperty("tags", out var t)
                    ? t.EnumerateArray().Select(x => x.GetString()!).ToList()
                    : [];
                yield return (path.Name, operation.Name, tags);
            }
        }
    }

    [Fact]
    public async Task No_operation_carries_the_catch_all_Api_tag()
    {
        using var doc = await FetchDocumentAsync(_factory);
        var operations = Operations(doc).ToList();

        // The control. An empty document would report no offenders and prove nothing.
        operations.Should().HaveCountGreaterThan(50, "the document has to describe the API for this to mean anything");

        var offenders = operations
            .Where(o => o.Tags.Contains("Api"))
            .Select(o => $"{o.Method.ToUpperInvariant()} {o.Path}")
            .ToList();

        offenders.Should().BeEmpty(
            "an operation tagged Api rejoins the catch-all group a client generator flattens into one class");
    }

    [Fact]
    public async Task Every_operation_carries_exactly_one_tag()
    {
        using var doc = await FetchDocumentAsync(_factory);
        var operations = Operations(doc).ToList();

        operations.Should().NotBeEmpty("the document must describe some operations for this to prove anything");

        var untagged = operations
            .Where(o => o.Tags.Count != 1)
            .Select(o => $"{o.Method.ToUpperInvariant()} {o.Path} -> [{string.Join(", ", o.Tags)}]")
            .ToList();

        untagged.Should().BeEmpty("a generator needs exactly one group per operation");
    }

    [Fact]
    public async Task The_tag_set_is_pinned()
    {
        using var doc = await FetchDocumentAsync(_factory);

        // "Delivery" is excluded: it exists only while some content type is publicly deliverable,
        // which depends on what the rest of the suite has stored. DeliveryOpenApiTests covers it.
        var tags = Operations(doc)
            .SelectMany(o => o.Tags)
            .Where(t => t != "Delivery")
            .Distinct()
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        // Tagging by convention means a namespace rename silently renames a client's method group,
        // which is a breaking change for consumers with nothing in the diff that looks like one.
        // This list is that diff. Adding a slice adds a line here on purpose.
        //
        // Analytics.Umami, DeviceTrust and Import were missing from this list until the module test
        // suites landed. Not because those modules had no endpoints, but because nothing in the test
        // host referenced them, so their assemblies were never loaded and their operations never
        // reached the document. The shipped suite loads all of them. The pin was describing the test
        // host rather than the product, which is worth remembering if a tag ever goes missing here.
        var expected = new[]
        {
            "AI", "Accounting", "Analytics.Umami", "ApiKeys", "Audit", "Auth", "Content",
            "ContentType", "DeviceTrust", "Diagnostics", "Email.Resend", "ExternalAuth",
            "FeatureFlags", "Files", "Import", "Me", "Monitoring", "Portability", "Preview",
            "Public", "Pwa", "Roles", "Settings", "Tenants", "UserGroups", "Users", "Workflows",
        };

        tags.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Monitoring_keeps_the_tag_it_sets_for_itself()
    {
        using var doc = await FetchDocumentAsync(_factory);

        Operations(doc).Where(o => o.Tags.Contains("Monitoring")).Should().NotBeEmpty(
            "the three endpoints that call WithTags(\"Monitoring\") are the escape hatch and must still win");
    }

    [Theory]
    [InlineData("barakoCMS.Features.Content.Create", "Content")]
    [InlineData("barakoCMS.Features.ContentType.SetPublicDelivery", "ContentType")]
    [InlineData("barakoCMS.Features.Public", "Public")]
    [InlineData("BarakoCMS.Accounting.Features.Reports", "Accounting")]
    [InlineData("BarakoCMS.Analytics.Umami.Features", "Analytics.Umami")]
    [InlineData("BarakoCMS.Files.Features.Upload", "Files")]
    [InlineData("BarakoCMS.AI.Features", "AI")]
    [InlineData("BarakoCMS.ExternalAuth", "ExternalAuth")]
    [InlineData("BarakoCMS.Email.Resend", "Email.Resend")]
    public void The_namespace_convention_maps_to_the_documented_tag(string ns, string expected)
        => barakoCMS.Infrastructure.OpenApi.EndpointTagConvention.ForNamespace(ns).Should().Be(expected);

    [Fact]
    public void An_empty_namespace_yields_no_tag()
        => barakoCMS.Infrastructure.OpenApi.EndpointTagConvention.ForNamespace(null).Should().BeNull();
}
