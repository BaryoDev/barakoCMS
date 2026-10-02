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

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public SingleClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }
}
