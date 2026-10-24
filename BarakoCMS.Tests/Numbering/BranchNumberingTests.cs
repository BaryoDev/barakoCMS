using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests.Numbering;

/// <summary>A rule with a series template counts per branch, using the code of the branch the write is for.</summary>
[Collection("Integration1")]
public class BranchNumberingTests
{
    private readonly IntegrationTestFixture _fixture;
    private readonly WebApplicationFactory<Program> _host;
    private readonly string _type = "customer" + Guid.NewGuid().ToString("n")[..8];

    // Codes of this run only, so a counter left by an earlier run never shows up here.
    private readonly string _first = "q" + Guid.NewGuid().ToString("n")[..6];
    private readonly string _second = "m" + Guid.NewGuid().ToString("n")[..6];

    public BranchNumberingTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _host = fixture.WithSettings(new Dictionary<string, string?> { ["Barako:NodeId"] = Guid.NewGuid().ToString() });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_branch_has_its_own_counter()
    {
        await StoreCustomerTypeAsync();

        (await CreateAsync(_first.ToUpperInvariant())).Should().Be("P-0001");
        (await CreateAsync(_first.ToUpperInvariant())).Should().Be("P-0002");
        (await CreateAsync(_second.ToUpperInvariant())).Should().Be("P-0001");
    }

    [Fact]
    public async Task The_middleware_loads_the_code_of_the_request_branch()
    {
        await StoreCustomerTypeAsync();
        var branchId = await StoreBranchAsync(code: _first.ToUpperInvariant());
        var client = await AdminAsync(branchId);

        var response = await PostCustomerAsync(client, branchId);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await StoredNumberAsync(response)).Should().Be("P-0001");
        (await SeriesNamesAsync()).Should().Contain($"{_first}.customer");
    }

    [Fact]
    public async Task A_branch_without_a_code_refuses_a_branch_template()
    {
        await StoreCustomerTypeAsync();
        var branchId = await StoreBranchAsync(code: null);
        var client = await AdminAsync(branchId);

        var response = await PostCustomerAsync(client, branchId);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("The numbering needs a branch with a code.");
    }

    [Fact]
    public async Task Two_branches_can_each_keep_their_own_client_allocated_number_one()
    {
        await StoreCustomerTypeAsync(clientAllocated: true);
        var first = await StoreBranchAsync(code: _first.ToUpperInvariant());
        var second = await StoreBranchAsync(code: _second.ToUpperInvariant());

        var firstNumber = await TakeAndKeepAsync(await AdminAsync(first), first, "P-0001");
        var secondNumber = await TakeAndKeepAsync(await AdminAsync(second), second, "P-0001");

        firstNumber.Should().Be("P-0001");
        secondNumber.Should().Be("P-0001");
    }

    [Fact]
    public async Task A_client_allocated_number_is_still_refused_twice_in_one_branch()
    {
        await StoreCustomerTypeAsync(clientAllocated: true);
        var branch = await StoreBranchAsync(code: _first.ToUpperInvariant());
        var client = await AdminAsync(branch);
        await TakeAndKeepAsync(client, branch, "P-0001");

        var again = await PostAsync(client, branch, "P-0001");

        again.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await again.Content.ReadAsStringAsync(Ct)).Should().Contain("That number is already issued.");
    }

    [Fact]
    public async Task A_longer_spelling_of_an_issued_number_is_refused_in_the_same_branch()
    {
        await StoreCustomerTypeAsync(clientAllocated: true);
        var branch = await StoreBranchAsync(code: _first.ToUpperInvariant());
        var client = await AdminAsync(branch);
        await TakeAndKeepAsync(client, branch, "P-0001");

        var again = await PostAsync(client, branch, "P-00001");

        again.StatusCode.Should().Be(HttpStatusCode.BadRequest, await again.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>Takes a number from the next-number route in the branch, then creates an entry that keeps it.</summary>
    private async Task<string> TakeAndKeepAsync(HttpClient client, Guid branch, string expected)
    {
        var take = new HttpRequestMessage(HttpMethod.Post, "/api/numbering/next")
        {
            Content = JsonContent.Create(new { contentType = _type }),
        };
        take.Headers.Add("X-Branch-Id", branch.ToString());
        var taken = await client.SendAsync(take, Ct);
        taken.StatusCode.Should().Be(HttpStatusCode.OK, await taken.Content.ReadAsStringAsync(Ct));

        var response = await PostAsync(client, branch, expected);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await StoredNumberAsync(response);
    }

    private Task<HttpResponseMessage> PostAsync(HttpClient client, Guid branch, string number)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/contents")
        {
            Content = JsonContent.Create(new
            {
                contentType = _type,
                data = new Dictionary<string, object> { ["Name"] = "A customer", ["CustomerNo"] = number },
            }),
        };
        request.Headers.Add("X-Branch-Id", branch.ToString());
        return client.SendAsync(request, Ct);
    }

    private async Task StoreCustomerTypeAsync(bool clientAllocated = false)
    {
        using var scope = _host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = _type,
            DisplayName = "Customer",
            Fields =
            [
                new() { Name = "Name", DisplayName = "Name", Type = "string" },
                new() { Name = "CustomerNo", DisplayName = "Customer no", Type = "string" },
            ],
            Numbering = new NumberingRule { Field = "CustomerNo", Prefix = "P-", Padding = 4, SeriesTemplate = "{branch}.customer", ClientAllocated = clientAllocated },
        });
        await session.SaveChangesAsync(Ct);
    }

    /// <summary>Stages one entry in a scope whose request branch carries the given code, and returns its number.</summary>
    private async Task<string> CreateAsync(string code)
    {
        using var scope = _host.Services.CreateScope();
        using var _ = scope.ServiceProvider.GetRequiredService<IEventEnvelopeContext>().Use(Guid.NewGuid(), code);
        var created = await scope.ServiceProvider.GetRequiredService<IContentCreator>().StageAsync(
            new ContentCreateRequest { ContentType = _type, Data = new() { ["Name"] = "A customer" } },
            Guid.NewGuid(), batch: null, Ct);
        await scope.ServiceProvider.GetRequiredService<IDocumentSession>().SaveChangesAsync(Ct);
        return created.Data["CustomerNo"].ToString()!;
    }

    private async Task<Guid> StoreBranchAsync(string? code)
    {
        using var scope = _host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        if (!await session.Query<ContentTypeDefinition>().AnyAsync(d => d.Name == "Branch", Ct))
        {
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = "Branch",
                DisplayName = "Branch",
                Fields =
                [
                    new() { Name = "Name", DisplayName = "Name", Type = "string" },
                    new() { Name = "Code", DisplayName = "Code", Type = "string" },
                ],
            });
        }

        var data = new Dictionary<string, object> { ["Name"] = "A branch" };
        if (code is not null) data["Code"] = code;
        var id = Guid.NewGuid();
        session.Store(new Content { Id = id, ContentType = "Branch", Data = data });
        await session.SaveChangesAsync(Ct);
        return id;
    }

    /// <summary>An admin whose membership profile holds the branch, as the middleware reads it.</summary>
    private async Task<HttpClient> AdminAsync(Guid branch)
    {
        using var scope = _host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var roleIds = new List<Guid>();
        foreach (var name in new[] { "SuperAdmin", "Admin" })
        {
            var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == name, Ct);
            if (role is null) { role = new Role { Id = Guid.NewGuid(), Name = name }; session.Store(role); }
            roleIds.Add(role.Id);
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"branchnum_{Guid.NewGuid():n}",
            Email = $"branchnum_{Guid.NewGuid():n}@example.com",
            RoleIds = roleIds,
        };
        session.Store(user);
        session.Store(new Membership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TenantSlug = Tenant.DefaultSlug,
            Status = MembershipStatus.Active,
            RoleIds = roleIds,
            Profile = new Dictionary<string, string> { ["branches"] = branch.ToString() },
        });
        await session.SaveChangesAsync(Ct);

        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(roles: ["SuperAdmin", "Admin"], userId: user.Id.ToString()));
        return client;
    }

    private Task<HttpResponseMessage> PostCustomerAsync(HttpClient client, Guid branch)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/contents")
        {
            Content = JsonContent.Create(new { contentType = _type, data = new Dictionary<string, object> { ["Name"] = "A customer" } }),
        };
        request.Headers.Add("X-Branch-Id", branch.ToString());
        return client.SendAsync(request, Ct);
    }

    private async Task<string> StoredNumberAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var id = doc.RootElement.GetProperty("id").GetGuid();
        using var scope = _host.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Content>(id, Ct);
        return stored!.Data["CustomerNo"].ToString()!;
    }

    private async Task<List<string>> SeriesNamesAsync()
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "select series from public.barako_number_counters where tenant_id = '*DEFAULT*'", connection);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) names.Add(reader.GetString(0));
        return names;
    }
}
