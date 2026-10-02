using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Features.Public;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentApi;

/// <summary>
/// <c>filter[field][op]=value</c> on <c>GET /api/contents</c>: it narrows the list, it is refused on a
/// field the caller cannot read, and it does not match an entry whose data the caller cannot read.
/// </summary>
/// <remarks>
/// Every narrowing assertion sits beside the unfiltered list returning more, because an endpoint
/// that ignored the parameter would otherwise pass anything that only looks at what came back.
/// </remarks>
[Collection("Sequential")]
public class EntriesListFieldFilterTests
{
    private const string Pin = "4821";

    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public EntriesListFieldFilterTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // Name and Stage are Public, Salary is Sensitive (view_sensitive), Pin is Hidden (view_hidden).
    // The seeded SuperAdmin role reads both.
    private async Task<string> SeedTypeAsync()
    {
        var type = "ff" + Guid.NewGuid().ToString("N")[..10];
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        using var session = store.LightweightSession();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = "Field filter probe",
            Fields = new List<FieldDefinition>
            {
                new() { Name = "Name", Type = "string", Sensitivity = SensitivityLevel.Public },
                new()
                {
                    Name = "Stage", Type = "choice", Sensitivity = SensitivityLevel.Public,
                    Options = new List<FieldOption>
                    {
                        new() { Value = "open", Label = "Open" },
                        new() { Value = "won", Label = "Won" },
                    },
                },
                new() { Name = "Salary", Type = "number", Sensitivity = SensitivityLevel.Sensitive },
                new() { Name = "Pin", Type = "string", Sensitivity = SensitivityLevel.Hidden },
            },
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return type;
    }

    private async Task<Guid> SeedEntryAsync(
        string type, string name, string stage, double salary,
        SensitivityLevel level = SensitivityLevel.Public, string pin = "0000")
    {
        var ids = await SeedEntriesAsync(type, 1, name, stage, salary, level, pin);
        return ids[0];
    }

    private async Task<List<Guid>> SeedEntriesAsync(
        string type, int count, string name, string stage, double salary,
        SensitivityLevel level = SensitivityLevel.Public, string pin = "0000")
    {
        var ids = new List<Guid>();
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        using var session = store.LightweightSession();
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            session.Store(new Content
            {
                Id = id,
                ContentType = type,
                Sensitivity = level,
                Data = new Dictionary<string, object>
                {
                    ["Name"] = name,
                    ["Stage"] = stage,
                    ["Salary"] = salary,
                    ["Pin"] = pin,
                },
                CreatedAt = DateTime.UtcNow,
            });
        }

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return ids;
    }

    // A stored user whose database role grants read on the type. What they may see is decided by
    // what is stored: "SuperAdmin" also gives them the seeded SuperAdmin role, a capability name
    // puts that capability on their role, and anything else leaves the role with none. The role's
    // name is random, and the token claims the names in tokenRoles, or the role's own when none
    // are given. A name in the token decides nothing.
    private async Task<string> ReaderAsync(string access, string type, params string[] tokenRoles)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        using var session = store.LightweightSession();

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"dbrole_{Guid.NewGuid():N}",
            SystemCapabilities = SystemCapabilities.IsKnown(access) ? new List<string> { access } : new List<string>(),
            Permissions = new List<ContentTypePermission>
            {
                new()
                {
                    ContentTypeSlug = type,
                    Read = new PermissionRule { Enabled = true },
                    Create = new PermissionRule { Enabled = false },
                    Update = new PermissionRule { Enabled = false },
                    Delete = new PermissionRule { Enabled = false },
                },
            },
        };
        session.Store(role);
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"user_{Guid.NewGuid()}",
            Email = $"{Guid.NewGuid()}@example.com",
            RoleIds = access == "SuperAdmin"
                ? new List<Guid> { role.Id, SystemRoles.SuperAdminRoleId }
                : new List<Guid> { role.Id },
        };
        session.Store(user);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return _factory.CreateToken(tokenRoles.Length == 0 ? new[] { role.Name } : tokenRoles, user.Id.ToString());
    }

    private Task<string> ViewerAsync(string type) => ReaderAsync($"Viewer_{Guid.NewGuid():N}", type);

    private async Task<HttpResponseMessage> SendAsync(string token, string query)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/contents?" + query);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<(List<Guid> Ids, int Total, int PageSize)> ListAsync(string token, string query)
    {
        var response = await SendAsync(token, query);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        var root = JsonDocument.Parse(body).RootElement;
        return (
            root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList(),
            root.GetProperty("totalItems").GetInt32(),
            root.GetProperty("pageSize").GetInt32());
    }

    // The error body carries the request's trace id, which differs on every request.
    private static string WithoutTraceId(string body)
    {
        var fields = JsonDocument.Parse(body).RootElement.EnumerateObject()
            .Where(p => p.Name != "traceId")
            .Select(p => $"{p.Name}={p.Value.GetRawText()}");
        return string.Join("\n", fields);
    }

    private async Task<string> RefusedAsync(string token, string query)
    {
        var response = await SendAsync(token, query);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        return body;
    }

    [Fact]
    public async Task A_choice_filter_returns_the_entries_holding_the_option_and_leaves_the_others_out()
    {
        var type = await SeedTypeAsync();
        var token = await ViewerAsync(type);

        // The two differ only in the filtered field.
        var open = await SeedEntryAsync(type, "same", "open", 100);
        var won = await SeedEntryAsync(type, "same", "won", 100);

        var everything = await ListAsync(token, $"contentType={type}&pageSize=100");
        everything.Ids.Should().HaveCount(2);
        everything.Ids.Should().BeEquivalentTo(new[] { open, won },
            "with no filter both come back, or the lines below prove nothing about the filter");

        var onlyWon = await ListAsync(token, $"contentType={type}&pageSize=100&filter[Stage][eq]=won");
        onlyWon.Ids.Should().HaveCount(1);
        onlyWon.Ids.Should().Equal(won);
        onlyWon.Total.Should().Be(1, "the total counts what the filter left, not the type");

        var notWon = await ListAsync(token, $"contentType={type}&pageSize=100&filter[Stage][ne]=won");
        notWon.Ids.Should().HaveCount(1);
        notWon.Ids.Should().Equal(open);
    }

    [Fact]
    public async Task A_filter_on_a_field_the_caller_cannot_read_is_refused()
    {
        var type = await SeedTypeAsync();
        var viewer = await ViewerAsync(type);
        await SeedEntryAsync(type, "Ana", "open", 60000, pin: Pin);

        var sensitive = await RefusedAsync(viewer, $"contentType={type}&filter[Salary][gte]=50000");
        var hidden = await RefusedAsync(viewer, $"contentType={type}&filter[Pin][eq]={Pin}");
        var unknown = await RefusedAsync(viewer, $"contentType={type}&filter[Nope][eq]=1");

        sensitive.Should().NotContain("Stage", "the refusal does not list the type's fields");
        WithoutTraceId(hidden).Replace("Pin", "X").Should().Be(WithoutTraceId(unknown).Replace("Nope", "X"),
            "a field the caller cannot read answers exactly as one that does not exist");
    }

    [Fact]
    public async Task A_caller_who_reads_the_field_filters_on_it()
    {
        var type = await SeedTypeAsync();
        var high = await SeedEntryAsync(type, "Ana", "open", 60000, pin: Pin);
        var low = await SeedEntryAsync(type, "Ben", "open", 20000);

        var nurse = await ReaderAsync(SystemCapabilities.ViewSensitive, type);
        var auditor = await ReaderAsync(SystemCapabilities.ViewHidden, type);
        var superAdmin = await ReaderAsync("SuperAdmin", type);

        var all = await ListAsync(nurse, $"contentType={type}&pageSize=100");
        all.Ids.Should().HaveCount(2);
        all.Ids.Should().BeEquivalentTo(new[] { high, low });

        var bySalary = await ListAsync(nurse, $"contentType={type}&pageSize=100&filter[Salary][gte]=50000");
        bySalary.Ids.Should().HaveCount(1);
        bySalary.Ids.Should().Equal(high);

        await RefusedAsync(nurse, $"contentType={type}&filter[Pin][eq]={Pin}");

        var byPin = await ListAsync(superAdmin, $"contentType={type}&pageSize=100&filter[Pin][eq]={Pin}");
        byPin.Ids.Should().HaveCount(1);
        byPin.Ids.Should().Equal(high);

        var auditorByPin = await ListAsync(auditor, $"contentType={type}&pageSize=100&filter[Pin][eq]={Pin}");
        auditorByPin.Ids.Should().HaveCount(1, "view_hidden is what a Hidden field asks for");
        auditorByPin.Ids.Should().Equal(high);
        await RefusedAsync(auditor, $"contentType={type}&filter[Salary][gte]=50000");
    }

    /// <summary>
    /// The names the old rule read from the token. A caller whose stored role holds no capability
    /// and whose token claims both is refused the filters and still has withheld entries dropped.
    /// </summary>
    [Fact]
    public async Task A_role_name_in_the_token_opens_no_filter_and_matches_no_withheld_entry()
    {
        var type = await SeedTypeAsync();
        var claimant = await ReaderAsync("Viewer", type, "HR", "SuperAdmin");
        var open = await SeedEntryAsync(type, "Ana", "open", 60000, pin: Pin);
        await SeedEntryAsync(type, "Ana", "open", 60000, SensitivityLevel.Sensitive);
        await SeedEntryAsync(type, "Ana", "open", 60000, SensitivityLevel.Hidden);

        await RefusedAsync(claimant, $"contentType={type}&filter[Salary][gte]=50000");
        await RefusedAsync(claimant, $"contentType={type}&filter[Pin][eq]={Pin}");

        var unfiltered = await ListAsync(claimant, $"contentType={type}&pageSize=100");
        unfiltered.Ids.Should().HaveCount(3, "the control: this caller lists the type");

        var filtered = await ListAsync(claimant, $"contentType={type}&pageSize=100&filter[Name][eq]=Ana");
        filtered.Ids.Should().HaveCount(1);
        filtered.Ids.Should().Equal(open);
    }

    [Fact]
    public async Task A_filter_does_not_match_an_entry_whose_data_the_caller_cannot_read()
    {
        var type = await SeedTypeAsync();
        var viewer = await ViewerAsync(type);
        var superAdmin = await ReaderAsync("SuperAdmin", type);

        var open = await SeedEntryAsync(type, "Ana", "open", 100);
        var sensitive = await SeedEntryAsync(type, "Ana", "open", 100, SensitivityLevel.Sensitive);
        var hidden = await SeedEntryAsync(type, "Ana", "open", 100, SensitivityLevel.Hidden);

        var unfiltered = await ListAsync(viewer, $"contentType={type}&pageSize=100");
        unfiltered.Ids.Should().HaveCount(3);
        unfiltered.Ids.Should().BeEquivalentTo(new[] { open, sensitive, hidden },
            "the unfiltered list still returns the two withheld entries, blanked");

        var filtered = await ListAsync(viewer, $"contentType={type}&pageSize=100&filter[Name][eq]=Ana");
        filtered.Ids.Should().HaveCount(1);
        filtered.Ids.Should().Equal(open);
        filtered.Total.Should().Be(1,
            "a withheld entry coming back for Name = Ana says what its Name is");

        var forSuperAdmin = await ListAsync(superAdmin, $"contentType={type}&pageSize=100&filter[Name][eq]=Ana");
        forSuperAdmin.Ids.Should().HaveCount(3);
        forSuperAdmin.Ids.Should().BeEquivalentTo(new[] { open, sensitive, hidden },
            "a caller who reads all three matches all three");
    }

    /// <summary>
    /// Two fields that differ only by case, one withheld and one readable. Creating a content type
    /// does not refuse that, and the SQL lookup ignores the key's case, so a filter on the readable
    /// name would match the value stored under the withheld one.
    /// </summary>
    [Fact]
    public async Task A_filter_naming_the_readable_twin_of_a_withheld_field_is_refused()
    {
        var type = "ff" + Guid.NewGuid().ToString("N")[..10];
        Guid entry;
        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
            using var session = store.LightweightSession();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = "Case twins",
                Fields = new List<FieldDefinition>
                {
                    new() { Name = "Name", Type = "string", Sensitivity = SensitivityLevel.Public },
                    new() { Name = "Salary", Type = "number", Sensitivity = SensitivityLevel.Sensitive },
                    new() { Name = "SalarY", Type = "number", Sensitivity = SensitivityLevel.Public },
                },
            });

            // The value sits under the withheld spelling only.
            entry = Guid.NewGuid();
            session.Store(new Content
            {
                Id = entry,
                ContentType = type,
                Sensitivity = SensitivityLevel.Public,
                Data = new Dictionary<string, object> { ["Name"] = "Ana", ["Salary"] = 60000d },
                CreatedAt = DateTime.UtcNow,
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var viewer = await ViewerAsync(type);
        var superAdmin = await ReaderAsync("SuperAdmin", type);

        var unfiltered = await ListAsync(viewer, $"contentType={type}&pageSize=100");
        unfiltered.Ids.Should().HaveCount(1);
        unfiltered.Ids.Should().Equal(entry);

        await RefusedAsync(viewer, $"contentType={type}&filter[SalarY][gte]=50000");
        await RefusedAsync(viewer, $"contentType={type}&filter[Salary][gte]=50000");

        var byName = await ListAsync(viewer, $"contentType={type}&pageSize=100&filter[Name][eq]=Ana");
        byName.Ids.Should().HaveCount(1, "a field with no withheld twin still filters on this type");

        var forSuperAdmin = await ListAsync(superAdmin, $"contentType={type}&pageSize=100&filter[SalarY][gte]=50000");
        forSuperAdmin.Ids.Should().HaveCount(1, "a caller who reads both spellings filters on either");
        forSuperAdmin.Ids.Should().Equal(entry);
    }

    [Fact]
    public async Task A_filter_without_a_content_type_or_on_a_type_with_no_definition_is_refused()
    {
        var type = await SeedTypeAsync();
        var viewer = await ViewerAsync(type);

        await RefusedAsync(viewer, "filter[Name][eq]=Ana");

        var undefined = "ff" + Guid.NewGuid().ToString("N")[..10];
        var undefinedViewer = await ViewerAsync(undefined);
        await SeedEntryAsync(undefined, "Ana", "open", 100);

        (await ListAsync(undefinedViewer, $"contentType={undefined}&pageSize=100")).Ids.Should().HaveCount(1,
            "the type lists without a filter, so the refusal below is about the filter");
        await RefusedAsync(undefinedViewer, $"contentType={undefined}&filter[Name][eq]=Ana");
    }

    [Theory]
    [InlineData("' or 1=1 --")]
    [InlineData("Ana' OR '1'='1")]
    public async Task A_value_carrying_sql_is_compared_and_matches_nothing(string payload)
    {
        var type = await SeedTypeAsync();
        var viewer = await ViewerAsync(type);
        var ana = await SeedEntryAsync(type, "Ana", "open", 100);
        await SeedEntryAsync(type, "Ben", "open", 100);

        var hostile = await ListAsync(viewer,
            $"contentType={type}&pageSize=100&filter[Name][eq]={Uri.EscapeDataString(payload)}");
        hostile.Ids.Should().BeEmpty("the value is data, so it matches no name");
        hostile.Total.Should().Be(0);

        var honest = await ListAsync(viewer, $"contentType={type}&pageSize=100&filter[Name][eq]=Ana");
        honest.Ids.Should().HaveCount(1);
        honest.Ids.Should().Equal(new[] { ana },
            "a real value on the same field still matches, so the empty result above means something");
    }

    [Fact]
    public async Task Too_many_filters_or_too_long_a_value_is_refused_and_not_dropped()
    {
        var type = await SeedTypeAsync();
        var viewer = await ViewerAsync(type);
        var ana = await SeedEntryAsync(type, "Ana", "open", 100);

        string Repeat(int times) => string.Join('&', Enumerable.Repeat("filter[Name][eq]=Ana", times));

        var atCap = await ListAsync(viewer, $"contentType={type}&pageSize=100&{Repeat(DeliveryQuery.MaxFilters)}");
        atCap.Ids.Should().HaveCount(1);
        atCap.Ids.Should().Equal(ana);

        await RefusedAsync(viewer, $"contentType={type}&{Repeat(DeliveryQuery.MaxFilters + 1)}");
        await RefusedAsync(viewer,
            $"contentType={type}&filter[Name][eq]={new string('a', DeliveryQuery.MaxValueLength + 1)}");
    }

    [Fact]
    public async Task The_page_size_stays_capped_with_a_filter()
    {
        var type = await SeedTypeAsync();
        var viewer = await ViewerAsync(type);

        var won = await SeedEntriesAsync(type, PaginatedRequest.MaxPageSize + 1, "same", "won", 100);
        await SeedEntryAsync(type, "same", "open", 100);

        var page = await ListAsync(viewer, $"contentType={type}&pageSize=500&filter[Stage][eq]=won");

        page.PageSize.Should().Be(PaginatedRequest.MaxPageSize);
        page.Ids.Should().HaveCount(PaginatedRequest.MaxPageSize);
        page.Ids.Should().BeSubsetOf(won);
        page.Total.Should().Be(PaginatedRequest.MaxPageSize + 1,
            "one more entry matches than a page holds, and the open one is not counted");
    }
}
