// Aliased: Microsoft.Extensions.DependencyInjection also ships a ServiceCollectionExtensions.
using Host = barakoCMS.Extensions.ServiceCollectionExtensions;
using barakoCMS.Core.Validation;
using barakoCMS.Features.Monitoring.Describe;
using barakoCMS.Features.Workflows;
using barakoCMS.Modules;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests;

/// <summary>
/// Issue #931: the describe document is a reading of the registries, so what is added to one is in
/// the document with no edit to the code that builds it.
/// </summary>
/// <remarks>
/// <see cref="FieldTypeRegistry"/> and <see cref="FieldRules"/> are static and take no registration
/// at run time, so a host cannot add a field type or a rule the way it adds a workflow action. These
/// cases hand the builder the registry's own list with one more entry, which is what the document
/// sees once an entry is added to the registry. <see cref="MetaDescribeTests"/> covers the served
/// document against the registries as they are.
/// </remarks>
public class DescribeDocumentTests
{
    [Fact]
    public void A_field_type_added_to_the_registry_list_is_described_with_no_other_edit()
    {
        FieldTypeRegistry.IsKnownType("colour").Should().BeFalse("the control: the registry has no such type today");
        FieldTypeRegistry.FieldTypeSpec[] types =
            [.. FieldTypeRegistry.Types, new FieldTypeRegistry.FieldTypeSpec("colour", "swatch", _ => true)];

        var described = DescribeDocument.FieldTypes(types, FieldRules.Names);

        described.Should().HaveCount(FieldTypeRegistry.Types.Count + 1);
        var added = described[described.Count - 1];
        added.Name.Should().Be("colour");
        added.EditorHint.Should().Be("swatch");
        added.Aliases.Should().BeEmpty();
        added.RuleNames.Should().Equal([FieldRules.RequiredWhen],
            "a type the number, date and text checks do not know takes only the rule every type takes");
    }

    [Fact]
    public void A_type_with_aliases_lists_them_and_a_type_without_lists_none()
    {
        var described = DescribeDocument.FieldTypes(FieldTypeRegistry.Types, FieldRules.Names);

        described.Should().HaveCount(FieldTypeRegistry.Types.Count);
        described.Single(t => t.Name == "int").Aliases.Should().Equal(["integer", "number"]);
        described.Single(t => t.Name == "bool").Aliases.Should().Equal(["boolean"]);
        described.Single(t => t.Name == "string").Aliases.Should().BeEmpty();
    }

    [Fact]
    public void The_rules_of_a_number_a_date_and_a_text_type_differ()
    {
        var described = DescribeDocument.FieldTypes(FieldTypeRegistry.Types, FieldRules.Names);

        described.Should().HaveCount(FieldTypeRegistry.Types.Count);
        string[] bounds = [FieldRules.Min, FieldRules.Max, FieldRules.RequiredWhen];
        described.Single(t => t.Name == "money").RuleNames.Should().Equal(bounds);
        described.Single(t => t.Name == "date").RuleNames.Should().Equal(bounds);
        described.Single(t => t.Name == "slug").RuleNames.Should().Equal(
            [FieldRules.MinLength, FieldRules.MaxLength, FieldRules.Pattern, FieldRules.RequiredWhen]);
        described.Single(t => t.Name == "geopoint").RuleNames.Should().Equal([FieldRules.RequiredWhen]);
    }

    [Fact]
    public void A_rule_added_to_the_list_of_names_is_described()
    {
        FieldRules.Names.Should().NotContain("unique", "the control: there is no such rule today");
        string[] names = [.. FieldRules.Names, "unique"];

        var described = DescribeDocument.Rules(names);

        described.Should().HaveCount(FieldRules.Names.Count + 1);
        described.Select(r => r.Name).Should().Equal(names);
        described[described.Count - 1].Aliases.Should().BeEmpty();
        described.Single(r => r.Name == FieldRules.Pattern).Aliases.Should().Equal([FieldRules.PatternAlias]);
    }

    [Fact]
    public void An_editor_hint_added_to_the_vocabulary_is_described_with_the_field_types_it_is_for()
    {
        FieldPresentation.Editors.Should().NotBeEmpty();
        FieldPresentation.Editors.Select(e => e.Name).Should().NotContain("gallery", "the control: there is no such hint today");
        FieldPresentation.Spec[] hints = [.. FieldPresentation.Editors, new FieldPresentation.Spec("gallery", ["array"])];

        var described = DescribeDocument.FieldHints(hints);

        described.Should().HaveCount(FieldPresentation.Editors.Count + 1);
        described.Select(h => h.Name).Should().Equal(hints.Select(h => h.Name));
        described[described.Count - 1].FieldTypes.Should().Equal(["array"]);
        described.Single(h => h.Name == "image").FieldTypes.Should().Equal(["url", "string"]);
    }

    [Fact]
    public void A_module_left_off_the_enabled_list_is_not_described()
    {
        var catalogue = new ModuleCatalogue(
        [
            new ModuleCatalogueEntry("Zulu", ModuleContract.Version, Enabled: true),
            new ModuleCatalogueEntry("Mike", 0, Enabled: false),
            new ModuleCatalogueEntry("Alpha", 0, Enabled: true),
        ]);

        var described = DescribeDocument.Modules(catalogue);

        described.Should().HaveCount(2);
        described.Select(m => m.Name).Should().Equal(["Alpha", "Zulu"]);
    }

    private sealed class AlphaAction : IWorkflowAction
    {
        public string Type => "AlphaProbe";

        public Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class BravoAction : IWorkflowAction
    {
        public string Type => "BravoProbe";

        public Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class Alpha : IBarakoModule
    {
        public string Name => "Alpha";

        public void ConfigureServices(IServiceCollection services, IConfiguration configuration) =>
            services.AddScoped<IWorkflowAction, AlphaAction>();
    }

    private sealed class Bravo : IBarakoModule
    {
        public string Name => "Bravo";

        public void ConfigureServices(IServiceCollection services, IConfiguration configuration) =>
            services.AddScoped<IWorkflowAction, BravoAction>();
    }

    /// <summary>
    /// The document reads workflow actions from the container and modules from the catalogue, and
    /// <c>AddBarakoCMS</c> puts a module's action in the first only when the second says it runs.
    /// </summary>
    [Fact]
    public void A_disabled_module_registers_no_workflow_action_and_is_left_out_of_the_modules()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=none",
            ["JWT:Key"] = "test-super-secret-key-that-is-at-least-32-chars-long",
            ["BarakoCMS:Modules:Enabled"] = "Alpha",
        }).Build();
        var services = new ServiceCollection();

        Host.AddBarakoCMS(services, config, m =>
        {
            m.Discover = false;
            m.Add(new Alpha());
            m.Add(new Bravo());
        });

        var actions = services
            .Where(d => d.ServiceType == typeof(IWorkflowAction))
            .Select(d => d.ImplementationType)
            .ToList();
        actions.Should().NotBeEmpty();
        actions.Should().Contain(typeof(AlphaAction));
        actions.Should().NotContain(typeof(BravoAction));

        var catalogue = (ModuleCatalogue)services.Single(d => d.ServiceType == typeof(ModuleCatalogue)).ImplementationInstance!;
        catalogue.Entries.Should().HaveCount(2, "both modules were seen");
        DescribeDocument.Modules(catalogue).Select(m => m.Name).Should().Equal(["Alpha"]);
    }
}
