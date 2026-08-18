using barakoCMS.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Host = barakoCMS.Extensions.ServiceCollectionExtensions;
using Xunit;
using FluentAssertions;

namespace BarakoCMS.Tests;

/// <summary>
/// Modules are ordered by their declared dependencies before anything runs.
///
/// Order decides who wins when two modules touch the same DI registration. It was whatever the host
/// happened to write in Program.cs, which assembly discovery cannot offer at all.
/// </summary>
public class ModuleOrderTests
{
    private sealed class Fake(string name, params string[] dependsOn) : IBarakoModule
    {
        public string Name { get; } = name;
        public IEnumerable<string> DependsOn { get; } = dependsOn;
    }

    private static string[] Order(params IBarakoModule[] modules) =>
        ModuleOrder.Sort(modules).Select(m => m.Name).ToArray();

    [Fact]
    public void A_dependency_is_configured_first_even_when_declared_last()
    {
        Order(new Fake("S3", "Files"), new Fake("Files"))
            .Should().Equal("Files", "S3");
    }

    [Fact]
    public void Independent_modules_keep_the_order_they_were_given()
    {
        // Reproducible builds: the same inputs must produce the same order every time.
        Order(new Fake("A"), new Fake("B"), new Fake("C"))
            .Should().Equal("A", "B", "C");
    }

    [Fact]
    public void Ordering_is_transitive()
    {
        Order(new Fake("C", "B"), new Fake("B", "A"), new Fake("A"))
            .Should().Equal("A", "B", "C");
    }

    [Fact]
    public void A_module_may_declare_several_dependencies()
    {
        var order = Order(new Fake("Last", "A", "B"), new Fake("A"), new Fake("B"));
        order.Should().Equal("A", "B", "Last");
    }

    [Fact]
    public void A_dependency_that_is_not_registered_is_refused_by_name()
    {
        // Silently ignoring it means the module runs in the wrong place and misbehaves later,
        // somewhere that looks unrelated.
        var act = () => Order(new Fake("S3", "Files"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*S3*").WithMessage("*Files*").WithMessage("*not registered*");
    }

    [Fact]
    public void A_cycle_is_refused_and_the_cycle_is_printed()
    {
        var act = () => Order(new Fake("A", "C"), new Fake("B", "A"), new Fake("C", "B"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*cycle*")
            .WithMessage("*A*").WithMessage("*B*").WithMessage("*C*");
    }

    [Fact]
    public void Two_modules_with_the_same_name_are_refused()
    {
        // Names are how modules refer to each other, so a duplicate makes DependsOn ambiguous.
        var act = () => Order(new Fake("Files"), new Fake("Files"));
        act.Should().Throw<InvalidOperationException>().WithMessage("*both named*");
    }

    [Fact]
    public void The_real_dependency_is_declared_and_ordered()
    {
        // S3 replaces the storage Files registers. RemoveAll only removes what is already there.
        var s3 = new BarakoCMS.Files.S3.S3FilesModule();
        s3.DependsOn.Should().Contain("Files");
    }

    /// <summary>
    /// Records the order in which the host actually configured it.
    /// </summary>
    /// <remarks>
    /// Two distinct types rather than two instances of one: BarakoModuleBuilder.Add deduplicates by
    /// type, so two instances of the same class silently collapse into one. Worth knowing when
    /// writing any test that registers more than one module.
    /// </remarks>
    private abstract class Recorder(List<string> log) : IBarakoModule
    {
        public abstract string Name { get; }

        // virtual, not shadowed in the derived class. A `new` member on a subclass does NOT satisfy
        // the interface when the base already does: the call through IBarakoModule resolves to the
        // interface's own default and the declaration is silently ignored.
        public virtual IEnumerable<string> DependsOn => Array.Empty<string>();

        public void ConfigureServices(IServiceCollection services, IConfiguration configuration) => log.Add(Name);
    }

    private sealed class FilesFake(List<string> log) : Recorder(log)
    {
        public override string Name => "Files";
    }

    private sealed class S3Fake(List<string> log) : Recorder(log)
    {
        public override string Name => "S3";
        public override IEnumerable<string> DependsOn => ["Files"];
    }

    /// <summary>
    /// The one that matters. Everything above tests the sort; this tests that the host uses it.
    ///
    /// Added after the other eight passed with the sort removed from AddBarakoCMS entirely: none of
    /// them went through the call site, so none could notice. A test that cannot fail against the
    /// bug it names is not a gate.
    /// </summary>
    [Fact]
    public void AddBarakoCMS_configures_modules_in_dependency_order()
    {
        var log = new List<string>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=db;Username=u;Password=p;Database=d",
            ["JWT:Key"] = "a-signing-key-that-is-comfortably-over-32-characters-long",
            ["JWT:Issuer"] = "test",
            ["JWT:Audience"] = "test",
        }).Build();

        // Declared dependency-last on purpose: registration order alone would run S3 first.
        Host.AddBarakoCMS(new ServiceCollection(), config, m =>
        {
            m.Add(new S3Fake(log));
            m.Add(new FilesFake(log));
        });

        // S3 replaces the storage Files registers, so it has to be configured after it.
        log.Should().Equal(["Files", "S3"]);
    }
}
