using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Infrastructure.Viewers;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A valid viewer token opens delivery of Members entries and nothing else (#912). It is signed
/// under a separate audience, so the bearer scheme must refuse it on every authoring route,
/// whether it travels as a bearer token or in the viewer header.
/// </summary>
[Collection("Integration1")]
public class ViewerTokenAuthoringRoutesTests
{
    private const string TypeName = "viewer_authoring_probe";

    private readonly IntegrationTestFixture _factory;

    public ViewerTokenAuthoringRoutesTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Viewer() =>
        ViewerToken.Create(_factory.Services.GetRequiredService<IConfiguration>(), Tenant.DefaultSlug, "member-1").Token;

    private async Task SeedMembersEntryAsync()
    {
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        await using var s = store.LightweightSession();

        // The database is shared and the name is unique, so the second case must find the first's rows.
        if (await s.Query<ContentTypeDefinition>().AnyAsync(d => d.Name == TypeName, Ct))
            return;

        s.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = TypeName, DisplayName = TypeName,
            IsPubliclyDeliverable = true,
            RouteTemplate = $"/{TypeName}/{{slug}}",
            Lookup = new LookupDefinition { MatchField = "Slug", VerifyFields = ["Title"], ReturnFields = ["Title"] },
            Fields = new()
            {
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug" },
            },
        });
        s.Store(new Content
        {
            Id = Guid.NewGuid(), ContentType = TypeName, Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Members, SearchText = "club",
            Data = new() { ["Title"] = "T-club", ["Slug"] = "club" },
        });
        await s.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task The_viewer_token_reads_a_members_entry_through_delivery()
    {
        await SeedMembersEntryAsync();
        var client = _factory.CreateClient();

        (await client.GetAsync($"/api/public/{TypeName}/club", Ct)).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "anonymous callers do not see a Members entry");

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/public/{TypeName}/club");
        req.Headers.Add(ViewerToken.Header, Viewer());
        var res = await client.SendAsync(req, Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
    }

    public static TheoryData<string, string> AuthoringRoutes => new()
    {
        { "GET", "/api/contents" },
        { "POST", "/api/contents" },
        { "POST", "/api/contents/batch" },
        { "PUT", "/api/contents/00000000-0000-0000-0000-000000000001" },
        { "PUT", "/api/contents/00000000-0000-0000-0000-000000000001/status" },
        { "DELETE", "/api/contents/00000000-0000-0000-0000-000000000001/erase" },
        { "POST", "/api/content-types" },
        { "POST", "/api/content-types/viewer_authoring_probe/fields" },
        { "PUT", "/api/content-types/viewer_authoring_probe/fields/Title/sensitivity" },
        { "POST", "/api/files" },
        { "POST", "/api/roles" },
        { "POST", "/api/viewer-tokens" },
        { "GET", "/api/me/tenants" },
    };

    private static readonly HttpStatusCode[] Refused = [HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden];

    private static HttpRequestMessage Request(string method, string url) =>
        new(new HttpMethod(method), url)
        {
            Content = method is "GET" or "DELETE" ? null : BodyFor(url),
        };

    // The upload route answers 415 to anything but a form before authorization runs, so it needs
    // a multipart body to reach the check under test.
    private static HttpContent BodyFor(string url)
    {
        if (!url.StartsWith("/api/files", StringComparison.Ordinal))
            return JsonContent.Create(new { });

        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("probe"u8.ToArray()), "file", "probe.txt");
        return form;
    }

    [Theory]
    [MemberData(nameof(AuthoringRoutes))]
    public async Task A_viewer_token_sent_as_a_bearer_token_opens_no_authoring_route(string method, string url)
    {
        await SeedMembersEntryAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Viewer());

        var res = await client.SendAsync(Request(method, url), Ct);

        res.StatusCode.Should().BeOneOf(Refused, $"{method} {url} must not accept a viewer token");
    }

    [Theory]
    [MemberData(nameof(AuthoringRoutes))]
    public async Task A_viewer_token_sent_in_the_viewer_header_opens_no_authoring_route(string method, string url)
    {
        await SeedMembersEntryAsync();
        var client = _factory.CreateClient();

        var req = Request(method, url);
        req.Headers.Add(ViewerToken.Header, Viewer());
        var res = await client.SendAsync(req, Ct);

        res.StatusCode.Should().BeOneOf(Refused, $"{method} {url} must not read the viewer header");
    }
}
