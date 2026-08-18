using barakoCMS.Models;
using barakoCMS.Modules;
using Marten;
using Xunit;
using FluentAssertions;

namespace BarakoCMS.Tests;

/// <summary>
/// A module may configure its own document types and nothing else.
///
/// Before this, <c>ConfigureMarten(StoreOptions)</c> handed a module the same store options core
/// configured, so it could re-map <c>Content</c>, change tenancy or index <c>mt_doc_contents</c>.
/// Nothing detected it and the first symptom would be a schema that no longer matched what core
/// expected.
/// </summary>
public class ModuleSchemaOwnershipTests
{
    /// <summary>A document type belonging to this test assembly, standing in for a module's own.</summary>
    private sealed class OwnDocument
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class Probe : IBarakoModule
    {
        public string Name => "Probe";
    }

    private static ModuleSchema SchemaFor(IBarakoModule module) =>
        new(new StoreOptions(), module);

    /// <summary>
    /// The modules this test project references, found rather than listed. A hardcoded list goes
    /// stale the moment someone adds a module and forgets to add it here, which is exactly the
    /// case this suite exists to catch.
    /// </summary>
    private static IReadOnlyList<IBarakoModule> ReferencedModules()
    {
        var builder = new BarakoModuleBuilder();
        builder.DiscoverFrom(
            typeof(BarakoCMS.Accounting.AccountingModule).Assembly,
            typeof(BarakoCMS.AI.AiModule).Assembly,
            typeof(BarakoCMS.Diagnostics.DiagnosticsModule).Assembly,
            typeof(BarakoCMS.Pwa.PwaModule).Assembly,
            typeof(BarakoCMS.Files.S3.S3FilesModule).Assembly);
        return builder.Modules;
    }


    [Fact]
    public void A_module_may_configure_a_type_from_its_own_assembly()
    {
        // Probe and OwnDocument both live in this assembly, so this is the owned case.
        var act = () => SchemaFor(new Probe()).For<OwnDocument>().Index(x => x.Name);
        act.Should().NotThrow();
    }

    [Fact]
    public void A_module_may_not_configure_a_core_document()
    {
        // Content is core's. A module reaching it could change how every tenant's content is stored.
        var act = () => SchemaFor(new Probe()).For<Content>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Probe*")
            .WithMessage("*Content*")
            .WithMessage("*own assemblies*");
    }

    [Fact]
    public void The_failure_names_the_module_the_type_and_where_it_came_from()
    {
        // The person reading this is usually not the person who wrote the module.
        var ex = Record.Exception(() => SchemaFor(new Probe()).For<User>());

        ex.Should().BeOfType<InvalidOperationException>();
        ex!.Message.Should().Contain("Probe").And.Contain("User").And.Contain("barakoCMS");
    }

    [Fact]
    public void A_module_may_not_configure_another_modules_document()
    {
        // A type from a module assembly, reached from a different module.
        var otherModulesType = new BarakoCMS.Diagnostics.DiagnosticsModule().GetType().Assembly
            .GetTypes().First(t => t.Name == "ClientError");

        var method = typeof(ModuleSchema).GetMethod(nameof(ModuleSchema.For))!
            .MakeGenericMethod(otherModulesType);

        var act = () => method.Invoke(SchemaFor(new Probe()), null);
        act.Should().Throw<System.Reflection.TargetInvocationException>()
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*Diagnostics*");
    }

    [Fact]
    public void Every_first_party_module_only_configures_types_it_owns()
    {
        // The regression guard for the whole suite: if someone adds a module that reaches into
        // core, this fails rather than the schema quietly changing.
        var modules = ReferencedModules();

        modules.Should().NotBeEmpty("discovery must actually find the referenced modules");

        foreach (var module in modules)
        {
            var act = () => module.ConfigureSchema(SchemaFor(module));
            act.Should().NotThrow($"{module.Name} must only configure document types it ships");
        }
    }

    [Fact]
    public void No_first_party_module_still_uses_the_deprecated_hook()
    {
        // ConfigureMarten stays for one major so third-party modules keep working. Ours should not
        // be relying on it, and this is what notices if one starts.
        var modules = ReferencedModules();

        foreach (var module in modules)
        {
            barakoCMS.Extensions.ServiceCollectionExtensions
                .OverridesConfigureMarten(module)
                .Should().BeFalse($"{module.Name} should have moved to ConfigureSchema");
        }
    }
}
