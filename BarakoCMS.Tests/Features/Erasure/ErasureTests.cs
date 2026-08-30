using System.Net;
using barakoCMS.Infrastructure.Erasure;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests.Features.Erasure;

/// <summary>
/// Erasure removes the bytes, and a mode that cannot do what its name says is refused at startup.
/// </summary>
/// <remarks>
/// DECISIONS.md D9. The guard is the part that matters most: crypto-shredding cannot be applied to
/// events already written in plaintext, so an operator who selects it and is allowed to start has
/// the belief without the property, and only finds out in a regulator's letter.
/// </remarks>
[Collection("Sequential")]
public class ErasureTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public ErasureTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>
    /// The payload is gone from the tables, not hidden from the API.
    /// </summary>
    /// <remarks>
    /// Asserted against <c>mt_events</c> directly. Reading it back through Marten would pass on an
    /// archived stream, where every byte is still on disk, and that difference is the whole subject.
    /// </remarks>
    [Fact]
    public async Task Erasing_content_removes_its_events_its_stream_and_its_document()
    {
        await AuthenticateAsync("SuperAdmin");
        var needle = $"erasure-{Guid.NewGuid():n}";
        var id = await SeedAsync(needle);

        (await RowsContainingAsync(needle)).Should().BeGreaterThan(0,
            "the probe has to be in the table first, or the assertion below proves nothing");

        var response = await _client.DeleteAsync($"/api/contents/{id}/erase");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await RowsContainingAsync(needle)).Should().Be(0, "the event payload has to go");
        (await ScalarAsync("select count(*) from public.mt_streams where id = @p", id))
            .Should().Be(0, "the stream row goes too, or the id stays discoverable");
        (await ScalarAsync("select count(*) from public.mt_doc_contents where id = @p", id))
            .Should().Be(0, "the read model holds the same data");
    }

    /// <summary>
    /// Erasing one item leaves the others alone.
    /// </summary>
    /// <remarks>
    /// The positive control. A delete with a broken predicate would satisfy the test above
    /// completely, and would be the worst possible defect in this particular feature.
    /// </remarks>
    [Fact]
    public async Task Erasing_one_item_does_not_touch_another()
    {
        await AuthenticateAsync("SuperAdmin");
        var survivorNeedle = $"survivor-{Guid.NewGuid():n}";
        var survivor = await SeedAsync(survivorNeedle);
        var doomed = await SeedAsync($"doomed-{Guid.NewGuid():n}");

        (await _client.DeleteAsync($"/api/contents/{doomed}/erase")).StatusCode
            .Should().Be(HttpStatusCode.NoContent);

        (await RowsContainingAsync(survivorNeedle)).Should().BeGreaterThan(0);
        (await ScalarAsync("select count(*) from public.mt_doc_contents where id = @p", survivor))
            .Should().Be(1);
    }

    /// <summary>
    /// The audit entry records that an erasure happened and nothing about what was erased.
    /// </summary>
    [Fact]
    public async Task The_audit_entry_records_the_erasure_without_the_erased_data()
    {
        await AuthenticateAsync("SuperAdmin");
        var needle = $"audited-{Guid.NewGuid():n}";
        var id = await SeedAsync(needle);

        (await _client.DeleteAsync($"/api/contents/{id}/erase")).StatusCode
            .Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var entry = await session.Query<barakoCMS.Models.AuditEvent>()
            .Where(a => a.Action == "content.erased" && a.TargetId == id.ToString())
            .FirstOrDefaultAsync();

        entry.Should().NotBeNull("an erasure is exactly the kind of thing an audit trail is for");
        System.Text.Json.JsonSerializer.Serialize(entry).Should().NotContain(needle,
            "an audit entry that quotes what was erased puts it back");
    }

    /// <summary>
    /// A content editor cannot erase.
    /// </summary>
    [Fact]
    public async Task Erasing_is_refused_below_SuperAdmin()
    {
        await AuthenticateAsync("SuperAdmin");
        var id = await SeedAsync($"gated-{Guid.NewGuid():n}");

        await AuthenticateAsync("Admin");
        (await _client.DeleteAsync($"/api/contents/{id}/erase")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden,
                "erasure destroys history irrecoverably, so it sits above content administration");
    }

    /// <summary>
    /// A mode that cannot do what its name says is refused, and the message says what to use instead.
    /// </summary>
    /// <remarks>
    /// Both cases are about the same failure: a setting that reads as a policy while no policy is in
    /// force. CryptoShred is refused because it is unimplemented and the subject-mapping question is
    /// open; None is refused without an acknowledgement because arriving at "no erasure" by leaving a
    /// value unset is not a decision.
    /// </remarks>
    [Theory]
    [InlineData("CryptoShred", null, "not available yet")]
    [InlineData("None", null, "AcknowledgeNoErasure")]
    [InlineData("Nonsense", null, "not a mode")]
    public void An_unusable_erasure_mode_is_refused(string mode, string? acknowledge, string expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Erasure:Mode"] = mode,
            ["Erasure:AcknowledgeNoErasure"] = acknowledge,
        }).Build();

        var act = () =>
        {
            var options = ErasureOptions.FromConfiguration(configuration);
            options.Validate();
        };

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{expected}*");
    }

    /// <summary>
    /// The positive controls for the guard: the default works, and None works once acknowledged.
    /// </summary>
    /// <remarks>
    /// Without these, a Validate that threw unconditionally would pass every case above.
    /// </remarks>
    [Theory]
    [InlineData(null, null, ErasureMode.Compact)]
    [InlineData("Compact", null, ErasureMode.Compact)]
    [InlineData("None", "true", ErasureMode.None)]
    public void A_usable_erasure_mode_is_accepted(string? mode, string? acknowledge, ErasureMode expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Erasure:Mode"] = mode,
            ["Erasure:AcknowledgeNoErasure"] = acknowledge,
        }).Build();

        var options = ErasureOptions.FromConfiguration(configuration);
        options.Validate();
        options.Mode.Should().Be(expected);
    }

    private async Task<Guid> SeedAsync(string needle)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var writer = scope.ServiceProvider.GetRequiredService<barakoCMS.Core.Interfaces.IContentWriter>();
        var id = Guid.NewGuid();

        var content = writer.Create(new barakoCMS.Events.ContentCreated(
            id, "erasure-probe", new Dictionary<string, object> { ["FullName"] = needle },
            barakoCMS.Models.ContentStatus.Draft, Guid.NewGuid(), needle,
            barakoCMS.Models.SensitivityLevel.Public));
        writer.Append(content, new barakoCMS.Events.ContentUpdated(
            id, new Dictionary<string, object> { ["FullName"] = needle }, Guid.NewGuid(), needle));

        await session.SaveChangesAsync();
        return id;
    }

    private Task<long> RowsContainingAsync(string needle) =>
        ScalarAsync("select count(*) from public.mt_events where data::text like @p", $"%{needle}%");

    private async Task<long> ScalarAsync(string sql, object parameter)
    {
        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("p", parameter);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
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
            Username = $"erasure-{userId:n}",
            Email = $"erasure-{userId:n}@example.com",
            RoleIds = roleIds,
        });
        await session.SaveChangesAsync();

        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: roles, userId: userId.ToString()));
    }
}
