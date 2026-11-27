using FluentAssertions;
using Marten;
using Marten.Services;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using static BarakoCMS.Tests.Features.Connectors.QueryKit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// A parameter is a value. Whatever it holds, it matches the entries that hold exactly that and
/// nothing else, and the run sends one statement.
/// </summary>
[Collection("Integration1")]
public class QueryParameterInjectionTests(IntegrationTestFixture factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Hostile() => new()
    {
        "' or 1=1 --",
        "x'; drop table mt_doc_content; --",
        "100%",
        "a_b",
        "%",
        "_",
        "\\",
        "\\%",
        "{\"Title\":\"x\",\"$or\":[{\"a\":1}]}",
        "{{data.Secret}}",
        new string('x', 10_000),
    };

    [Theory]
    [MemberData(nameof(Hostile))]
    public async Task A_parameter_matches_only_the_entries_that_hold_exactly_that_value(string hostile)
    {
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        var tenant = Tenant();
        var type = TypeName();
        var exact = Guid.NewGuid();
        var decoys = new[]
        {
            "other", "x", "100", "100 percent", "aXb", "ab", "' or 1=1", "1", hostile + "tail", "head" + hostile, hostile.ToUpperInvariant() + "!",
        };
        await SeedAsync(store, tenant, Type(type, Field("Title")),
            decoys.Select(d => Entry(type, new() { ["Title"] = d }))
                .Append(Entry(type, new() { ["Title"] = hostile }, id: exact)), Ct);

        var (result, rowStatements) = await RunAsync(store, tenant, type, hostile);
        var (_, plainStatements) = await RunAsync(store, tenant, type, "plain");

        result.Ok.Should().BeTrue(result.Refusal);
        result.Ids.Should().ContainSingle("only the entry that holds exactly this value").Which.Should().Be(exact);
        rowStatements.Should().ContainSingle("a value never starts another statement");
        rowStatements[0].Should().Be(plainStatements[0], "the statement is the same whatever the value is: it is bound, not written in");

        await using var check = store.QuerySession(tenant);
        (await check.Query<Content>().CountAsync(c => c.ContentType == type, Ct)).Should().Be(decoys.Length + 1);
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("\\")]
    [InlineData("50%")]
    public async Task A_contains_parameter_keeps_percent_underscore_and_backslash_literal(string needle)
    {
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        var tenant = Tenant();
        var type = TypeName();
        var holds = Entry(type, new() { ["Title"] = "big " + needle + " sale" });
        await SeedAsync(store, tenant, Type(type, Field("Title")),
            [holds, Entry(type, new() { ["Title"] = "big sale" }), Entry(type, new() { ["Title"] = "aXb" }), Entry(type, new() { ["Title"] = "a\\b" })], Ct);

        await using var session = store.QuerySession(tenant);
        var result = await ((IContextQueryRunner)new QueryRunner(session)).RunAsync(
            new QueryDefinition
            {
                ContentType = type, Fields = ["Title"], Limit = 100,
                Filters = [WhereParam("Title", "contains", "data.Needle")],
            },
            new QueryRunContext(Values(("data.Needle", needle))), Ct);

        result.Ok.Should().BeTrue(result.Refusal);
        result.Ids.Should().Contain(holds.Id);
        result.Rows.Should().OnlyContain(r => r["Title"].ToString()!.Contains(needle), "the wildcards are not wildcards");
    }

    [Fact]
    public async Task A_filter_with_no_value_to_bind_refuses_and_never_matches_the_empty_value()
    {
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        var tenant = Tenant();
        var type = TypeName();
        await SeedAsync(store, tenant, Type(type, Field("Title")), [Entry(type, new() { ["Title"] = "" })], Ct);

        await using var session = store.QuerySession(tenant);
        var runner = new QueryRunner(session);
        var definition = new QueryDefinition
        {
            ContentType = type, Fields = ["Title"], Filters = [WhereParam("Title", "eq", "data.Title")],
        };

        (await runner.RunAsync(definition, Ct)).Refusal.Should().Contain("parameter");
        (await ((IContextQueryRunner)runner).RunAsync(definition, new QueryRunContext(null), Ct)).Refusal.Should().Contain("@data.Title");
        (await ((IContextQueryRunner)runner).RunAsync(definition, new QueryRunContext(Values(("data.Title", ""))), Ct)).Refusal.Should().Contain("empty");
    }

    [Fact]
    public async Task A_parameter_that_cannot_be_the_fields_type_refuses_without_repeating_it()
    {
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        var tenant = Tenant();
        var type = TypeName();
        await SeedAsync(store, tenant, Type(type, Field("Amount", "decimal"), Field("Owner", "reference"), Field("Flag", "bool")), [], Ct);

        await using var session = store.QuerySession(tenant);
        var runner = (IContextQueryRunner)new QueryRunner(session);
        const string secret = "not-a-value-9f3";

        foreach (var field in new[] { "Amount", "Owner", "Flag" })
        {
            var result = await runner.RunAsync(
                new QueryDefinition { ContentType = type, Fields = [field], Filters = [WhereParam(field, "eq", "data.V")] },
                new QueryRunContext(Values(("data.V", secret))), Ct);

            result.Ok.Should().BeFalse(field);
            result.Refusal.Should().NotContain(secret, "a message never repeats the value").And.Contain("@data.V");
        }
    }

    [Fact]
    public async Task A_literal_that_starts_with_an_at_sign_is_still_a_literal()
    {
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        var tenant = Tenant();
        var type = TypeName();
        var hit = Entry(type, new() { ["Title"] = "@data.Title" });
        await SeedAsync(store, tenant, Type(type, Field("Title")), [hit, Entry(type, new() { ["Title"] = "x" })], Ct);

        await using var session = store.QuerySession(tenant);
        var result = await new QueryRunner(session).RunAsync(
            new QueryDefinition { ContentType = type, Fields = ["Title"], Filters = [Where("Title", "eq", "@data.Title")] }, Ct);

        result.Ids.Should().Equal(hit.Id);
    }

    private static async Task<(QueryResult Result, List<string> RowStatements)> RunAsync(
        IDocumentStore store, string tenant, string type, string value)
    {
        await using var session = store.QuerySession(tenant);
        var commands = new Recording();
        session.Logger = commands;

        var result = await ((IContextQueryRunner)new QueryRunner(session)).RunAsync(
            new QueryDefinition
            {
                ContentType = type, Fields = ["Title"], Limit = 100,
                Filters = [WhereParam("Title", "eq", "data.Title")],
            },
            new QueryRunContext(Values(("data.Title", value))), Ct);

        return (result, commands.OnContent);
    }

    private sealed class Recording : IMartenSessionLogger
    {
        private readonly List<string> _text = [];

        /// <summary>The statements that read entries. The runner also reads the type's definition, which holds no value.</summary>
        public List<string> OnContent => [.. _text.Where(t => System.Text.RegularExpressions.Regex.IsMatch(t, @"mt_doc_content\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))];

        public void LogSuccess(NpgsqlCommand command) => _text.Add(command.CommandText);
        public void LogFailure(NpgsqlCommand command, Exception ex) { }
        public void LogSuccess(NpgsqlBatch batch) => _text.AddRange(batch.BatchCommands.Cast<NpgsqlBatchCommand>().Select(c => c.CommandText));
        public void LogFailure(NpgsqlBatch batch, Exception ex) { }
        public void LogFailure(Exception ex, string message) { }
        public void RecordSavedChanges(IDocumentSession session, IChangeSet commit) { }
        public void OnBeforeExecute(NpgsqlCommand command) { }
        public void OnBeforeExecute(NpgsqlBatch batch) { }
    }
}
