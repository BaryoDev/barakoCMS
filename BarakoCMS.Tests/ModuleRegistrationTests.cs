using barakoCMS.Modules;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// What <see cref="BarakoModuleBuilder"/> does when a host registers the same module twice.
///
/// It used to keep the first and drop the second without a word, so a host that deliberately
/// registered two configured instances got one of them and no explanation. The duplicate-name
/// check in <c>ModuleOrder</c> already throws for the same class of mistake, and this now matches
/// it: a repeat is a configuration error, not a preference.
/// </summary>
public class ModuleRegistrationTests
{
    private sealed class Alpha : IBarakoModule
    {
        public string Name => "Alpha";
    }

    private sealed class Beta : IBarakoModule
    {
        public string Name => "Beta";
    }

    [Fact]
    public void Registering_the_same_module_class_twice_is_refused_by_type()
    {
        var builder = new BarakoModuleBuilder();
        builder.Add(new Alpha());

        var act = () => builder.Add(new Alpha());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Alpha*")
            .WithMessage("*already registered*");
    }

    [Fact]
    public void The_refusal_leaves_the_first_registration_alone()
    {
        // Deduplicating was the right half of the old behaviour. One module class, one instance,
        // still holds; only the silence is gone.
        var builder = new BarakoModuleBuilder();
        var first = new Alpha();
        builder.Add(first);

        try { builder.Add(new Alpha()); }
        catch (InvalidOperationException) { /* asserted above; here we only care what survived */ }

        builder.Modules.Should().ContainSingle().Which.Should().BeSameAs(first);
    }

    [Fact]
    public void Different_module_classes_both_register_in_the_order_given()
    {
        var builder = new BarakoModuleBuilder();
        builder.Add(new Alpha()).Add(new Beta());

        builder.Modules.Select(m => m.Name).Should().Equal("Alpha", "Beta");
    }

    [Fact]
    public void Discovery_skips_a_class_the_host_already_added_rather_than_refusing_it()
    {
        // Discovery is a sweep, not a statement of intent. Adding a module by hand and then
        // scanning the assembly it lives in is a normal combination, so it must not throw.
        var builder = new BarakoModuleBuilder();
        var added = new BarakoCMS.Files.FilesModule();
        builder.Add(added);

        var act = () => builder.DiscoverFrom(typeof(BarakoCMS.Files.FilesModule).Assembly);

        act.Should().NotThrow();
        builder.Modules.Should().ContainSingle(m => m is BarakoCMS.Files.FilesModule)
            .Which.Should().BeSameAs(added, "the explicit instance is the configured one");
    }

    [Fact]
    public void Scanning_one_assembly_twice_registers_each_module_once()
    {
        var builder = new BarakoModuleBuilder();
        var assembly = typeof(BarakoCMS.Files.FilesModule).Assembly;

        builder.DiscoverFrom(assembly, assembly);

        builder.Modules.Should().NotBeEmpty();
        builder.Modules.Select(m => m.GetType()).Should().OnlyHaveUniqueItems();
    }
}
