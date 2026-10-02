using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Http;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// Issue #871: the credentials of the child actions a Conditional carries in ThenActions and
/// ElseActions are stored encrypted, left out of the API's responses and decrypted before the child
/// runs.
/// </summary>
[Collection("Sequential")]
public class ConditionalChildCredentialTests
{
    private const string Condition = "{{status}} == Published";

    private readonly IntegrationTestFixture _fixture;

    public ConditionalChildCredentialTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private ISecretProtector Protector() => _fixture.Services.GetRequiredService<ISecretProtector>();

    private static string NewSecret() => "whsec_child_" + Guid.NewGuid().ToString("N");

    private static string NewApiKey() => "ak_child_" + Guid.NewGuid().ToString("N");

    private static object Child(string type, Dictionary<string, string> parameters) =>
        new { Type = type, Parameters = parameters };

    private static string Branch(params object[] children) => JsonSerializer.Serialize(children);

    private static Content PublishedContent() => new()
    {
        Id = Guid.NewGuid(),
        ContentType = "article",
        Status = ContentStatus.Published,
        Sensitivity = SensitivityLevel.Public,
        Data = new Dictionary<string, object> { ["Title"] = "hello" },
    };

    private static WorkflowDefinition Conditional(string? thenActions = null, string? elseActions = null)
    {
        var parameters = new Dictionary<string, string> { ["Condition"] = Condition };
        if (thenActions is not null) parameters["ThenActions"] = thenActions;
        if (elseActions is not null) parameters["ElseActions"] = elseActions;

        var id = Guid.NewGuid();
        return new WorkflowDefinition
        {
            Id = id,
            Name = "conditional-child-" + id.ToString("N"),
            TriggerContentType = "article",
            TriggerEvent = "Published",
            Actions = [new WorkflowAction { Type = "Conditional", Parameters = parameters }],
        };
    }

    private async Task<string> StoredJsonAsync(Guid id)
    {
        await using var session = _fixture.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var json = await session.Json.FindByIdAsync<WorkflowDefinition>(id, TestContext.Current.CancellationToken);
        json.Should().NotBeNull();
        return json!;
    }

    [Fact]
    public async Task A_childs_credentials_are_stored_encrypted_and_left_out_of_the_create_and_list_responses()
    {
        var secret = NewSecret();
        var apiKey = NewApiKey();
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));

        var thenActions = Branch(
            Child("Webhook", new() { ["Url"] = "https://hooks.example.com/child", ["Secret"] = secret }),
            Child("CredentialEcho", new() { ["ApiKey"] = apiKey, ["Channel"] = "ops-channel" }));

        var response = await client.PostAsJsonAsync("/api/workflows", new
        {
            name = "conditional-child-" + Guid.NewGuid().ToString("N"),
            triggerContentType = "article",
            triggerEvent = "Published",
            actions = new[]
            {
                new { type = "Conditional", parameters = new Dictionary<string, string> { ["Condition"] = Condition, ["ThenActions"] = thenActions } },
            },
        }, TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.IsSuccessStatusCode.Should().BeTrue("got {0}", response.StatusCode);
        body.Should().Contain("ops-channel", "the response is the one for this workflow");
        body.Should().NotContain(secret);
        body.Should().NotContain(apiKey);

        using var created = JsonDocument.Parse(body);
        var id = created.RootElement.GetProperty("id").GetGuid();
        var returnedBranch = created.RootElement.GetProperty("actions")[0].GetProperty("parameters").GetProperty("ThenActions").GetString();

        using var returned = JsonDocument.Parse(returnedBranch!);
        returned.RootElement.GetArrayLength().Should().Be(2);
        var returnedWebhook = returned.RootElement[0];
        returnedWebhook.GetProperty("SecretSet").GetBoolean().Should().BeTrue();
        returnedWebhook.GetProperty("Parameters").TryGetProperty("Secret", out _).Should().BeFalse("a boolean stands in for the value");
        returnedWebhook.GetProperty("Parameters").GetProperty("Url").GetString().Should().Be("https://hooks.example.com/child");
        var returnedEcho = returned.RootElement[1];
        returnedEcho.GetProperty("SecretSet").GetBoolean().Should().BeFalse();
        returnedEcho.GetProperty("Parameters").TryGetProperty("ApiKey", out _).Should().BeFalse();
        returnedEcho.GetProperty("Parameters").GetProperty("Channel").GetString().Should().Be("ops-channel");

        var json = await StoredJsonAsync(id);
        json.Should().Contain("ops-channel", "the stored document is the one this workflow was saved as");
        json.Should().NotContain(secret);
        json.Should().NotContain(apiKey);

        await using (var session = _fixture.Services.GetRequiredService<IDocumentStore>().QuerySession())
        {
            var stored = await session.LoadAsync<WorkflowDefinition>(id, TestContext.Current.CancellationToken);
            using var storedBranch = JsonDocument.Parse(stored!.Actions.Single().Parameters["ThenActions"]);
            storedBranch.RootElement.GetArrayLength().Should().Be(2);
            Protector().Unprotect(storedBranch.RootElement[0].GetProperty("Parameters").GetProperty("Secret").GetString()!)
                .Should().Be(secret);
            Protector().Unprotect(storedBranch.RootElement[1].GetProperty("Parameters").GetProperty("ApiKey").GetString()!)
                .Should().Be(apiKey);
        }

        var listBody = await PageHoldingAsync(client, id);
        listBody.Should().Contain("ops-channel");
        listBody.Should().NotContain(secret);
        listBody.Should().NotContain(apiKey);
    }

    [Fact]
    public async Task A_workflow_whose_children_were_stored_in_clear_is_rewritten_by_the_startup_migration()
    {
        var secret = NewSecret();
        var apiKey = NewApiKey();
        var nestedToken = NewApiKey();
        const string notJson = "[{\"Type\":\"CredentialEcho\",\"Parameters\":{\"Channel\":";

        var workflow = Conditional(
            thenActions: Branch(
                Child("Webhook", new() { ["Url"] = "https://hooks.example.com/child", ["Secret"] = secret }),
                Child("CredentialEcho", new() { ["ApiKey"] = apiKey, ["Channel"] = "ops-channel" })),
            elseActions: Branch(
                Child("Conditional", new()
                {
                    ["Condition"] = Condition,
                    ["ThenActions"] = Branch(Child("CredentialEcho", new() { ["Token"] = nestedToken })),
                })));
        workflow.Actions.Add(new WorkflowAction
        {
            Type = "Conditional",
            Parameters = new Dictionary<string, string> { ["Condition"] = Condition, ["ThenActions"] = notJson },
        });

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession())
        {
            session.Store(workflow);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var before = await StoredJsonAsync(workflow.Id);
        before.Should().Contain(secret, "the document has to start out in clear for this to be an upgrade");
        before.Should().Contain(nestedToken);

        var migration = new WorkflowCredentialMigrationService(
            store, Protector(), _fixture.Services.GetRequiredService<IConfiguration>(),
            _fixture.Services.GetRequiredService<ILogger<WorkflowCredentialMigrationService>>());
        await migration.ProtectAllTenantsAsync(TestContext.Current.CancellationToken);

        var afterFirst = await StoredJsonAsync(workflow.Id);
        afterFirst.Should().Contain("ops-channel");
        afterFirst.Should().NotContain(secret);
        afterFirst.Should().NotContain(apiKey);
        afterFirst.Should().NotContain(nestedToken);

        await using (var check = store.QuerySession())
        {
            var stored = await check.LoadAsync<WorkflowDefinition>(workflow.Id, TestContext.Current.CancellationToken);
            stored!.Actions.Should().HaveCount(2);
            stored.Actions[1].Parameters["ThenActions"].Should().Be(notJson, "a branch that is not valid JSON is left as it is");

            using var elseBranch = JsonDocument.Parse(stored.Actions[0].Parameters["ElseActions"]);
            elseBranch.RootElement.GetArrayLength().Should().Be(1);
            using var nestedBranch = JsonDocument.Parse(
                elseBranch.RootElement[0].GetProperty("Parameters").GetProperty("ThenActions").GetString()!);
            nestedBranch.RootElement.GetArrayLength().Should().Be(1);
            Protector().Unprotect(nestedBranch.RootElement[0].GetProperty("Parameters").GetProperty("Token").GetString()!)
                .Should().Be(nestedToken);
        }

        await migration.ProtectAllTenantsAsync(TestContext.Current.CancellationToken);
        (await StoredJsonAsync(workflow.Id)).Should().Be(afterFirst, "an envelope is not encrypted a second time");
    }

    [Fact]
    public async Task A_child_custom_action_is_handed_the_api_key_as_typed()
    {
        var apiKey = NewApiKey();
        var runKey = Guid.NewGuid().ToString();

        var workflow = Conditional(thenActions: Branch(
            Child("CredentialEcho", new() { ["ApiKey"] = apiKey, ["RunId"] = runKey })));

        WebhookSigning.ProtectSecrets(workflow, Protector()).Should().BeTrue();
        var saved = workflow.Actions.Single().Parameters;
        saved["ThenActions"].Should().Contain(runKey).And.NotContain(apiKey, "saving the workflow encrypts the child's key");

        var services = new ServiceCollection();
        services.AddSingleton(Protector());
        services.AddSingleton<IWorkflowAction>(new CredentialEchoAction());
        var conditional = new ConditionalAction(services.BuildServiceProvider(), NullLogger<ConditionalAction>.Instance);

        var result = await conditional.RunAsync(saved, PublishedContent(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        CredentialEchoAction.ReceivedByRun.Should().ContainKey(runKey);
        CredentialEchoAction.ReceivedByRun[runKey].Should().Be(apiKey);
    }

    [Fact]
    public async Task A_child_webhook_signs_with_the_secret_as_typed()
    {
        const string tenant = "conditional-child-webhook";
        var secret = NewSecret();
        using var listener = new RecordingListener();

        var workflow = Conditional(thenActions: Branch(
            Child("Webhook", new() { ["Url"] = listener.Url, ["Secret"] = secret })));

        WebhookSigning.ProtectSecrets(workflow, Protector()).Should().BeTrue();
        var saved = workflow.Actions.Single().Parameters;
        saved["ThenActions"].Should().NotContain(secret, "saving the workflow encrypts the child's secret");

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var guard = new OutboundAddressGuard(isBlocked: _ => false);
        await using var session = store.LightweightSession(tenant);
        using var handler = OutboundHttpHandler.Create(guard);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [WebhookSigning.AllowInsecureSignedUrlsKey] = "true",
        }).Build();

        var services = new ServiceCollection();
        services.AddSingleton(Protector());
        services.AddSingleton<IWorkflowAction>(new WebhookAction(
            new SingleClientFactory(client), session, Protector(), guard, NullLogger<WebhookAction>.Instance, configuration));
        var conditional = new ConditionalAction(services.BuildServiceProvider(), NullLogger<ConditionalAction>.Instance);

        var result = await conditional.RunAsync(saved, PublishedContent(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        listener.WasCalled.Should().BeTrue();
        listener.LastHeaders.Should().ContainKey(WebhookSigning.SignatureHeader);
        listener.LastHeaders.Should().ContainKey(WebhookSigning.TimestampHeader);

        var material = Encoding.UTF8.GetBytes(listener.LastHeaders[WebhookSigning.TimestampHeader] + ".")
            .Concat(listener.LastBodyBytes!).ToArray();
        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), material));
        listener.LastHeaders[WebhookSigning.SignatureHeader].Should().Be(expected);
    }

    [Fact]
    public async Task A_child_credential_the_key_cannot_decrypt_fails_the_child_naming_the_parameter_and_not_the_value()
    {
        var runKey = Guid.NewGuid().ToString();
        var otherKey = new SecretProtector(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Secrets:Key"] = "a-different-key-that-is-at-least-32-characters" })
            .Build());
        var envelope = otherKey.Protect("ak_child_rotated");

        var services = new ServiceCollection();
        services.AddSingleton(Protector());
        services.AddSingleton<IWorkflowAction>(new CredentialEchoAction());
        var conditional = new ConditionalAction(services.BuildServiceProvider(), NullLogger<ConditionalAction>.Instance);

        var result = await conditional.RunAsync(
            new()
            {
                ["Condition"] = Condition,
                ["ThenActions"] = Branch(Child("CredentialEcho", new() { ["ApiKey"] = envelope, ["RunId"] = runKey })),
            },
            PublishedContent(),
            TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse("the key that would decrypt it is not coming back on a retry");
        result.Error.Should().Contain("ApiKey");
        result.Error.Should().NotContain(envelope).And.NotContain("ak_child_rotated");
        CredentialEchoAction.ReceivedByRun.Should().NotContainKey(runKey, "the child must not run with a credential it cannot read");
    }

    [Fact]
    public void A_branch_that_is_not_valid_JSON_is_left_as_it_is_when_a_workflow_is_saved()
    {
        const string notJson = "[{\"Type\":\"CredentialEcho\",\"Parameters\":{\"Channel\":";
        var workflow = Conditional(thenActions: notJson);

        WebhookSigning.ProtectSecrets(workflow, Protector()).Should().BeFalse();

        workflow.Actions.Single().Parameters["ThenActions"].Should().Be(notJson);
    }

    [Fact]
    public void A_nested_childs_credentials_are_left_out_of_the_response()
    {
        var token = NewApiKey();
        var workflow = Conditional(elseActions: Branch(
            Child("Conditional", new()
            {
                ["Condition"] = Condition,
                ["ThenActions"] = Branch(Child("CredentialEcho", new() { ["Token"] = token, ["Channel"] = "ops-channel" })),
            })));

        var parameters = WorkflowActionResponse.From(workflow.Actions.Single()).Parameters;

        parameters.Should().ContainKey("ElseActions");
        parameters["ElseActions"].Should().Contain("ops-channel").And.NotContain(token);
    }

    [Fact]
    public async Task A_child_two_branches_down_is_handed_the_api_key_as_typed()
    {
        var apiKey = NewApiKey();
        var runKey = Guid.NewGuid().ToString();

        // The same child in both inner branches, so which one the inner condition picks does not matter.
        var inner = Branch(Child("CredentialEcho", new() { ["ApiKey"] = apiKey, ["RunId"] = runKey }));
        var workflow = Conditional(thenActions: Branch(
            Child("Conditional", new() { ["Condition"] = Condition, ["ThenActions"] = inner, ["ElseActions"] = inner })));

        WebhookSigning.ProtectSecrets(workflow, Protector()).Should().BeTrue();
        var saved = workflow.Actions.Single().Parameters;
        saved["ThenActions"].Should().Contain(runKey).And.NotContain(apiKey, "saving the workflow encrypts the nested child's key");

        var services = new ServiceCollection();
        services.AddSingleton(Protector());
        services.AddSingleton<IWorkflowAction>(new CredentialEchoAction());
        services.AddSingleton<IWorkflowAction>(provider =>
            new ConditionalAction(provider, NullLogger<ConditionalAction>.Instance));
        var conditional = new ConditionalAction(services.BuildServiceProvider(), NullLogger<ConditionalAction>.Instance);

        var result = await conditional.RunAsync(saved, PublishedContent(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        CredentialEchoAction.ReceivedByRun.Should().ContainKey(runKey);
        CredentialEchoAction.ReceivedByRun[runKey].Should().Be(apiKey);
    }

    /// <summary>Valid JSON whose child has two Parameters objects, the credential in the second.</summary>
    private static string BranchRepeatingParameters(string type, string credentialName, string credential, string runKey) =>
        "[{\"Type\":\"" + type + "\",\"Parameters\":{\"Channel\":\"ops-channel\"},"
        + "\"Parameters\":{\"" + credentialName + "\":\"" + credential + "\",\"RunId\":\"" + runKey + "\"}}]";

    [Fact]
    public void A_branch_that_repeats_a_property_name_is_named_in_the_response_and_not_returned()
    {
        var secret = NewSecret();
        var workflow = Conditional(
            thenActions: BranchRepeatingParameters("Webhook", "Secret", secret, Guid.NewGuid().ToString()),
            elseActions: Branch(Child("CredentialEcho", new() { ["Channel"] = "ops-channel" })));

        var response = WorkflowActionResponse.From(workflow.Actions.Single());

        response.UnreadableBranches.Should().HaveCount(1);
        response.UnreadableBranches.Should().Equal("ThenActions");
        response.Parameters.Should().NotContainKey("ThenActions");
        response.Parameters.Should().ContainKey("ElseActions", "a branch that can be read is still returned");
        response.Parameters["ElseActions"].Should().Contain("ops-channel");
        JsonSerializer.Serialize(response).Should().NotContain(secret);
    }

    [Fact]
    public void A_nested_branch_that_repeats_a_property_name_is_not_returned()
    {
        var secret = NewSecret();
        var workflow = Conditional(thenActions: Branch(
            Child("Conditional", new()
            {
                ["Condition"] = Condition,
                ["ThenActions"] = BranchRepeatingParameters("Webhook", "Secret", secret, Guid.NewGuid().ToString()),
            })));

        var response = WorkflowActionResponse.From(workflow.Actions.Single());

        response.UnreadableBranches.Should().BeEmpty("the outer branch itself can be read");
        response.Parameters.Should().ContainKey("ThenActions");
        response.Parameters["ThenActions"].Should().Contain("UnreadableBranches").And.NotContain(secret);
    }

    [Fact]
    public void A_branch_that_is_not_valid_JSON_is_named_in_the_response_and_not_returned()
    {
        var secret = NewSecret();
        var workflow = Conditional(thenActions: "[{\"Type\":\"Webhook\",\"Parameters\":{\"Secret\":\"" + secret + "\",");

        var response = WorkflowActionResponse.From(workflow.Actions.Single());

        response.UnreadableBranches.Should().HaveCount(1);
        response.UnreadableBranches.Should().Equal("ThenActions");
        JsonSerializer.Serialize(response).Should().NotContain(secret);
    }

    [Fact]
    public async Task A_branch_that_repeats_a_property_name_is_refused_when_the_conditional_runs()
    {
        var apiKey = NewApiKey();
        var runKey = Guid.NewGuid().ToString();
        var workflow = Conditional(thenActions: BranchRepeatingParameters("CredentialEcho", "ApiKey", apiKey, runKey));

        WebhookSigning.ProtectSecrets(workflow, Protector());
        var saved = workflow.Actions.Single().Parameters;

        var services = new ServiceCollection();
        services.AddSingleton(Protector());
        services.AddSingleton<IWorkflowAction>(new CredentialEchoAction());
        var conditional = new ConditionalAction(services.BuildServiceProvider(), NullLogger<ConditionalAction>.Instance);

        var result = await conditional.RunAsync(saved, PublishedContent(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse("the branch reads the same on the next attempt");
        result.Error.Should().Contain("ThenActions").And.NotContain(apiKey);
        CredentialEchoAction.ReceivedByRun.Should().NotContainKey(runKey, "a branch whose credentials were never encrypted must not run");
    }

    private static string NewBase64Secret()
    {
        // No '+' or '/', so the value reads the same inside JSON however it was escaped and a
        // search of the raw stored document for it means something.
        while (true)
        {
            var candidate = Convert.ToBase64String(RandomNumberGenerator.GetBytes(33));
            if (!candidate.Contains('+') && !candidate.Contains('/')) return candidate;
        }
    }

    [Fact]
    public async Task The_startup_migration_encrypts_a_child_secret_that_is_base64_or_hex()
    {
        var base64Secret = NewBase64Secret();
        var hexSecret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

        var workflow = Conditional(thenActions: Branch(
            Child("Webhook", new() { ["Url"] = "https://hooks.example.com/base64", ["Secret"] = base64Secret }),
            Child("Webhook", new() { ["Url"] = "https://hooks.example.com/hex", ["Secret"] = hexSecret })));

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession())
        {
            session.Store(workflow);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var before = await StoredJsonAsync(workflow.Id);
        before.Should().Contain(base64Secret, "the document has to start out in clear for this to be an upgrade");
        before.Should().Contain(hexSecret);

        var migration = new WorkflowCredentialMigrationService(
            store, Protector(), _fixture.Services.GetRequiredService<IConfiguration>(),
            _fixture.Services.GetRequiredService<ILogger<WorkflowCredentialMigrationService>>());
        await migration.ProtectAllTenantsAsync(TestContext.Current.CancellationToken);

        var after = await StoredJsonAsync(workflow.Id);
        after.Should().Contain("hooks.example.com/base64");
        after.Should().NotContain(base64Secret);
        after.Should().NotContain(hexSecret);

        await using var check = store.QuerySession();
        var stored = await check.LoadAsync<WorkflowDefinition>(workflow.Id, TestContext.Current.CancellationToken);
        using var branch = JsonDocument.Parse(stored!.Actions.Single().Parameters["ThenActions"]);
        branch.RootElement.GetArrayLength().Should().Be(2);
        Protector().Unprotect(branch.RootElement[0].GetProperty("Parameters").GetProperty("Secret").GetString()!)
            .Should().Be(base64Secret);
        Protector().Unprotect(branch.RootElement[1].GetProperty("Parameters").GetProperty("Secret").GetString()!)
            .Should().Be(hexSecret);
    }

    private const string ChildInsideAnInnerArray =
        "[[{\"Type\":\"CredentialEcho\",\"Parameters\":{\"ApiKey\":\"ak_shape_marker_7f3a\",\"RunId\":\"shape-inner-array\"}}]]";

    private const string ParameterValueThatIsAnObject =
        "[{\"Type\":\"CredentialEcho\",\"Parameters\":{\"RunId\":\"shape-object-value\",\"Extra\":{\"ApiKey\":\"ak_shape_marker_7f3a\"}}}]";

    private const string CredentialThatIsANumber =
        "[{\"Type\":\"CredentialEcho\",\"Parameters\":{\"RunId\":\"shape-number\",\"ApiKey\":7300000000000001}}]";

    [Theory]
    [InlineData(ChildInsideAnInnerArray)]
    [InlineData(ParameterValueThatIsAnObject)]
    [InlineData(CredentialThatIsANumber)]
    public void A_branch_in_a_shape_the_conditional_cannot_run_is_not_readable_and_is_left_as_sent_when_saved(string branch)
    {
        var workflow = Conditional(thenActions: branch);

        WebhookSigning.IsReadableBranch(branch).Should().BeFalse();
        WebhookSigning.ProtectSecrets(workflow, Protector()).Should().BeFalse();
        workflow.Actions.Single().Parameters["ThenActions"].Should().Be(branch);
    }

    [Theory]
    [InlineData(ChildInsideAnInnerArray, "ak_shape_marker_7f3a")]
    [InlineData(ParameterValueThatIsAnObject, "ak_shape_marker_7f3a")]
    [InlineData(CredentialThatIsANumber, "7300000000000001")]
    public void A_branch_in_a_shape_the_conditional_cannot_run_is_named_in_the_response_and_not_returned(string branch, string marker)
    {
        var workflow = Conditional(thenActions: branch);

        var response = WorkflowActionResponse.From(workflow.Actions.Single());

        response.UnreadableBranches.Should().HaveCount(1);
        response.UnreadableBranches.Should().Equal("ThenActions");
        response.Parameters.Should().NotContainKey("ThenActions");
        JsonSerializer.Serialize(response).Should().NotContain(marker);
    }

    [Theory]
    [InlineData(ChildInsideAnInnerArray, "shape-inner-array")]
    [InlineData(ParameterValueThatIsAnObject, "shape-object-value")]
    [InlineData(CredentialThatIsANumber, "shape-number")]
    public async Task A_branch_in_a_shape_the_conditional_cannot_run_is_refused_when_the_conditional_runs(string branch, string runId)
    {
        var runKey = runId + "-" + Guid.NewGuid().ToString("N");
        var workflow = Conditional(thenActions: branch.Replace(runId, runKey));

        var services = new ServiceCollection();
        services.AddSingleton(Protector());
        services.AddSingleton<IWorkflowAction>(new CredentialEchoAction());
        var conditional = new ConditionalAction(services.BuildServiceProvider(), NullLogger<ConditionalAction>.Instance);

        var result = await conditional.RunAsync(
            workflow.Actions.Single().Parameters, PublishedContent(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse("the branch reads the same on the next attempt");
        result.Error.Should().Contain("ThenActions").And.NotContain("ak_shape_marker_7f3a").And.NotContain("7300000000000001");
        CredentialEchoAction.ReceivedByRun.Should().NotContainKey(runKey);
    }

    [Fact]
    public void A_branch_whose_property_name_cannot_be_read_is_unreadable_and_does_not_break_saving_or_the_response()
    {
        // The JSON text holds the escape for half of a surrogate pair, not the character itself.
        const string branch = "[{\"Type\":\"CredentialEcho\",\"Parameters\":{\"\\uD800\":\"x\"}}]";
        var workflow = Conditional(thenActions: branch);

        WebhookSigning.IsReadableBranch(branch).Should().BeFalse();

        WebhookSigning.ProtectSecrets(workflow, Protector()).Should().BeFalse();

        var response = WorkflowActionResponse.From(workflow.Actions.Single());
        response.UnreadableBranches.Should().HaveCount(1);
        response.UnreadableBranches.Should().Equal("ThenActions");
        response.Parameters.Should().NotContainKey("ThenActions");
    }

    [Fact]
    public void A_branch_named_in_another_casing_is_encrypted_when_saved_and_left_out_of_the_response()
    {
        var apiKey = NewApiKey();
        WorkflowDefinition Build()
        {
            var workflow = Conditional(thenActions: Branch(Child("CredentialEcho", new() { ["Channel"] = "ops-channel" })));
            workflow.Actions.Single().Parameters["elseActions"] =
                Branch(Child("CredentialEcho", new() { ["ApiKey"] = apiKey, ["Channel"] = "else-channel" }));
            return workflow;
        }

        var saved = Build();
        WebhookSigning.ProtectSecrets(saved, Protector()).Should().BeTrue();
        saved.Actions.Single().Parameters["elseActions"].Should().Contain("else-channel").And.NotContain(apiKey);

        var response = WorkflowActionResponse.From(Build().Actions.Single());
        response.Parameters.Should().ContainKey("elseActions");
        response.Parameters["elseActions"].Should().Contain("else-channel").And.NotContain(apiKey);
    }

    [Fact]
    public void A_credential_named_key_on_the_child_itself_is_encrypted_when_saved_and_left_out_of_the_response()
    {
        var apiKey = NewApiKey();
        var branch = "[{\"Type\":\"CredentialEcho\",\"ApiKey\":\"" + apiKey + "\",\"Parameters\":{\"Channel\":\"ops-channel\"}}]";

        var saved = Conditional(thenActions: branch);
        WebhookSigning.ProtectSecrets(saved, Protector()).Should().BeTrue();
        var stored = saved.Actions.Single().Parameters["ThenActions"];
        stored.Should().Contain("ops-channel").And.NotContain(apiKey);
        using (var storedBranch = JsonDocument.Parse(stored))
        {
            storedBranch.RootElement.GetArrayLength().Should().Be(1);
            Protector().Unprotect(storedBranch.RootElement[0].GetProperty("ApiKey").GetString()!).Should().Be(apiKey);
        }

        var response = WorkflowActionResponse.From(Conditional(thenActions: branch).Actions.Single());
        response.UnreadableBranches.Should().BeEmpty();
        response.Parameters.Should().ContainKey("ThenActions");
        response.Parameters["ThenActions"].Should().Contain("ops-channel").And.NotContain(apiKey);
    }

    [Fact]
    public void A_branch_sent_back_the_way_the_api_returned_it_is_still_readable()
    {
        var workflow = Conditional(thenActions: Branch(
            Child("Webhook", new() { ["Url"] = "https://hooks.example.com/child", ["Secret"] = NewSecret() }),
            Child("Conditional", new() { ["Condition"] = Condition, ["ThenActions"] = "[{\"Type\":" })));

        var returned = WorkflowActionResponse.From(workflow.Actions.Single()).Parameters;

        returned.Should().ContainKey("ThenActions");
        returned["ThenActions"].Should().Contain("SecretSet").And.Contain("UnreadableBranches");
        WebhookSigning.IsReadableBranch(returned["ThenActions"]).Should().BeTrue();
    }

    [Fact]
    public async Task The_startup_migration_logs_an_unreadable_stored_branch_by_name_and_not_by_value()
    {
        var secret = NewSecret();
        var workflow = Conditional(thenActions: BranchRepeatingParameters("Webhook", "Secret", secret, Guid.NewGuid().ToString()));

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession())
        {
            session.Store(workflow);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var logger = new CapturingLogger();
        var migration = new WorkflowCredentialMigrationService(
            store, Protector(), _fixture.Services.GetRequiredService<IConfiguration>(), logger);
        await migration.ProtectAllTenantsAsync(TestContext.Current.CancellationToken);

        logger.Lines.Should().NotBeEmpty();
        logger.Lines.Should().Contain(line => line.Contains(workflow.Id.ToString()) && line.Contains("ThenActions"));
        logger.Lines.Should().NotContain(line => line.Contains(secret));
    }

    [Fact]
    public async Task A_dry_run_reports_a_conditional_with_an_unreadable_branch_as_failed()
    {
        var secret = NewSecret();
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));

        var response = await client.PostAsJsonAsync("/api/workflows/dry-run", new
        {
            workflow = new
            {
                id = Guid.NewGuid(),
                name = "conditional-child-dry-run",
                triggerContentType = "article",
                triggerEvent = "Published",
                conditions = new Dictionary<string, string>(),
                actions = new[]
                {
                    new
                    {
                        type = "Conditional",
                        parameters = new Dictionary<string, string>
                        {
                            ["Condition"] = Condition,
                            ["ThenActions"] = BranchRepeatingParameters("Webhook", "Secret", secret, Guid.NewGuid().ToString()),
                        },
                    },
                },
            },
            sampleContent = new
            {
                id = Guid.NewGuid(),
                contentType = "article",
                status = 0,
                data = new Dictionary<string, object> { ["Title"] = "hello" },
                createdAt = DateTime.UtcNow,
                updatedAt = DateTime.UtcNow,
            },
        }, TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.IsSuccessStatusCode.Should().BeTrue("got {0}", response.StatusCode);
        body.Should().NotContain(secret);

        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("actions").GetArrayLength().Should().Be(1);
        var action = json.RootElement.GetProperty("actions")[0];
        action.GetProperty("success").GetBoolean().Should().BeFalse();
        action.GetProperty("errorMessage").GetString().Should().Contain("ThenActions");
    }

    private static async Task<string> PageHoldingAsync(HttpClient client, Guid id)
    {
        for (var page = 1; page <= 50; page++)
        {
            var response = await client.GetAsync($"/api/workflows?page={page}&pageSize=100", TestContext.Current.CancellationToken);
            response.IsSuccessStatusCode.Should().BeTrue("got {0}", response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            if (body.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase)) return body;

            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.GetProperty("hasNextPage").GetBoolean()) break;
        }

        throw new Xunit.Sdk.XunitException("the created workflow was not on any page of the list");
    }

    private sealed class CapturingLogger : ILogger<WorkflowCredentialMigrationService>
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public SingleClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }
}
