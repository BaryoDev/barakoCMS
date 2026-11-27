using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using static BarakoCMS.Tests.Features.Connectors.QueryKit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>A run that reads as a user reads what that user may read, by the services every read uses.</summary>
[Collection("Integration1")]
public class QueryPermissionTests(IntegrationTestFixture factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(string Type, IServiceScope Scope, Guid Entry)> SeedAsync()
    {
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        var type = TypeName();
        var entry = Entry(type, new() { ["Name"] = "Ana", ["Email"] = "ana@example.com", ["Secret"] = "s3", ["Owned"] = "o" });
        await QueryKit.SeedAsync(
            store, null,
            Type(type, Field("Name"), Field("Email"), Field("Secret", "text", SensitivityLevel.Sensitive),
                new FieldDefinition { Name = "Owned", DisplayName = "Owned", Type = "text", ShownWhenListedIn = "Shown" }),
            [entry], Ct);
        return (type, factory.Services.CreateScope(), entry.Id);
    }

    private static QueryDefinition Select(string type, params string[] fields) =>
        new() { ContentType = type, Fields = [.. fields], Limit = 5 };

    [Fact]
    public async Task A_user_who_reads_the_fields_gets_them()
    {
        var (type, scope, id) = await SeedAsync();
        using var _ = scope;
        var actor = await ActorAsync(factory.Services.GetRequiredService<IDocumentStore>(), Ct, (type, null));

        var result = await scope.ServiceProvider.GetRequiredService<QueryRunner>()
            .RunAsync(Select(type, "Name", "Email"), AsActor(actor), Ct);

        result.Ok.Should().BeTrue(result.Refusal);
        result.Ids.Should().Equal(id);
        result.Rows[0]["Email"].Should().Be("ana@example.com");
    }

    [Fact]
    public async Task An_author_who_may_not_read_a_field_cannot_save_a_query_that_names_it()
    {
        var (type, scope, _) = await SeedAsync();
        using var _s = scope;
        var author = await ActorAsync(factory.Services.GetRequiredService<IDocumentStore>(), Ct, (type, ["Name"]));

        var refusal = await ((IContextQueryRunner)scope.ServiceProvider.GetRequiredService<QueryRunner>())
            .ValidateAsync(Select(type, "Name", "Email"), AsActor(author), Ct);

        refusal.Should().Contain("Email");
    }

    [Fact]
    public async Task A_filter_or_a_sort_on_a_field_the_user_may_not_read_is_refused_too()
    {
        var (type, scope, _) = await SeedAsync();
        using var _s = scope;
        var actor = await ActorAsync(factory.Services.GetRequiredService<IDocumentStore>(), Ct, (type, ["Name"]));
        var runner = scope.ServiceProvider.GetRequiredService<QueryRunner>();

        var filtered = Select(type, "Name");
        filtered.Filters = [Where("Email", "eq", "ana@example.com")];
        (await runner.RunAsync(filtered, AsActor(actor), Ct)).Ok.Should().BeFalse("which entries match would tell the value");

        var sorted = Select(type, "Name");
        sorted.SortField = "Email";
        (await runner.RunAsync(sorted, AsActor(actor), Ct)).Ok.Should().BeFalse();
    }

    [Fact]
    public async Task Rights_narrowed_after_the_save_fail_the_run_and_the_message_names_the_field()
    {
        var (type, scope, _) = await SeedAsync();
        using var _s = scope;
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        var definition = Select(type, "Name", "Email");

        var wide = await ActorAsync(store, Ct, (type, null));
        var runner = scope.ServiceProvider.GetRequiredService<QueryRunner>();
        (await runner.RunAsync(definition, AsActor(wide), Ct)).Ok.Should().BeTrue();

        // The same query, run later as somebody whose rights were cut down to one field.
        using var later = factory.Services.CreateScope();
        var narrow = await ActorAsync(store, Ct, (type, ["Name"]));
        var result = await later.ServiceProvider.GetRequiredService<QueryRunner>().RunAsync(definition, AsActor(narrow), Ct);

        result.Ok.Should().BeFalse("a step that goes on without a field it asked for is working from less");
        result.Refusal.Should().Contain("Email");
        result.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_user_with_no_read_rule_on_the_type_is_refused()
    {
        var (type, scope, _) = await SeedAsync();
        using var _s = scope;
        var actor = await ActorAsync(factory.Services.GetRequiredService<IDocumentStore>(), Ct, (TypeName(), null));

        var result = await scope.ServiceProvider.GetRequiredService<QueryRunner>()
            .RunAsync(Select(type, "Name"), AsActor(actor), Ct);

        result.Ok.Should().BeFalse();
    }

    [Fact]
    public async Task A_user_that_no_longer_exists_is_refused()
    {
        var (type, scope, _) = await SeedAsync();
        using var _s = scope;

        var result = await scope.ServiceProvider.GetRequiredService<QueryRunner>()
            .RunAsync(Select(type, "Name"), AsActor(Guid.NewGuid()), Ct);

        result.Refusal.Should().Contain("no longer exists");
    }

    [Fact]
    public async Task No_user_reads_Public_fields_only()
    {
        var (type, scope, id) = await SeedAsync();
        using var _s = scope;
        var runner = scope.ServiceProvider.GetRequiredService<QueryRunner>();

        var open = await runner.RunAsync(Select(type, "Name", "Email"), AsActor(Guid.Empty), Ct);
        open.Ok.Should().BeTrue(open.Refusal);
        open.Ids.Should().Equal(id);

        (await runner.RunAsync(Select(type, "Secret"), AsActor(Guid.Empty), Ct)).Ok.Should().BeFalse();
    }

    [Fact]
    public async Task An_owner_hidden_field_is_refused_even_for_a_user_who_may_read_the_type()
    {
        var (type, scope, _) = await SeedAsync();
        using var _s = scope;
        var actor = await ActorAsync(factory.Services.GetRequiredService<IDocumentStore>(), Ct, (type, null));
        var runner = scope.ServiceProvider.GetRequiredService<QueryRunner>();

        (await runner.RunAsync(Select(type, "Owned"), AsActor(actor), Ct)).Ok.Should().BeFalse();
        (await runner.RunAsync(Select(type, "Owned"), AsActor(Guid.Empty), Ct)).Ok.Should().BeFalse();
    }

    [Fact]
    public async Task A_sensitive_field_is_refused_for_a_user_without_the_capability()
    {
        var (type, scope, _) = await SeedAsync();
        using var _s = scope;
        var actor = await ActorAsync(factory.Services.GetRequiredService<IDocumentStore>(), Ct, (type, null));

        var result = await scope.ServiceProvider.GetRequiredService<QueryRunner>()
            .RunAsync(Select(type, "Secret"), AsActor(actor), Ct);

        result.Ok.Should().BeFalse();
        result.Refusal.Should().NotContain("s3");
    }
}
