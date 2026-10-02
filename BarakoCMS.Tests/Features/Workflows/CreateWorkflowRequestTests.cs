using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;
using Marten;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// Creating a workflow takes a request of its own and not the stored document, and reads every
/// field a caller chooses the way it did when it bound the document.
/// </summary>
/// <remarks>
/// The request types are reached through the endpoints and never named, so these tests compile
/// against an endpoint that binds the stored type and fail there, which is what they are for.
///
/// Two kinds of test. The shape tests fail when an endpoint binds <see cref="WorkflowDefinition"/>.
/// The tests that go over HTTP hold what a caller sees still, and pass either way: an id sent to
/// create was already overwritten before this, so no request can tell the two apart.
/// </remarks>
[Collection("Sequential")]
public class CreateWorkflowRequestTests
{
    private static readonly string[] Accepted =
    [
        "Name",
        "TriggerContentType",
        "TriggerContentTypes",
        "TriggerEvent",
        "TriggerEvents",
        "Conditions",
        "Actions",
        "Enabled",
    ];

    /// <summary>What the stored workflow has that no request may set.</summary>
    private static readonly string[] ServerOwned = ["Id"];

    private readonly IntegrationTestFixture _factory;
    private readonly WorkflowStopHarness _harness;

    public CreateWorkflowRequestTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _harness = new WorkflowStopHarness(factory);
    }

    private static CancellationToken Ct => WorkflowStopHarness.Ct;

    private static Type CreateRequest => typeof(CreateWorkflowEndpoint).BaseType!.GetGenericArguments()[0];

    private static Type DryRunWorkflow =>
        typeof(barakoCMS.Features.Workflows.DryRunWorkflow.Request).GetProperty("Workflow")!.PropertyType;

    private static Type ValidateRequest => typeof(barakoCMS.Features.Workflows.ValidateWorkflow.Request);

    private static Dictionary<string, PropertyInfo> PropertiesOf(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).ToDictionary(p => p.Name);

    [Fact]
    public void Create_binds_a_request_of_its_own_with_no_id()
    {
        CreateRequest.Should().NotBe(typeof(WorkflowDefinition));
        CreateRequest.Assembly.FullName.Should().Be(typeof(CreateWorkflowEndpoint).Assembly.FullName,
            "a type from the package contract is one a module stores, not one a caller writes");

        var names = PropertiesOf(CreateRequest).Keys.ToList();

        names.Should().HaveCount(8);
        names.Should().BeEquivalentTo(Accepted);
        names.Should().NotContain("Id");
    }

    /// <summary>
    /// A field added to the stored workflow fails here until somebody says which list it goes in,
    /// so whether a caller may set it is a decision and not something the binder settles.
    /// </summary>
    [Fact]
    public void Every_field_of_the_stored_workflow_is_accepted_by_create_or_named_as_server_owned()
    {
        var stored = PropertiesOf(typeof(WorkflowDefinition));
        var request = PropertiesOf(CreateRequest);

        stored.Should().HaveCount(9);
        stored.Keys.Should().BeEquivalentTo(Accepted.Concat(ServerOwned));
        request.Keys.Should().BeEquivalentTo(stored.Keys.Except(ServerOwned));

        var storedDefaults = new WorkflowDefinition();
        var requestDefaults = Activator.CreateInstance(CreateRequest)!;

        foreach (var name in Accepted)
        {
            request[name].PropertyType.Should().Be(stored[name].PropertyType, "{0} must bind the same JSON", name);

            JsonSerializer.Serialize(request[name].GetValue(requestDefaults))
                .Should().Be(JsonSerializer.Serialize(stored[name].GetValue(storedDefaults)),
                    "a request that leaves {0} out must be read as it was", name);
        }
    }

    [Fact]
    public void Validate_reads_every_field_create_reads_apart_from_enabled()
    {
        var create = PropertiesOf(CreateRequest);
        var validate = PropertiesOf(ValidateRequest);

        validate.Should().HaveCount(7);
        create.Keys.Except(validate.Keys).Should().Equal("Enabled");
        validate.Keys.Except(create.Keys).Should().BeEmpty();

        foreach (var (name, property) in validate)
        {
            property.PropertyType.Should().Be(create[name].PropertyType,
                "a request validate passes has to be the request create reads, and {0} differs", name);
        }
    }

    [Fact]
    public void The_dry_run_workflow_is_what_create_takes_plus_the_id_its_log_is_filed_under()
    {
        DryRunWorkflow.Should().NotBe(typeof(WorkflowDefinition));
        CreateRequest.IsAssignableFrom(DryRunWorkflow).Should().BeTrue(
            "a dry run that read a field differently from create would simulate a different workflow");

        var names = PropertiesOf(DryRunWorkflow).Keys.ToList();

        names.Should().HaveCount(9);
        names.Should().BeEquivalentTo(Accepted.Append("Id"));
    }

    [Fact]
    public async Task The_OpenAPI_document_describes_the_create_body_without_an_id()
    {
        using var doc = await OpenApiTagTests.FetchDocumentAsync(_factory);

        var body = OpenApiSchemaReader.RequestBody(doc, OpenApiSchemaReader.Operation(doc, "/api/workflows", "post"));
        var names = OpenApiSchemaReader.PropertyNames(body);

        names.Should().HaveCount(8);
        names.Should().BeEquivalentTo(Accepted.Select(JsonNamingPolicy.CamelCase.ConvertName));
        names.Should().NotContain("id");
    }

    /// <summary>
    /// The id of a workflow that already exists, sent to create. Holds either way: the handler
    /// overwrote a bound id before the request type existed.
    /// </summary>
    [Fact]
    public async Task An_id_sent_to_create_names_no_workflow_and_overwrites_none()
    {
        var admin = await _harness.AdminAsync();
        var firstName = WorkflowStopHarness.NewName("655-first");
        var secondName = WorkflowStopHarness.NewName("655-second");

        var first = await CreateAsync(admin, new { name = firstName, triggerContentType = WorkflowStopHarness.NewName("post"), triggerEvent = "Published", actions = Email() });
        var firstId = first.GetProperty("id").GetGuid();

        var second = await CreateAsync(admin, new { id = firstId, name = secondName, triggerContentType = WorkflowStopHarness.NewName("post"), triggerEvent = "Published", actions = Email() });
        var secondId = second.GetProperty("id").GetGuid();

        secondId.Should().NotBe(firstId);

        var stored = await StoredNamedAsync(firstName, secondName);
        stored.Should().HaveCount(2, "the second request creates a workflow and replaces none");
        stored.Single(w => w.Id == firstId).Name.Should().Be(firstName);
        stored.Single(w => w.Id == secondId).Name.Should().Be(secondName);

        var unused = Guid.NewGuid();
        var third = await CreateAsync(admin, new { id = unused, name = WorkflowStopHarness.NewName("655-third"), triggerContentType = WorkflowStopHarness.NewName("post"), triggerEvent = "Published", actions = Email() });

        third.GetProperty("id").GetGuid().Should().NotBe(unused);
        (await _harness.LoadWorkflowAsync(unused)).Should().BeNull();
    }

    /// <summary>Holds either way. A property no type declares was skipped by the binder before too.</summary>
    [Fact]
    public async Task A_property_the_API_does_not_know_is_ignored_and_the_workflow_is_saved()
    {
        var admin = await _harness.AdminAsync();
        var name = WorkflowStopHarness.NewName("655-unknown");

        var created = await CreateAsync(admin, new
        {
            name,
            triggerContentType = WorkflowStopHarness.NewName("post"),
            triggerEvent = "Published",
            actions = Email(),
            tenantId = "another-tenant",
            createdAt = DateTimeOffset.UtcNow,
            createdBy = Guid.NewGuid(),
            anything = new { nested = true },
        });

        var stored = await StoredNamedAsync(name);
        stored.Should().HaveCount(1);
        stored[0].Id.Should().Be(created.GetProperty("id").GetGuid());
    }

    /// <summary>
    /// Holds either way against the stored type, and fails if the request stops handing a field to
    /// the definition: each accepted field is sent with a value that is not its default.
    /// </summary>
    [Fact]
    public async Task Every_field_create_accepts_is_stored_as_it_was_sent()
    {
        var admin = await _harness.AdminAsync();
        var name = WorkflowStopHarness.NewName("655-all");
        var firstType = WorkflowStopHarness.NewName("post");
        var secondType = WorkflowStopHarness.NewName("page");

        var created = await CreateAsync(admin, new
        {
            name,
            triggerContentType = firstType,
            triggerContentTypes = new[] { secondType },
            triggerEvent = "Published",
            triggerEvents = new[] { "Created", "Updated" },
            conditions = new Dictionary<string, string> { ["Status"] = "Approved" },
            actions = Email(),
            enabled = false,
        });

        var stored = await _harness.LoadWorkflowAsync(created.GetProperty("id").GetGuid());

        stored.Should().NotBeNull();
        stored!.Name.Should().Be(name);
        stored.TriggerContentType.Should().Be(firstType);
        stored.TriggerContentTypes.Should().HaveCount(2);
        stored.TriggerContentTypes.Should().Equal(firstType, secondType);
        stored.TriggerEvent.Should().Be("Published");
        stored.TriggerEvents.Should().HaveCount(3);
        stored.TriggerEvents.Should().Equal("Published", "Created", "Updated");
        stored.Conditions.Should().HaveCount(1);
        stored.Conditions["Status"].Should().Be("Approved");
        stored.Actions.Should().HaveCount(1);
        stored.Actions[0].Type.Should().Be("Email");
        stored.Actions[0].Parameters.Should().HaveCount(3);
        stored.Actions[0].Parameters["To"].Should().Be("a@example.com");
        stored.Actions[0].Parameters["Subject"].Should().Be("s");
        stored.Actions[0].Parameters["Body"].Should().Be("b");
        stored.Enabled.Should().BeFalse();
    }

    /// <summary>Holds either way. The schema validator's words still reach the caller as they did.</summary>
    [Fact]
    public async Task A_refused_create_answers_400_with_the_schema_validator_s_field_and_message()
    {
        var admin = await _harness.AdminAsync();
        var name = WorkflowStopHarness.NewName("655-refused");

        var response = await admin.PostAsJsonAsync("/api/workflows", new { name, triggerContentType = WorkflowStopHarness.NewName("post"), triggerEvent = "Published" }, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        text.Should().Contain("actions: At least one action is required");
        (await StoredNamedAsync(name)).Should().BeEmpty();

        var saved = await CreateAsync(admin, new { name, triggerContentType = WorkflowStopHarness.NewName("post"), triggerEvent = "Published", actions = Email() });
        (await StoredNamedAsync(name)).Should().HaveCount(1, "the same request with an action is saved, so the refusal was about the action");
        saved.GetProperty("name").GetString().Should().Be(name);
    }

    /// <summary>Holds either way. The id a dry run sends is a reference, and it is kept.</summary>
    [Fact]
    public async Task A_dry_run_is_logged_under_the_workflow_id_the_request_names()
    {
        var admin = await _harness.AdminAsync();
        var workflowId = Guid.NewGuid();

        var response = await admin.PostAsJsonAsync("/api/workflows/dry-run", new
        {
            workflow = new
            {
                id = workflowId,
                name = "Dry run",
                triggerContentType = "post",
                triggerEvent = "Published",
                actions = Email(),
            },
            sampleContent = new
            {
                id = Guid.NewGuid(),
                contentType = "post",
                status = 0,
                data = new Dictionary<string, object> { ["Title"] = "t" },
            },
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "got {0}", await response.Content.ReadAsStringAsync(Ct));

        await using var session = _harness.Store.QuerySession();
        var logs = await session.Query<WorkflowExecutionLog>().Where(l => l.WorkflowId == workflowId).ToListAsync(Ct);

        logs.Should().HaveCount(1);
        logs[0].IsDryRun.Should().BeTrue();
        logs[0].Actions.Should().HaveCount(1);
        logs[0].Actions[0].ActionType.Should().Be("Email");
    }

    private static object[] Email() =>
    [
        new
        {
            type = "Email",
            parameters = new Dictionary<string, string> { ["To"] = "a@example.com", ["Subject"] = "s", ["Body"] = "b" },
        },
    ];

    private static async Task<JsonElement> CreateAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/api/workflows", body, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", response.StatusCode, text);

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private async Task<IReadOnlyList<WorkflowDefinition>> StoredNamedAsync(params string[] names)
    {
        // A list and not the array: on an array, Contains binds to the span overload, which a query
        // provider cannot translate.
        var wanted = names.ToList();

        await using var session = _harness.Store.QuerySession();
        return await session.Query<WorkflowDefinition>().Where(w => wanted.Contains(w.Name)).ToListAsync(Ct);
    }
}
