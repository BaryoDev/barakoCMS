using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using static BarakoCMS.Tests.Features.Connectors.QueryKit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>select may follow one reference, read as the user this run reads as may read it.</summary>
[Collection("Integration1")]
public class QueryReferenceSelectTests(IntegrationTestFixture factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FieldDefinition RefField(string name, string target, bool multiple = false) =>
        new() { Name = name, DisplayName = name, Type = "reference", ReferenceType = target, Multiple = multiple };

    private sealed record Setup(
        IServiceScope Scope, string Lines, string Products, Guid Product, Guid Line, Guid Orphan, Guid Wrong);

    private async Task<Setup> SeedAsync(SensitivityLevel priceLevel = SensitivityLevel.Public)
    {
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        var lines = TypeName();
        var products = TypeName();
        var other = TypeName();

        var product = Entry(products, new() { ["Name"] = "Kettle", ["Price"] = 20m });
        var notAProduct = Entry(other, new() { ["Name"] = "Not a product" });
        var line = Entry(lines, new() { ["Product"] = product.Id.ToString(), ["Qty"] = 2m }, Day(1));
        var orphan = Entry(lines, new() { ["Product"] = Guid.NewGuid().ToString(), ["Qty"] = 3m }, Day(2));
        var wrong = Entry(lines, new() { ["Product"] = notAProduct.Id.ToString(), ["Qty"] = 4m }, Day(3));

        await QueryKit.SeedAsync(store, null, Type(products, Field("Name"), Field("Price", "decimal", priceLevel)), [product], Ct);
        await QueryKit.SeedAsync(store, null, Type(other, Field("Name")), [notAProduct], Ct);
        await QueryKit.SeedAsync(
            store, null,
            Type(lines, RefField("Product", products), Field("Qty", "decimal"), RefField("Many", products, multiple: true)),
            [line, orphan, wrong], Ct);

        return new Setup(factory.Services.CreateScope(), lines, products, product.Id, line.Id, orphan.Id, wrong.Id);
    }

    private static DateTime Day(int n) => new(2026, 4, n, 9, 0, 0, DateTimeKind.Utc);

    private static QueryDefinition Select(string type, params string[] fields) =>
        new() { ContentType = type, Fields = [.. fields], SortField = "createdAt" };

    [Fact]
    public async Task Select_follows_one_reference_and_leaves_out_what_is_not_there()
    {
        var s = await SeedAsync();
        using var _ = s.Scope;
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        var actor = await ActorAsync(store, Ct, (s.Lines, null), (s.Products, null));

        var result = await s.Scope.ServiceProvider.GetRequiredService<QueryRunner>()
            .RunAsync(Select(s.Lines, "Qty", "Product.Name", "Product.Price"), AsActor(actor), Ct);

        result.Ok.Should().BeTrue(result.Refusal);
        result.Ids.Should().Equal(s.Line, s.Orphan, s.Wrong);
        result.Rows[0]["Product.Name"].Should().Be("Kettle");
        result.Rows[0]["Product.Price"].Should().NotBeNull();
        result.Rows[1].Should().NotContainKey("Product.Name", "a reference to nothing has no value");
        result.Rows[2].Should().NotContainKey("Product.Name", "an entry of another type is not what the field points at");
    }

    [Fact]
    public async Task A_target_type_the_user_may_not_read_refuses_the_run()
    {
        var s = await SeedAsync();
        using var _ = s.Scope;
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        var actor = await ActorAsync(store, Ct, (s.Lines, null));

        var result = await s.Scope.ServiceProvider.GetRequiredService<QueryRunner>()
            .RunAsync(Select(s.Lines, "Qty", "Product.Name"), AsActor(actor), Ct);

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Contain("Product.Name");
    }

    [Fact]
    public async Task A_target_field_the_user_may_not_read_refuses_the_run()
    {
        var s = await SeedAsync(SensitivityLevel.Sensitive);
        using var _ = s.Scope;
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        var actor = await ActorAsync(store, Ct, (s.Lines, null), (s.Products, null));

        var runner = s.Scope.ServiceProvider.GetRequiredService<QueryRunner>();
        (await runner.RunAsync(Select(s.Lines, "Product.Price"), AsActor(actor), Ct)).Ok.Should().BeFalse();
        (await runner.RunAsync(Select(s.Lines, "Product.Name"), AsActor(actor), Ct)).Ok.Should().BeTrue();
    }

    [Theory]
    [InlineData("Many.Name")]
    [InlineData("Qty.Name")]
    [InlineData("Product.Nope")]
    [InlineData("Product.Name.Extra")]
    [InlineData("Product.")]
    [InlineData(".Name")]
    [InlineData("Product.id")]
    public async Task A_dotted_name_that_is_not_one_single_reference_and_a_field_is_refused(string name)
    {
        var s = await SeedAsync();
        using var _ = s.Scope;
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        var actor = await ActorAsync(store, Ct, (s.Lines, null), (s.Products, null));

        var result = await s.Scope.ServiceProvider.GetRequiredService<QueryRunner>()
            .RunAsync(Select(s.Lines, name), AsActor(actor), Ct);

        result.Ok.Should().BeFalse();
    }

    [Fact]
    public async Task With_no_user_a_followed_field_is_refused_and_the_older_runs_never_follow()
    {
        var s = await SeedAsync();
        using var _ = s.Scope;
        var runner = s.Scope.ServiceProvider.GetRequiredService<QueryRunner>();

        (await runner.RunAsync(Select(s.Lines, "Product.Name"), AsActor(Guid.Empty), Ct)).Refusal.Should().Contain("needs a user");
        (await runner.RunAsync(Select(s.Lines, "Product.Name"), Ct)).Ok.Should().BeFalse();
        (await ((IContextQueryRunner)runner).RunAsync(Select(s.Lines, "Product.Name"), new QueryRunContext(null), Ct)).Ok.Should().BeFalse();
    }
}
