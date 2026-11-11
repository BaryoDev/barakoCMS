using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using barakoCMS.Models;
using BarakoCMS.Tests.Features.Workflows;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.WorkflowRuns;

/// <summary>
/// The title on a run is the title of its entry as the caller may read it, and nothing more: what
/// hides or masks the entry hides the title.
/// </summary>
[Collection("Integration1")]
public class WorkflowRunContentTitleTests
{
    private const string Secret = "Layoff plan for the third floor";

    private readonly IntegrationTestFixture _fixture;
    private readonly WorkflowStopHarness _harness;
    private readonly CapturingMartenLogger _logger = new();

    public WorkflowRunContentTitleTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _harness = new WorkflowStopHarness(fixture);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewType() => "title-" + Guid.NewGuid().ToString("n")[..10];

    private async Task<string> StoreTypeAsync(SensitivityLevel title = SensitivityLevel.Public, bool branchScoped = false)
    {
        var type = NewType();
        await using var session = _harness.Store.LightweightSession();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = type, DisplayName = type, BranchScoped = branchScoped,
            Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string", Sensitivity = title }],
        });
        await session.SaveChangesAsync(Ct);
        return type;
    }

    private async Task<Guid> StoreEntryAsync(
        string type, string title, SensitivityLevel level = SensitivityLevel.Public, Guid? branch = null)
    {
        var id = Guid.NewGuid();
        await using var session = _harness.Store.LightweightSession();
        session.Store(new Content
        {
            Id = id, ContentType = type, Status = ContentStatus.Published, Sensitivity = level, BranchId = branch,
            Data = new Dictionary<string, object> { ["Title"] = title },
        });
        await session.SaveChangesAsync(Ct);
        return id;
    }

    private async Task<Guid> StoreRunAsync(Guid contentId) =>
        (await _harness.SeedRunAsync(Guid.NewGuid(), contentId, [WorkflowStopHarness.Waiting()])).Id;

    private async Task<HttpClient> CallerAsync(params string[] readableTypes)
    {
        await using var session = _harness.Store.LightweightSession();
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Titles {Guid.NewGuid():N}",
            SystemCapabilities = [SystemCapabilities.ViewWorkflowRuns],
            Permissions = [.. readableTypes.Select(t =>
                new ContentTypePermission { ContentTypeSlug = t, Read = new PermissionRule { Enabled = true } })],
        };
        session.Store(role);
        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId, Username = $"titles-{userId:n}", Email = $"titles-{userId:n}@example.com", RoleIds = [role.Id],
        });
        await session.SaveChangesAsync(Ct);

        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(roles: [role.Name], userId: userId.ToString()));
        return client;
    }

    private static async Task<JsonElement> ListOneAsync(HttpClient client, Guid contentId)
    {
        var res = await client.GetAsync($"/api/workflow-runs?contentId={contentId}", TestContext.Current.CancellationToken);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("items").EnumerateArray().Should().ContainSingle().Subject.Clone();
    }

    private static string? TitleOf(JsonElement run)
    {
        run.TryGetProperty("contentTitle", out var title).Should().BeTrue("the field is always present");
        return title.ValueKind == JsonValueKind.Null ? null : title.GetString();
    }

    [Fact]
    public async Task A_readable_entry_gives_its_title()
    {
        var type = await StoreTypeAsync();
        var entry = await StoreEntryAsync(type, "Quarterly report");
        await StoreRunAsync(entry);

        TitleOf(await ListOneAsync(await CallerAsync(type), entry)).Should().Be("Quarterly report");
    }

    [Fact]
    public async Task A_type_the_caller_may_not_read_gives_no_title()
    {
        var readable = await StoreTypeAsync();
        var hidden = await StoreTypeAsync();
        var entry = await StoreEntryAsync(hidden, Secret);
        await StoreRunAsync(entry);

        var run = await ListOneAsync(await CallerAsync(readable), entry);

        TitleOf(run).Should().BeNull();
        run.GetRawText().Should().NotContain(Secret);
    }

    [Fact]
    public async Task A_title_field_the_caller_may_not_see_gives_no_title()
    {
        var type = await StoreTypeAsync(SensitivityLevel.Hidden);
        var entry = await StoreEntryAsync(type, Secret);
        await StoreRunAsync(entry);

        var run = await ListOneAsync(await CallerAsync(type), entry);

        TitleOf(run).Should().BeNull();
        run.GetRawText().Should().NotContain(Secret);
    }

    [Fact]
    public async Task A_hidden_entry_gives_no_title_to_a_caller_who_reads_its_type()
    {
        var type = await StoreTypeAsync();
        var entry = await StoreEntryAsync(type, Secret, SensitivityLevel.Hidden);
        await StoreRunAsync(entry);

        var run = await ListOneAsync(await CallerAsync(type), entry);

        TitleOf(run).Should().BeNull();
        run.GetRawText().Should().NotContain(Secret);
    }

    [Fact]
    public async Task An_entry_in_a_branch_the_caller_does_not_hold_gives_no_title()
    {
        var type = await StoreTypeAsync(branchScoped: true);
        var entry = await StoreEntryAsync(type, Secret, branch: Guid.NewGuid());
        await StoreRunAsync(entry);

        TitleOf(await ListOneAsync(await CallerAsync(type), entry)).Should().BeNull();
    }

    [Fact]
    public async Task An_erased_entry_gives_no_title()
    {
        var run = await StoreRunAsync(Guid.NewGuid());
        var admin = await _harness.AdminAsync();

        var res = await admin.GetAsync($"/api/workflow-runs/{run}", Ct);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));
        doc.RootElement.GetProperty("contentTitle").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task An_entry_with_no_title_field_gives_no_title()
    {
        var type = NewType();
        var entry = Guid.NewGuid();
        await using (var session = _harness.Store.LightweightSession())
        {
            session.Store(new Content
            {
                Id = entry, ContentType = type, Status = ContentStatus.Published,
                Data = new Dictionary<string, object> { ["Body"] = "text" },
            });
            await session.SaveChangesAsync(Ct);
        }

        await StoreRunAsync(entry);

        TitleOf(await ListOneAsync(await _harness.AdminAsync(), entry)).Should().BeNull();
    }

    [Fact]
    public async Task The_single_run_read_gives_the_same_title_and_hides_it_the_same_way()
    {
        var type = await StoreTypeAsync();
        var hiddenType = await StoreTypeAsync();
        var shown = await StoreRunAsync(await StoreEntryAsync(type, "Quarterly report"));
        var hidden = await StoreRunAsync(await StoreEntryAsync(hiddenType, Secret));
        var caller = await CallerAsync(type);

        using var shownDoc = JsonDocument.Parse(await caller.GetStringAsync($"/api/workflow-runs/{shown}", Ct));
        using var hiddenDoc = JsonDocument.Parse(await caller.GetStringAsync($"/api/workflow-runs/{hidden}", Ct));

        shownDoc.RootElement.GetProperty("contentTitle").GetString().Should().Be("Quarterly report");
        hiddenDoc.RootElement.GetProperty("contentTitle").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_page_of_fifty_runs_reads_its_entries_in_one_query()
    {
        var host = _fixture.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.ConfigureMarten(options => options.Logger(_logger))));
        var admin = host.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin", "Admin"));

        var type = await StoreTypeAsync();
        var workflow = Guid.NewGuid();
        var ids = new List<Guid>();
        for (var i = 0; i < 50; i++)
        {
            var entry = await StoreEntryAsync(type, $"Entry {i}");
            ids.Add((await _harness.SeedRunAsync(workflow, entry, [WorkflowStopHarness.Waiting()])).Id);
        }

        _logger.Commands.Clear();
        var res = await admin.GetAsync($"/api/workflow-runs?workflowId={workflow}&pageSize=50", Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var doc = JsonDocument.Parse(body);
        var items = doc.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(50);
        items.EnumerateArray().Select(i => i.GetProperty("contentTitle").GetString())
            .Should().OnlyContain(t => t != null && t.StartsWith("Entry "));

        var entryReads = _logger.Commands.Where(c => Regex.IsMatch(c.Sql, @"\bmt_doc_content\b")).ToList();
        entryReads.Should().HaveCount(1, "the entries of a page are read once, not once per run");
    }
}
