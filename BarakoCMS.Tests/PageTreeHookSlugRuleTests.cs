using BarakoCMS.Pages;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// A top-level page cannot take a reserved slug, whatever the sensitivity of the slug field. The
/// check on write reads the slug by the authoring rule, not by delivery's Public-only one.
/// </summary>
[Collection("Sequential")]
public class PageTreeHookSlugRuleTests
{
    private readonly IntegrationTestFixture _factory;

    public PageTreeHookSlugRuleTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(SensitivityLevel.Public)]
    [InlineData(SensitivityLevel.Sensitive)]
    public async Task A_reserved_root_slug_is_refused_whatever_the_slug_field_sensitivity(SensitivityLevel sensitivity)
    {
        var type = "treeslug" + Guid.NewGuid().ToString("n")[..10];
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = type, DisplayName = type, IsPubliclyDeliverable = true,
            Fields =
            [
                new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
                new FieldDefinition { Name = "Slug", DisplayName = "Slug", Type = "slug", Sensitivity = sensitivity },
                new FieldDefinition { Name = "Parent", DisplayName = "Parent", Type = "reference", ReferenceType = type },
            ],
        });
        await session.SaveChangesAsync(Ct);

        var hook = new PageTreeHook(
            Options.Create(new PagesOptions { ContentType = type, ParentField = "Parent", ReservedSlugs = ["shop"] }),
            scope.ServiceProvider.GetRequiredService<IPublicContentProjector>());

        var errors = await hook.OnBeforeSaveAsync(new ContentLifecycleContext
        {
            ContentType = type,
            Data = new Dictionary<string, object> { ["Title"] = "Shop", ["Slug"] = "shop" },
            Session = session,
            UserId = Guid.NewGuid(),
        }, Ct);

        errors.Should().HaveCount(1);
        errors[0].Should().Contain("'shop' is reserved");
    }
}
