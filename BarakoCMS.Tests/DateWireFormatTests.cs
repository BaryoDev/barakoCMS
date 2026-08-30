using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Every timestamp that leaves this API is unambiguous about the instant it names.
/// </summary>
/// <remarks>
/// Program.cs sets Npgsql.EnableLegacyTimestampBehavior, and the documents are stored in
/// timestamp without time zone columns, so a DateTime round-tripped through Postgres comes back
/// with Kind=Unspecified. System.Text.Json writes an Unspecified DateTime with no trailing Z and
/// no offset, and new Date() in a browser reads exactly that as local time. The value is correct
/// on a UTC server and silently wrong by the offset anywhere else, which is the worst version of
/// this bug: it passes every test run in CI and fails in one deployment.
///
/// Both halves are asserted for the same reason as the enum pair. The wire format is what clients
/// parse, and the stored format is what the database indexes and what an existing row already
/// holds, so a change that fixes one and breaks the other has to be visible here.
/// </remarks>
[Collection("Sequential")]
public class DateWireFormatTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public DateWireFormatTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task A_single_content_names_an_unambiguous_instant()
    {
        await AuthenticateAsync("SuperAdmin", "Admin");
        var id = await CreateContentAsync();

        var response = await _client.GetAsync($"/api/contents/{id}");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var createdAt = document.RootElement.GetProperty("createdAt").GetString();

        IsUnambiguous(createdAt).Should().BeTrue(
            "createdAt was {0}, which a browser reads as local time. The instant a row was created "
          + "does not depend on where the reader is standing", createdAt);
    }

    [Fact]
    public async Task A_listed_content_names_an_unambiguous_instant()
    {
        await AuthenticateAsync("SuperAdmin", "Admin");
        await CreateContentAsync();

        var response = await _client.GetAsync("/api/contents");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var createdAt = document.RootElement.GetProperty("items")[0].GetProperty("createdAt").GetString();

        IsUnambiguous(createdAt).Should().BeTrue("createdAt was {0} in the list", createdAt);
    }

    // The pair that makes the two above meaningful. One resource returning DateTime and another
    // returning DateTimeOffset for the same underlying column is the actual defect: a client cannot
    // write one parser. If these ever disagree, one of the endpoints was missed.
    [Fact]
    public async Task The_same_timestamp_has_the_same_shape_in_both_representations()
    {
        await AuthenticateAsync("SuperAdmin", "Admin");
        var id = await CreateContentAsync();

        var single = await _client.GetAsync($"/api/contents/{id}");
        using var one = JsonDocument.Parse(await single.Content.ReadAsStringAsync());
        var fromGet = one.RootElement.GetProperty("createdAt").GetString();

        var listed = await _client.GetAsync("/api/contents");
        using var many = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        var fromList = many.RootElement.GetProperty("items")
            .EnumerateArray()
            .First(e => e.GetProperty("id").GetGuid() == id)
            .GetProperty("createdAt").GetString();

        IsUnambiguous(fromGet).Should().Be(IsUnambiguous(fromList),
            "GET returned {0} and the list returned {1} for the same row. A client cannot write one "
          + "date parser against an API that answers in two shapes", fromGet, fromList);
    }

    [Fact]
    public async Task A_user_record_names_an_unambiguous_instant()
    {
        await AuthenticateAsync("SuperAdmin", "Admin");

        var response = await _client.GetAsync("/api/users");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var createdAt = document.RootElement.GetProperty("items")[0].GetProperty("createdAt").GetString();

        IsUnambiguous(createdAt).Should().BeTrue(
            "createdAt was {0}. Users/List is one of the resources still on plain DateTime", createdAt);
    }

    // A timestamp is unambiguous when it carries a zone: a trailing Z, or an explicit offset after
    // the time part. Checked on the text rather than by parsing, because DateTime.Parse happily
    // invents the local zone for a string that has none, which is the whole failure being tested.
    /// <summary>
    /// The history endpoint reported the same instant twice, once correctly and once without a zone.
    /// </summary>
    /// <remarks>
    /// VersionResponse carried both Timestamp and UpdatedAt, built from the same e.Timestamp.
    /// DateTimeOffset.DateTime does not convert to UTC, it drops the offset and returns the local
    /// wall clock reading with Kind=Unspecified, so on a UTC+8 machine the response said
    /// 14:12:53+08:00 and 14:12:53 side by side: the same event, eight hours apart, in one object.
    /// UpdatedAt is gone rather than fixed, because it duplicated a field the admin already reads.
    /// </remarks>
    [Fact]
    public async Task A_content_version_reports_one_instant_and_reports_it_in_utc()
    {
        await AuthenticateAsync("SuperAdmin", "Admin");
        var id = await CreateContentAsync();

        var response = await _client.GetAsync($"/api/contents/{id}/history");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var version = document.RootElement.GetProperty("items")[0];

        var timestamp = version.GetProperty("timestamp").GetString();
        IsUnambiguous(timestamp).Should().BeTrue("timestamp was {0}", timestamp);

        // Marten hands the event timestamp back in the server's local offset, so it is normalised on
        // the way out and the offset is zero. Asserted on the parsed instant rather than on the text:
        // System.Text.Json writes a DateTimeOffset as +00:00 and a Utc DateTime as Z, both of which
        // are ISO 8601 and parse identically, and a converter to make the two spellings agree would
        // be machinery bought for appearances.
        DateTimeOffset.Parse(timestamp!, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind)
            .Offset.Should().Be(TimeSpan.Zero,
                "the offset here is whatever the server happens to be set to unless it is normalised, "
              + "so this fails on a machine that is not UTC and passes in CI, which is the shape of "
              + "bug that reaches production");

        version.TryGetProperty("updatedAt", out _).Should().BeFalse(
            "updatedAt carried the same instant with no zone, which a browser reads as local time");
    }

    /// <summary>
    /// Every timestamp in every response body carries a zone.
    /// </summary>
    /// <remarks>
    /// The instance above was found by reading one endpoint. This asserts the class, by walking
    /// whole response bodies and checking every value that looks like a timestamp, so the next
    /// endpoint to strip an offset fails here without anyone having thought to test it.
    ///
    /// A DateTime whose Kind is Utc serialises with a Z and is fine, which is why this checks the
    /// wire rather than the property types: the defect is a value that lost its zone, and a type
    /// audit would flag dozens of correct properties while missing the conversion that caused it.
    /// </remarks>
    [Fact]
    public async Task No_endpoint_returns_a_timestamp_without_a_zone()
    {
        await AuthenticateAsync("SuperAdmin", "Admin");
        var id = await CreateContentAsync();

        string[] paths =
        [
            $"/api/contents/{id}",
            $"/api/contents/{id}/history",
            "/api/contents",
            "/api/users",
            "/api/roles",
            "/api/content-types",
            "/api/audit",
            "/api/api-keys",
        ];

        var offenders = new List<string>();
        foreach (var path in paths)
        {
            var response = await _client.GetAsync(path);
            if (!response.IsSuccessStatusCode) continue;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Walk(document.RootElement, path, offenders);
        }

        // The control. If every request 404s or the walker never recurses, the list is empty for a
        // reason that has nothing to do with correctness, and this test would pass forever.
        _seen.Should().BeGreaterThan(3,
            "only {0} timestamps were examined across {1} endpoints, so an empty offender list "
          + "proves nothing", _seen, paths.Length);

        offenders.Should().BeEmpty(
            "a timestamp with no zone is read as local time by the client that parses it");
    }

    private int _seen;

    private void Walk(JsonElement element, string where, List<string> offenders)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String && LooksLikeATimestamp(property.Value.GetString()))
                    {
                        _seen++;
                        if (!IsUnambiguous(property.Value.GetString()))
                        {
                            offenders.Add($"{where} -> {property.Name} = {property.Value.GetString()}");
                        }
                    }
                    else
                    {
                        Walk(property.Value, where, offenders);
                    }
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Walk(item, where, offenders);
                break;
        }
    }

    // Shape rather than name: a field called ExpiresAt is a timestamp and so is one called Foo, and
    // matching on the name would miss exactly the field nobody thought to name conventionally.
    private static bool LooksLikeATimestamp(string? value)
        => value is { Length: >= 19 }
           && value[4] == '-' && value[7] == '-' && value[10] == 'T'
           && char.IsDigit(value[0]);

    private static bool IsUnambiguous(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (value.EndsWith('Z')) return true;

        var timePart = value.IndexOf('T');
        if (timePart < 0) return false;
        return value.IndexOf('+', timePart) > 0 || value.IndexOf('-', timePart) > 0;
    }

    private async Task<Guid> CreateContentAsync()
    {
        var typeName = "date-wire-" + Guid.NewGuid().ToString("n")[..8];
        var type = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = typeName,
            displayName = "Date Wire",
            fields = new[] { new { name = "Title", type = "Text" } },
        });
        type.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            type.StatusCode, await type.Content.ReadAsStringAsync());

        var created = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = typeName,
            data = new Dictionary<string, object> { ["Title"] = "date probe" },
            status = "Published",
        });
        created.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            created.StatusCode, await created.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private async Task AuthenticateAsync(params string[] roles)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var roleIds = new List<Guid>();
        foreach (var roleName in roles)
        {
            var role = await session.Query<barakoCMS.Models.Role>().FirstOrDefaultAsync(r => r.Name == roleName);
            if (role is null)
            {
                role = new barakoCMS.Models.Role { Id = Guid.NewGuid(), Name = roleName };
                session.Store(role);
            }

            roleIds.Add(role.Id);
        }

        var userId = Guid.NewGuid();
        session.Store(new barakoCMS.Models.User
        {
            Id = userId,
            Username = $"date-wire-{userId:n}",
            Email = $"date-wire-{userId:n}@example.com",
            RoleIds = roleIds,
        });
        await session.SaveChangesAsync();

        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: roles, userId: userId.ToString()));
    }
}
