using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using barakoCMS.Core.Validation;
using barakoCMS.Features.Monitoring.Describe;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using barakoCMS.Modules;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests;

/// <summary>
/// Issue #931: <c>GET /api/meta/describe</c> answers with what the registries hold, and leaves out
/// the parts a caller could not read from the endpoint that already lists them.
/// </summary>
/// <remarks>
/// Every list is compared against the registry it was read from, after a count that would catch an
/// empty one. The shared host registers no <see cref="IBarakoModule"/> and so has an empty
/// catalogue, so the module and added-action cases run on one derived host that replaces the
/// catalogue and registers one more workflow action, the way <see cref="ModulesEndpointTests"/>
/// builds its own.
/// </remarks>
[Collection("Sequential")]
public class MetaDescribeTests
{
    private const string Route = "/api/meta/describe";

    private const string Zulu = "Zulu Describe Module";
    private const string Alpha = "Alpha Describe Module";
    private const string Mike = "Mike Describe Module";

    private readonly IntegrationTestFixture _factory;

    public MetaDescribeTests(IntegrationTestFixture factory) => _factory = factory;

    private static readonly Lock Gate = new();
    private static WebApplicationFactory<Program>? _extended;

    private WebApplicationFactory<Program> ExtendedHost()
    {
        lock (Gate)
        {
            return _extended ??= _factory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton(new ModuleCatalogue(
                    [
                        new ModuleCatalogueEntry(Zulu, ModuleContract.Version, Enabled: true),
                        new ModuleCatalogueEntry(Mike, 0, Enabled: false),
                        new ModuleCatalogueEntry(Alpha, 0, Enabled: true),
                    ]));
                    services.AddScoped<barakoCMS.Features.Workflows.IWorkflowAction, DescribeProbeAction>();
                }));
        }
    }

    private static WebApplicationFactory<Program>? _brokenRegistry;

    // The registry resolves and only reading the actions throws. A registration that throws on
    // resolve stops the host from starting: the workflow endpoints take the registry, or the
    // validator built on it, in their constructors, and those are resolved while endpoints are
    // mapped. No throwing action is registered either, because a derived host runs a workflow
    // runner against the shared database and would fail other classes' runs.
    private WebApplicationFactory<Program> BrokenRegistryHost()
    {
        lock (Gate)
        {
            return _brokenRegistry ??= _factory.WithWebHostBuilder(builder =>
                builder.ConfigureServices(services =>
                    services.AddScoped<IWorkflowPluginRegistry>(provider => new UnreadableRegistry(
                        new WorkflowPluginRegistry(
                            provider.GetServices<barakoCMS.Features.Workflows.IWorkflowAction>())))));
        }
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        var response = await _factory.CreateClient().GetAsync(Route, TestContext.Current.CancellationToken);

        // Exactly 401. A 404 would satisfy "not 200" and means the route is not there at all.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Every_field_type_in_the_registry_is_described_with_its_aliases_and_editor_hint()
    {
        var document = await DescribeAsync(await CallerHolding());

        var described = document.GetProperty("fieldTypes").EnumerateArray().ToList();
        FieldTypeRegistry.Types.Should().NotBeEmpty("an empty registry would make every assertion below vacuous");
        described.Should().HaveCount(FieldTypeRegistry.Types.Count);

        described.Select(Name).Should().Equal(FieldTypeRegistry.Types.Select(t => t.Name),
            "one entry per canonical type, in the registry's own order");

        foreach (var type in described)
        {
            type.GetProperty("editorHint").GetString().Should().Be(FieldTypeRegistry.EditorHintFor(Name(type)!));

            foreach (var alias in Strings(type, "aliases"))
            {
                FieldTypeRegistry.IsKnownType(alias).Should().BeTrue("{0} is listed as an alias", alias);
                FieldTypeRegistry.EditorHintFor(alias).Should().Be(type.GetProperty("editorHint").GetString(),
                    "an alias resolves to the type it is listed under");
            }
        }

        // Canonical names and aliases together are every name a content type may declare.
        described.SelectMany(t => Strings(t, "aliases").Prepend(Name(t)!))
            .Should().BeEquivalentTo(FieldTypeRegistry.AllowedTypeNames);
    }

    /// <summary>
    /// The rules listed under a type are the ones a content type save accepts on a field of that
    /// type, asked of the validator itself and not of the method the document was built with.
    /// </summary>
    [Fact]
    public async Task The_rules_listed_under_a_field_type_are_the_ones_a_type_save_accepts_on_it()
    {
        var document = await DescribeAsync(await CallerHolding());

        var described = document.GetProperty("fieldTypes").EnumerateArray().ToList();
        described.Should().HaveCount(FieldTypeRegistry.Types.Count);
        FieldRules.Names.Should().NotBeEmpty();

        var listedSomewhere = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in described)
        {
            var listed = Strings(type, "ruleNames");
            listedSomewhere.UnionWith(listed);

            foreach (var rule in FieldRules.Names)
            {
                listed.Contains(rule).Should().Be(!Misplaced(Name(type)!, rule),
                    "rule '{0}' on a field of type '{1}'", rule, Name(type));
            }
        }

        listedSomewhere.Should().BeEquivalentTo(FieldRules.Names, "every rule applies to some type");
    }

    [Fact]
    public async Task Every_rule_is_described_and_an_alias_is_a_name_a_type_save_reads()
    {
        var document = await DescribeAsync(await CallerHolding());

        var rules = document.GetProperty("rules").EnumerateArray().ToList();
        FieldRules.Names.Should().NotBeEmpty();
        rules.Should().HaveCount(FieldRules.Names.Count);
        rules.Select(Name).Should().Equal(FieldRules.Names);

        var aliases = rules.SelectMany(r => Strings(r, "aliases")).ToList();
        aliases.Should().Equal([FieldRules.PatternAlias], "pattern is the one rule with a second name");

        var field = new FieldDefinition
        {
            Name = "Field",
            Type = "string",
            ValidationRules = new Dictionary<string, object> { [aliases[0]] = "^a$" },
        };
        FieldRules.DefinitionErrors(field).Should().BeEmpty("the alias is read as the rule it is listed under");
    }

    [Fact]
    public async Task A_caller_holding_no_capability_gets_the_field_types_and_rules_and_nothing_else()
    {
        var document = await DescribeAsync(await CallerHolding());

        document.GetProperty("fieldTypes").GetArrayLength().Should().Be(FieldTypeRegistry.Types.Count);
        document.GetProperty("rules").GetArrayLength().Should().Be(FieldRules.Names.Count);
        document.GetProperty("apiContractVersion").GetInt32()
            .Should().Be(barakoCMS.Features.Monitoring.Meta.ApiContract.Version);

        Withheld(document, "capabilities").Should().BeTrue();
        Withheld(document, "workflowActions").Should().BeTrue();
        Withheld(document, "modules").Should().BeTrue();
    }

    [Fact]
    public async Task A_caller_who_may_read_roles_gets_the_capability_vocabulary_and_only_that()
    {
        var document = await DescribeAsync(await CallerHolding(SystemCapabilities.ManageRoles));

        var vocabulary = _factory.Services.GetRequiredService<CapabilityVocabulary>().Entries;
        vocabulary.Should().NotBeEmpty();

        var described = document.GetProperty("capabilities").EnumerateArray().ToList();
        described.Should().HaveCount(vocabulary.Count);
        described.Select(c => (Name(c), c.GetProperty("source").GetString()))
            .Should().Equal(vocabulary.Select(e => ((string?)e.Name, (string?)e.Source)));

        // Not one of core's constants: it reaches the vocabulary from a module endpoint's gate,
        // which is how a capability added by a module reaches this document.
        SystemCapabilities.Known.Should().NotContain(BarakoCMS.Pwa.PwaCapabilities.ViewPwaInstalls);
        described.Select(Name).Should().Contain(BarakoCMS.Pwa.PwaCapabilities.ViewPwaInstalls);

        Withheld(document, "workflowActions").Should().BeTrue();
        Withheld(document, "modules").Should().BeTrue();
    }

    [Fact]
    public async Task A_caller_who_may_manage_workflows_gets_every_registered_action_and_only_that()
    {
        var document = await DescribeAsync(await CallerHolding(SystemCapabilities.ManageWorkflows));

        using var scope = _factory.Services.CreateScope();
        var registered = scope.ServiceProvider.GetRequiredService<IWorkflowPluginRegistry>().GetAllActions();
        registered.Should().NotBeEmpty();

        var described = document.GetProperty("workflowActions").EnumerateArray().ToList();
        described.Should().HaveCount(registered.Count);
        described.Select(a => a.GetProperty("type").GetString())
            .Should().Equal(registered.Select(a => a.Type).OrderBy(t => t, StringComparer.Ordinal));

        foreach (var action in described)
        {
            var source = registered.Single(a => a.Type == action.GetProperty("type").GetString());
            Strings(action, "requiredParameters").Should().Equal(source.RequiredParameters);
            Strings(action, "optionalParameters").Should().Equal(source.OptionalParameters);
            Strings(action, "secretParameters").Should().Equal(source.SecretParameters);
        }

        Withheld(document, "capabilities").Should().BeTrue();
        Withheld(document, "modules").Should().BeTrue();
    }

    /// <summary>
    /// An action registered in the container, which is what a module's <c>ConfigureServices</c>
    /// does, is in the document with no other edit.
    /// </summary>
    [Fact]
    public async Task A_workflow_action_a_host_registers_is_described_with_its_parameters()
    {
        var document = await DescribeAsync(
            await CallerHolding(SystemCapabilities.ManageWorkflows), ExtendedHost());

        var expected = new WorkflowPluginRegistry([new DescribeProbeAction()]).GetAllActions().Single();
        expected.SecretParameters.Should().Equal(["ApiKey"], "the control: the probe declares one secret parameter");

        var described = document.GetProperty("workflowActions").EnumerateArray().ToList();
        described.Should().NotBeEmpty();
        var probe = described.Single(a => a.GetProperty("type").GetString() == DescribeProbeAction.TypeName);

        probe.GetProperty("description").GetString().Should().Be(expected.Description);
        Strings(probe, "requiredParameters").Should().Equal(expected.RequiredParameters);
        Strings(probe, "optionalParameters").Should().Equal(expected.OptionalParameters);
        Strings(probe, "secretParameters").Should().Equal(expected.SecretParameters);

        var onTheSharedHost = await DescribeAsync(await CallerHolding(SystemCapabilities.ManageWorkflows));
        onTheSharedHost.GetProperty("workflowActions").EnumerateArray()
            .Select(a => a.GetProperty("type").GetString())
            .Should().NotContain(DescribeProbeAction.TypeName, "a host that does not register it does not describe it");
    }

    [Fact]
    public async Task A_caller_who_may_view_modules_gets_the_modules_that_run_and_not_one_left_off()
    {
        var document = await DescribeAsync(await CallerHolding(SystemCapabilities.ViewModules), ExtendedHost());

        var modules = document.GetProperty("modules").EnumerateArray().ToList();
        modules.Should().HaveCount(2, "three were seen and one of them is switched off");
        modules.Select(Name).Should().Equal([Alpha, Zulu]);

        foreach (var module in modules)
        {
            module.EnumerateObject().Select(p => p.Name).Should().Equal(["name"]);
        }

        Withheld(document, "capabilities").Should().BeTrue();
        Withheld(document, "workflowActions").Should().BeTrue();
    }

    /// <summary>
    /// "None" is an answer and is not the same as "not yours to see".
    /// </summary>
    [Fact]
    public async Task A_host_running_no_modules_answers_an_empty_list_to_a_caller_who_may_view_them()
    {
        var document = await DescribeAsync(await CallerHolding(SystemCapabilities.ViewModules));

        document.GetProperty("modules").ValueKind.Should().Be(JsonValueKind.Array);
        document.GetProperty("modules").GetArrayLength().Should().Be(0);
    }

    /// <summary>
    /// The field names are what a console on its own release schedule reads, so they are pinned.
    /// </summary>
    [Fact]
    public async Task A_super_admin_gets_every_part_under_the_documented_names()
    {
        var client = ExtendedHost().CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("SuperAdmin"));

        var document = await DescribeAsync(client);

        document.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            ["apiContractVersion", "fieldTypes", "rules", "capabilities", "workflowActions", "modules"]);

        var fieldTypes = document.GetProperty("fieldTypes").EnumerateArray().ToList();
        fieldTypes.Should().NotBeEmpty();
        foreach (var type in fieldTypes)
        {
            type.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
                ["name", "aliases", "editorHint", "ruleNames"]);
        }

        var rules = document.GetProperty("rules").EnumerateArray().ToList();
        rules.Should().NotBeEmpty();
        foreach (var rule in rules)
        {
            rule.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["name", "aliases"]);
        }

        document.GetProperty("capabilities").GetArrayLength().Should().BeGreaterThan(0);
        document.GetProperty("workflowActions").GetArrayLength().Should().BeGreaterThan(0);
        document.GetProperty("modules").GetArrayLength().Should().Be(2);
    }

    /// <summary>
    /// On the answer and on a refusal alike, so the header is not something only the handler sets.
    /// </summary>
    [Fact]
    public async Task The_document_is_not_kept_by_a_cache_whether_it_is_served_or_refused()
    {
        var served = await (await CallerHolding()).GetAsync(Route, TestContext.Current.CancellationToken);
        var refused = await _factory.CreateClient().GetAsync(Route, TestContext.Current.CancellationToken);

        served.StatusCode.Should().Be(HttpStatusCode.OK);
        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        foreach (var response in new[] { served, refused })
        {
            response.Headers.CacheControl.Should().NotBeNull("{0} must say how it may be cached", response.StatusCode);
            response.Headers.CacheControl!.NoStore.Should().BeTrue(
                "the body differs by what the caller holds, and a grant can be taken away");
            response.Headers.Pragma.Select(p => p.Name).Should().Contain("no-cache",
                "HTTP/1.0 caches read Pragma, not Cache-Control");
        }
    }

    /// <summary>
    /// The control. <c>/api/meta</c> is a prefix of this route and is the same for every caller, so
    /// marking the describe document must not start marking it.
    /// </summary>
    [Fact]
    public async Task The_meta_endpoint_beside_it_keeps_the_headers_it_had()
    {
        var refused = await _factory.CreateClient().GetAsync("/api/meta", TestContext.Current.CancellationToken);
        var served = await (await CallerHolding()).GetAsync("/api/meta", TestContext.Current.CancellationToken);

        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        served.StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var response in new[] { served, refused })
        {
            (response.Headers.CacheControl?.NoStore ?? false).Should().BeFalse();
            response.Headers.Pragma.Should().BeEmpty();
        }
    }

    /// <summary>
    /// A registry that cannot be built costs the one part that reads it, not the document.
    /// </summary>
    [Fact]
    public async Task When_the_action_registry_cannot_be_read_the_part_is_null_and_the_rest_still_answers()
    {
        var document = await DescribeAsync(
            await CallerHolding(SystemCapabilities.ManageWorkflows, SystemCapabilities.ViewModules), BrokenRegistryHost());

        document.GetProperty("fieldTypes").GetArrayLength().Should().Be(FieldTypeRegistry.Types.Count);
        document.GetProperty("rules").GetArrayLength().Should().Be(FieldRules.Names.Count);
        Withheld(document, "workflowActions").Should().BeTrue("the caller may read them and the registry threw");

        using (var scope = BrokenRegistryHost().Services.CreateScope())
        {
            var registry = scope.ServiceProvider.GetRequiredService<IWorkflowPluginRegistry>();
            registry.IsActionRegistered("Email").Should().BeTrue("the control: the registry itself resolves on this host");
            registry.Invoking(r => r.GetAllActions()).Should().Throw<InvalidOperationException>(
                "and reading the actions is what fails, so the null above is the endpoint's catch");
        }

        document.GetProperty("modules").ValueKind.Should().Be(JsonValueKind.Array,
            "a part that comes after the failed one is still answered");

        var onTheSharedHost = await DescribeAsync(await CallerHolding(SystemCapabilities.ManageWorkflows));
        onTheSharedHost.GetProperty("workflowActions").GetArrayLength().Should().BeGreaterThan(0,
            "the control: the same kind of caller is shown the actions where the registry can be read");
    }

    /// <summary>
    /// The entries of these two parts are the response types of other endpoints, so their property
    /// names are pinned here as well: a rename in either type is a break to this document too.
    /// </summary>
    [Fact]
    public async Task The_names_inside_a_capability_and_a_workflow_action_are_pinned()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("SuperAdmin"));

        var document = await DescribeAsync(client);

        var capabilities = document.GetProperty("capabilities").EnumerateArray().ToList();
        capabilities.Should().NotBeEmpty();
        foreach (var capability in capabilities)
        {
            capability.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["name", "source", "note"]);
        }

        var actions = document.GetProperty("workflowActions").EnumerateArray().ToList();
        actions.Should().NotBeEmpty();
        foreach (var action in actions)
        {
            action.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            [
                "type", "description", "requiredParameters", "optionalParameters", "secretParameters",
                "group", "exampleConfiguration",
            ]);
        }
    }

    /// <summary>
    /// The three gates are written down a second time in <see cref="DescribeDocument"/>. This reads
    /// the gate each listing endpoint declares off the routing table and requires the copy to match,
    /// so changing who may list capabilities, actions or modules cannot leave this document behind.
    /// </summary>
    [Fact]
    public void Each_gated_part_asks_for_what_the_endpoint_that_lists_it_asks_for()
    {
        var pairs = new (string Path, RequiredCapability Gate)[]
        {
            ("api/capabilities", DescribeDocument.CapabilitiesGate),
            ("api/workflows/actions", DescribeDocument.WorkflowActionsGate),
            ("api/modules", DescribeDocument.ModulesGate),
        };

        foreach (var (path, gate) in pairs)
        {
            var declared = _factory.Services.GetServices<EndpointDataSource>()
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Where(endpoint => string.Equals(
                    endpoint.RoutePattern.RawText?.Trim('/'), path, StringComparison.OrdinalIgnoreCase))
                .Where(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("GET") == true)
                .Select(endpoint => endpoint.Metadata.GetMetadata<RequiredCapability>())
                .Where(required => required is not null)
                .ToList();

            declared.Should().ContainSingle("GET /{0} declares one capability gate", path);
            gate.Capability.Should().Be(declared[0]!.Capability);
            gate.LegacyRoles.Should().BeEquivalentTo(declared[0]!.LegacyRoles);
        }
    }

    private static bool Misplaced(string type, string rule)
    {
        var field = new FieldDefinition
        {
            Name = "Field",
            DisplayName = "Field",
            Type = type,
            ValidationRules = new Dictionary<string, object> { [rule] = 1L },
        };

        // The validator's own words for a rule on a type it does not apply to. A rule that applies
        // and holds an unusable value is refused in other words, which is not what is asked here.
        return FieldRules.DefinitionErrors(field).Any(error => error.Contains(", which applies to "));
    }

    private static string? Name(JsonElement entry) => entry.GetProperty("name").GetString();

    private static List<string> Strings(JsonElement entry, string property) =>
        entry.GetProperty(property).EnumerateArray().Select(value => value.GetString()!).ToList();

    // Present and null, which is what the document promises: a console reads "not yours to see"
    // from the null, and a missing property would read the same as an older API.
    private static bool Withheld(JsonElement document, string property) =>
        document.GetProperty(property).ValueKind == JsonValueKind.Null;

    private async Task<JsonElement> DescribeAsync(HttpClient client, WebApplicationFactory<Program>? host = null)
    {
        if (host is not null)
        {
            var onHost = host.CreateClient();
            onHost.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;
            client = onHost;
        }

        var response = await client.GetAsync(Route, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var parsed = JsonDocument.Parse(body);
        return parsed.RootElement.Clone();
    }

    /// <summary>
    /// A caller whose one role holds exactly these capabilities, under a name no gate lists, so the
    /// legacy fallback cannot be what admits it.
    /// </summary>
    private async Task<HttpClient> CallerHolding(params string[] capabilities)
    {
        var unique = $"Describe Caller {Guid.NewGuid():N}";

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new Role { Id = Guid.NewGuid(), Name = unique, SystemCapabilities = capabilities.ToList() };
        session.Store(role);

        var userId = Guid.NewGuid();
        session.Store(new User
        {
            Id = userId,
            Username = $"describe-{userId:n}",
            Email = $"describe-{userId:n}@example.com",
            RoleIds = [role.Id],
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: [unique], userId: userId.ToString()));
        return client;
    }
}

/// <summary>A registry that answers everything but the list of actions.</summary>
internal sealed class UnreadableRegistry(IWorkflowPluginRegistry inner) : IWorkflowPluginRegistry
{
    public IReadOnlyList<WorkflowActionMetadata> GetAllActions() =>
        throw new InvalidOperationException("an action could not be read");

    public WorkflowActionMetadata? GetActionMetadata(string actionType) => inner.GetActionMetadata(actionType);

    public bool IsActionRegistered(string actionType) => inner.IsActionRegistered(actionType);
}

[barakoCMS.Infrastructure.Attributes.WorkflowActionMetadataAttribute(
    Description = "A probe for the describe document",
    RequiredParameters = new[] { "Target" },
    OptionalParameters = new[] { "ApiKey" })]
internal sealed class DescribeProbeAction : barakoCMS.Features.Workflows.IWorkflowAction
{
    public const string TypeName = "DescribeProbe";

    public string Type => TypeName;

    public Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct) =>
        Task.CompletedTask;
}
