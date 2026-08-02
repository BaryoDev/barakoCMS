using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Infrastructure;

/// <summary>
/// <see cref="ConditionEvaluatorTests"/> only ever calls <see cref="ConditionEvaluator.Evaluate"/>
/// with hand-built, in-memory dictionaries, so it never exercises what a
/// <see cref="PermissionRule.Conditions"/> value actually looks like once it has been through a real
/// Marten round-trip. These tests store a Role via Marten, reload it, and evaluate the Conditions
/// dictionary that comes back — the shape production code actually sees.
/// </summary>
[Collection("Sequential")]
public class ConditionEvaluatorIntegrationTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly ConditionEvaluator _evaluator = new();

    public ConditionEvaluatorIntegrationTests(IntegrationTestFixture factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Conditions_reloaded_from_marten_still_evaluate_a_matching_item_as_true()
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"role_{Guid.NewGuid():N}",
            Permissions = new List<ContentTypePermission>
            {
                new()
                {
                    ContentTypeSlug = "doc",
                    Read = new PermissionRule
                    {
                        Enabled = true,
                        Conditions = new Dictionary<string, object>
                        {
                            ["OwnerId"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" },
                        },
                    },
                },
            },
        };

        using (var write = store.LightweightSession())
        {
            write.Store(role);
            await write.SaveChangesAsync();
        }

        using var read = store.QuerySession();
        var reloaded = await read.LoadAsync<Role>(role.Id);
        reloaded.Should().NotBeNull();

        var conditions = reloaded!.Permissions[0].Read!.Conditions!;
        var user = new User { Id = Guid.NewGuid() };
        var contentData = new Dictionary<string, object> { ["OwnerId"] = user.Id.ToString() };

        _evaluator.Evaluate(conditions, contentData, user).Should().BeTrue(
            "the reloaded Conditions dictionary must evaluate the same way it would if it had never " +
            "left memory — today it comes back as a JsonElement, which the evaluator's " +
            "'is not Dictionary<string, object>' check silently treats as no-match");
    }

    [Fact]
    public async Task Conditions_reloaded_from_marten_still_evaluate_a_non_matching_item_as_false()
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"role_{Guid.NewGuid():N}",
            Permissions = new List<ContentTypePermission>
            {
                new()
                {
                    ContentTypeSlug = "doc",
                    Read = new PermissionRule
                    {
                        Enabled = true,
                        Conditions = new Dictionary<string, object>
                        {
                            ["OwnerId"] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" },
                        },
                    },
                },
            },
        };

        using (var write = store.LightweightSession())
        {
            write.Store(role);
            await write.SaveChangesAsync();
        }

        using var read = store.QuerySession();
        var reloaded = await read.LoadAsync<Role>(role.Id);
        var conditions = reloaded!.Permissions[0].Read!.Conditions!;
        var user = new User { Id = Guid.NewGuid() };
        var contentData = new Dictionary<string, object> { ["OwnerId"] = "someone-else" };

        _evaluator.Evaluate(conditions, contentData, user).Should().BeFalse(
            "a genuinely non-matching item must still be denied after the fix — this guards against " +
            "a normalization bug that accidentally makes every reloaded condition pass");
    }
}
