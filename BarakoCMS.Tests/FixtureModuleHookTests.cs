using System.Net;
using System.Net.Http.Json;
using BarakoCMS.Testing.Fixture;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The fixture module's content hook through <c>/api/contents</c>: a core write path proven with a
/// module that is not a shipped domain.
/// </summary>
[Collection("Integration4")]
public class FixtureModuleHookTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly HttpClient _client;

    public FixtureModuleHookTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task An_entry_with_a_negative_amount_is_rejected_by_the_fixture_hook()
    {
        await AuthenticateAsync();
        await EnsureContentTypeAsync();

        var response = await _client.PostAsJsonAsync("/api/contents", Entry(-5), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain(FixtureAmountHook.NegativeAmountMessage);
    }

    [Fact]
    public async Task An_entry_with_a_valid_amount_is_stored_so_the_rejection_above_is_the_hook()
    {
        await AuthenticateAsync();
        await EnsureContentTypeAsync();

        var response = await _client.PostAsJsonAsync("/api/contents", Entry(5), TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static object Entry(int amount) => new
    {
        contentType = FixtureAmountHook.ContentTypeName,
        data = new Dictionary<string, object> { ["Amount"] = amount },
    };

    private static readonly SemaphoreSlim TypeGate = new(1, 1);
    private static bool _typeCreated;

    private async Task EnsureContentTypeAsync()
    {
        await TypeGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            if (_typeCreated) return;

            var response = await _client.PostAsJsonAsync("/api/content-types", new
            {
                name = FixtureAmountHook.ContentTypeName,
                displayName = "Fixture Entry",
                fields = new[] { new { name = "Amount", type = "Number" } },
            }, TestContext.Current.CancellationToken);

            response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
                response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            _typeCreated = true;
        }
        finally
        {
            TypeGate.Release();
        }
    }

    private async Task AuthenticateAsync()
    {
        string[] roles = ["SuperAdmin", "Admin"];

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
            Username = $"fixture-hook-{userId:n}",
            Email = $"fixture-hook-{userId:n}@example.com",
            RoleIds = roleIds,
        });
        await session.SaveChangesAsync();

        _client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: roles, userId: userId.ToString()));
    }
}
