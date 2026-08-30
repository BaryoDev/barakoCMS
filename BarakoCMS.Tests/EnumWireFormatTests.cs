using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Enums cross the wire as names, and are still stored as numbers.
/// </summary>
/// <remarks>
/// An int enum renumbers every client the moment a member is inserted, which is why this is a
/// change only a major can make. The admin had the numbering transcribed into its own source to
/// cope with it.
///
/// The storage half matters as much as the wire half. Documents are stored with Status as a number,
/// and <c>mt_doc_contents_idx_status</c> indexes <c>((data -&gt; &gt; 'Status')::integer)</c>, so
/// putting names in JSONB breaks the index cast and every LINQ query that filters on status. Adding
/// the converter to Marten's serializer instead of the HTTP one would have looked identical from
/// the outside and quietly broken the database, so both halves are asserted here.
/// </remarks>
[Collection("Sequential")]
public class EnumWireFormatTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public EnumWireFormatTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task A_status_comes_back_as_a_name_not_a_number()
    {
        await AuthenticateAsync("SuperAdmin", "Admin");
        var id = await CreateContentAsync();

        var response = await _client.GetAsync($"/api/contents/{id}");
        response.IsSuccessStatusCode.Should().BeTrue();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var status = document.RootElement.GetProperty("status");
        status.ValueKind.Should().Be(JsonValueKind.String,
            "an int enum renumbers every client when a member is inserted");
        status.GetString().Should().Be("Published");
    }

    /// <summary>
    /// A request naming its enum by number still works.
    /// </summary>
    /// <remarks>
    /// The break is on the way out, not on the way in: JsonStringEnumConverter reads numbers as well
    /// as names, so a caller written against 3.x keeps working when it posts. Without this test the
    /// converter could later be configured to reject numbers and nothing would notice.
    /// </remarks>
    [Fact]
    public async Task A_request_may_still_name_its_enum_by_number()
    {
        await AuthenticateAsync("SuperAdmin", "Admin");
        var typeName = await CreateContentTypeAsync();

        var response = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = typeName,
            data = new Dictionary<string, object> { ["Title"] = "numeric status" },
            status = 1,
        });

        response.IsSuccessStatusCode.Should().BeTrue("a 3.x client posting status as a number must still work");
    }

    /// <summary>
    /// The stored document still holds a number, so the status index keeps working.
    /// </summary>
    [Fact]
    public async Task The_stored_document_still_holds_a_number()
    {
        await AuthenticateAsync("SuperAdmin", "Admin");
        var id = await CreateContentAsync();

        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "select data ->> 'Status' from public.mt_doc_contents where id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        var stored = (string?)await command.ExecuteScalarAsync();

        stored.Should().Be("1",
            "mt_doc_contents_idx_status indexes ((data ->> 'Status')::integer), so a name here "
            + "breaks the index cast and every LINQ query that filters on status");
    }

    private async Task<string> CreateContentTypeAsync()
    {
        var typeName = "enum-wire-" + Guid.NewGuid().ToString("n")[..8];
        var response = await _client.PostAsJsonAsync("/api/content-types", new
        {
            name = typeName,
            displayName = "Enum Wire",
            fields = new[] { new { name = "Title", type = "Text" } },
        });
        response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            response.StatusCode, await response.Content.ReadAsStringAsync());
        return typeName;
    }

    private async Task<Guid> CreateContentAsync()
    {
        var typeName = await CreateContentTypeAsync();
        var created = await _client.PostAsJsonAsync("/api/contents", new
        {
            contentType = typeName,
            data = new Dictionary<string, object> { ["Title"] = "enum probe" },
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
            Username = $"enum-wire-{userId:n}",
            Email = $"enum-wire-{userId:n}@example.com",
            RoleIds = roleIds,
        });
        await session.SaveChangesAsync();

        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: roles, userId: userId.ToString()));
    }
}
