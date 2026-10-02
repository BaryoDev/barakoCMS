using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// What the startup notice reads for one tenant: the types with a rule a write applies, and every
/// stored rule a write skips. Stored straight into the database, because no endpoint accepts the
/// unusable ones any more.
/// </summary>
[Collection("Sequential")]
public class StoredValidationRulesNoticeTests
{
    private readonly IntegrationTestFixture _fixture;

    public StoredValidationRulesNoticeTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FieldDefinition Field(string name, string type, params (string Rule, object Value)[] rules) =>
        new()
        {
            Name = name,
            DisplayName = name,
            Type = type,
            ValidationRules = rules.ToDictionary(r => r.Rule, r => r.Value),
        };

    private static ContentTypeDefinition Type(string name, params FieldDefinition[] fields) =>
        new() { Id = Guid.NewGuid(), Name = name, DisplayName = name, Fields = fields.ToList() };

    [Fact]
    public async Task Each_stored_rule_is_listed_by_what_a_write_does_with_it_and_only_for_its_own_tenant()
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var tag = Guid.NewGuid().ToString("n")[..8];
        var first = $"notice-a-{tag}";
        var second = $"notice-b-{tag}";

        var valid = $"valid{tag}";
        var unusable = $"unusable{tag}";
        var mixed = $"mixed{tag}";
        var contrary = $"contrary{tag}";
        var plain = $"plain{tag}";
        var elsewhere = $"elsewhere{tag}";

        await using (var session = store.LightweightSession(first))
        {
            session.Store(Type(valid, Field("Grade", "int", ("max", 100))));
            session.Store(Type(unusable, Field("Code", "string", ("matches", "^[A-Z]+$"))));
            session.Store(Type(mixed, Field("Code", "string", ("maxLength", 12), ("matches", "^[A-Z]+$"))));
            session.Store(Type(contrary, Field("Grade", "int", ("min", 10), ("max", 5))));
            session.Store(Type(plain, Field("Title", "string")));
            await session.SaveChangesAsync(Ct);
        }

        await using (var session = store.LightweightSession(second))
        {
            session.Store(Type(elsewhere, Field("Grade", "int", ("min", 1))));
            await session.SaveChangesAsync(Ct);
        }

        await using (var read = store.QuerySession(first))
        {
            var (applied, skipped) = await StoredValidationRulesNotice.ReadAsync(read, Ct);

            applied.Should().HaveCount(2);
            applied.Should().BeEquivalentTo(new[] { valid, mixed });

            skipped.Should().HaveCount(4);
            skipped.Should().BeEquivalentTo(new[]
            {
                $"{unusable}.Code.matches",
                $"{mixed}.Code.matches",
                $"{contrary}.Grade.min",
                $"{contrary}.Grade.max",
            });
        }

        await using (var read = store.QuerySession(second))
        {
            var (applied, skipped) = await StoredValidationRulesNotice.ReadAsync(read, Ct);

            applied.Should().HaveCount(1);
            applied.Should().BeEquivalentTo(new[] { elsewhere });
            skipped.Should().BeEmpty();
        }
    }
}
