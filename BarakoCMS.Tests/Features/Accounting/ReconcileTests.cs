using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using BarakoCMS.Accounting;
using BarakoCMS.Accounting.Domain;
using BarakoCMS.Tests.Features.Config;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Accounting;

/// <summary>The reconcile report lists the subjects the books do not agree with.</summary>
[Collection("Integration1")]
public class ReconcileTests(IntegrationTestFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ConfigCallers _callers = new(fixture);

    private async Task<string> MappingAsync()
    {
        var cash = await AccountingTestData.AccountAsync(fixture, "1010", AccountType.Asset);
        var income = await AccountingTestData.AccountAsync(fixture, "4010", AccountType.Income);
        var name = $"map-{Guid.NewGuid():N}"[..14];
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new AccountMapping
        {
            Name = name,
            Rule = new MappingRule([new(cash, "debit", "{{Data.Total}}"), new(income, "credit", "{{Data.Total}}")], true, null),
        });
        await session.SaveChangesAsync(Ct);
        return name;
    }

    private async Task<Content> SubjectAsync(string type, decimal total, DateTime createdAt, string number)
    {
        var subject = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            CreatedAt = createdAt,
            Data = new Dictionary<string, object> { ["Total"] = total, ["Number"] = number },
        };
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(subject);
        await session.SaveChangesAsync(Ct);
        return subject;
    }

    private async Task<WorkflowActionResult> RunAsync<TAction>(Content subject, Dictionary<string, string> parameters)
        where TAction : class, IWorkflowAction
    {
        parameters["Date"] = "2026-06-05";
        parameters["RunId"] = Guid.NewGuid().ToString();
        using var scope = fixture.Services.CreateScope();
        IWorkflowAction action = ActivatorUtilities.CreateInstance<TAction>(scope.ServiceProvider);
        return await action.RunAsync(new WorkflowContext { Parameters = parameters, Content = subject, TriggerEvent = "Updated" }, Ct);
    }

    private async Task PostAsync(Content subject, string mapping, decimal? amount = null)
    {
        var parameters = new Dictionary<string, string> { ["Mapping"] = mapping, ["ActionKey"] = "receipt" };
        if (amount is { } a)
            parameters["Amount"] = a.ToString(System.Globalization.CultureInfo.InvariantCulture);
        (await RunAsync<PostJournalAction>(subject, parameters)).Succeeded.Should().BeTrue();
    }

    private static string Url(string type, string from = "2026-06-01", string to = "2026-06-30") =>
        $"/api/accounting/reconcile?contentType={type}&from={from}&to={to}";

    private static DateTime June(int day) => new(2026, 6, day, 12, 0, 0, DateTimeKind.Utc);

    private static string NewType() => "rcpt" + Guid.NewGuid().ToString("N")[..10];

    [Fact]
    public async Task Receipts_in_range_are_checked_and_the_ones_that_differ_or_were_never_posted_are_listed()
    {
        var type = NewType();
        var mapping = await MappingAsync();
        var good = await SubjectAsync(type, 100m, June(3), "R-1");
        var wrong = await SubjectAsync(type, 200m, June(4), "R-2");
        var never = await SubjectAsync(type, 300m, June(5), "R-3");
        var outside = await SubjectAsync(type, 400m, new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc), "R-4");
        await PostAsync(good, mapping);
        await PostAsync(wrong, await AmountMappingAsync(), amount: 150m);
        var client = await _callers.SuperAdminAsync(null);

        var response = await client.GetAsync(Url(type), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("checked").GetInt32().Should().Be(3);
        var mismatch = body.GetProperty("mismatches").EnumerateArray().Single();
        mismatch.GetProperty("subjectId").GetGuid().Should().Be(wrong.Id);
        mismatch.GetProperty("subjectNumber").GetString().Should().Be("R-2");
        mismatch.GetProperty("subjectAmount").GetDecimal().Should().Be(200m);
        mismatch.GetProperty("journalAmount").GetDecimal().Should().Be(150m);
        mismatch.GetProperty("problem").GetString().Should().Be("Amount differs.");
        var missing = body.GetProperty("missing").EnumerateArray().Single();
        missing.GetProperty("subjectId").GetGuid().Should().Be(never.Id);
        missing.GetProperty("problem").GetString().Should().Be("No posting.");
        body.ToString().Should().NotContain(outside.Id.ToString());
    }

    [Fact]
    public async Task A_voided_and_reversed_posting_is_matched()
    {
        var type = NewType();
        var mapping = await MappingAsync();
        var subject = await SubjectAsync(type, 80m, June(8), "R-9");
        await PostAsync(subject, mapping);
        (await RunAsync<ReverseAction>(subject, new() { ["ActionKey"] = "receipt", ["Reason"] = "cancelled" })).Succeeded.Should().BeTrue();
        var client = await _callers.SuperAdminAsync(null);

        var body = await (await client.GetAsync(Url(type), Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        body.GetProperty("checked").GetInt32().Should().Be(1);
        body.GetProperty("mismatches").GetArrayLength().Should().Be(0);
        body.GetProperty("missing").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_caller_without_view_ledger_gets_a_403()
    {
        var role = await _callers.RoleAsync("manage_config");
        var client = await _callers.ClientAsync(null, [role]);

        (await client.GetAsync(Url(NewType()), Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_empty_content_type_is_a_400()
    {
        var client = await _callers.SuperAdminAsync(null);

        (await client.GetAsync(Url(string.Empty), Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_range_over_366_days_is_a_400()
    {
        var client = await _callers.SuperAdminAsync(null);

        (await client.GetAsync(Url(NewType(), "2025-01-01", "2026-06-30"), Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync(Url(NewType(), "2026-06-30", "2026-06-01"), Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_total_that_is_not_public_is_a_400()
    {
        var type = NewType();
        using (var scope = fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = type,
                Fields = [new FieldDefinition { Name = "Total", DisplayName = "Total", Type = "decimal", Sensitivity = SensitivityLevel.Sensitive }],
            });
            await session.SaveChangesAsync(Ct);
        }

        var client = await _callers.SuperAdminAsync(null);

        (await client.GetAsync(Url(type), Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>A member of the default tenant who may view the ledger, holds the given branches, and reads the types given.</summary>
    private async Task<HttpClient> MemberAsync(string[] readable, params Guid[] branches)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Ledger_{Guid.NewGuid():n}",
            SystemCapabilities = [AccountingCapabilities.ViewLedger],
            Permissions = [.. readable.Select(t => new ContentTypePermission { ContentTypeSlug = t, Read = new PermissionRule { Enabled = true } })],
        };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"ledger_{Guid.NewGuid():n}",
            Email = $"ledger_{Guid.NewGuid():n}@example.com",
            RoleIds = [role.Id],
        };
        session.Store(role);
        session.Store(user);
        session.Store(new Membership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TenantSlug = barakoCMS.Models.Tenant.DefaultSlug,
            Status = MembershipStatus.Active,
            RoleIds = [role.Id],
            Profile = new Dictionary<string, string> { ["branches"] = string.Join(",", branches) },
        });
        await session.SaveChangesAsync(Ct);

        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", fixture.CreateToken(roles: [role.Name], userId: user.Id.ToString()));
        return client;
    }

    private async Task<(string Type, Content InA, Content InB)> BranchedSubjectsAsync(Guid a, Guid b)
    {
        var type = NewType();
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            BranchScoped = true,
            Fields =
            [
                new FieldDefinition { Name = "Total", DisplayName = "Total", Type = "decimal" },
                new FieldDefinition { Name = "Number", DisplayName = "Number", Type = "string" },
            ],
        });
        Content Make(Guid branch, string number) => new()
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            BranchId = branch,
            CreatedAt = June(3),
            Data = new Dictionary<string, object> { ["Total"] = 10m, ["Number"] = number },
        };
        var inA = Make(a, "A-1");
        var inB = Make(b, "B-1");
        session.Store(inA, inB);
        await session.SaveChangesAsync(Ct);
        return (type, inA, inB);
    }

    [Fact]
    public async Task A_caller_in_one_branch_does_not_see_the_rows_of_another()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var (type, inA, inB) = await BranchedSubjectsAsync(a, b);
        var client = await MemberAsync([type], a);

        var body = await (await client.GetAsync(Url(type), Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        body.GetProperty("checked").GetInt32().Should().Be(1);
        body.GetProperty("missing").EnumerateArray().Select(r => r.GetProperty("subjectId").GetGuid()).Should().Equal(inA.Id);
        body.ToString().Should().NotContain(inB.Id.ToString());
    }

    [Fact]
    public async Task Rows_the_caller_cannot_read_are_left_out()
    {
        var a = Guid.NewGuid();
        var (type, _, _) = await BranchedSubjectsAsync(a, Guid.NewGuid());
        var client = await MemberAsync([], a);

        var response = await client.GetAsync(Url(type), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("checked").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task A_reversed_key_settles_only_itself_when_no_action_key_is_given()
    {
        var type = NewType();
        var receipt = await MappingAsync();
        var fee = await AmountMappingAsync();
        var subject = await SubjectAsync(type, 100m, June(10), "R-20");
        await PostAsync(subject, receipt);
        (await RunAsync<PostJournalAction>(subject, new() { ["Mapping"] = fee, ["ActionKey"] = "fee", ["Amount"] = "40" })).Succeeded.Should().BeTrue();
        (await RunAsync<ReverseAction>(subject, new() { ["ActionKey"] = "receipt", ["Reason"] = "cancelled" })).Succeeded.Should().BeTrue();
        var client = await _callers.SuperAdminAsync(null);

        var body = await (await client.GetAsync(Url(type), Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);

        var mismatch = body.GetProperty("mismatches").EnumerateArray().Should().ContainSingle().Subject;
        mismatch.GetProperty("subjectId").GetGuid().Should().Be(subject.Id);
        mismatch.GetProperty("journalAmount").GetDecimal().Should().Be(40m);
    }

    private async Task<string> AmountMappingAsync()
    {
        var cash = await AccountingTestData.AccountAsync(fixture, "1010", AccountType.Asset);
        var income = await AccountingTestData.AccountAsync(fixture, "4010", AccountType.Income);
        var name = $"map-{Guid.NewGuid():N}"[..14];
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new AccountMapping
        {
            Name = name,
            Rule = new MappingRule([new(cash, "debit", "{{Amount}}"), new(income, "credit", "{{Amount}}")], true, null),
        });
        await session.SaveChangesAsync(Ct);
        return name;
    }
}
