using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// Issue #765: every parameter the API hides as a credential is stored encrypted, not only Secret.
/// </summary>
[Collection("Sequential")]
public class WorkflowCredentialEncryptionTests
{
    private readonly IntegrationTestFixture _fixture;

    public WorkflowCredentialEncryptionTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private ISecretProtector Protector() => _fixture.Services.GetRequiredService<ISecretProtector>();

    private static string NewPlaintext() => "ak_live_" + Guid.NewGuid().ToString("N");

    private async Task<string> StoredJsonAsync(Guid id)
    {
        await using var session = _fixture.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var json = await session.Json.FindByIdAsync<WorkflowDefinition>(id, TestContext.Current.CancellationToken);
        json.Should().NotBeNull();
        return json!;
    }

    [Fact]
    public async Task A_credential_named_parameter_is_stored_encrypted_when_a_workflow_is_created()
    {
        var plaintext = NewPlaintext();
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));

        var response = await client.PostAsJsonAsync("/api/workflows", new
        {
            name = "credential-" + Guid.NewGuid().ToString("N"),
            triggerContentType = "article",
            triggerEvent = "Published",
            actions = new[]
            {
                new { type = "CredentialEcho", parameters = new Dictionary<string, string> { ["ApiKey"] = plaintext, ["Channel"] = "#ops" } },
            },
        }, TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", response.StatusCode, body);
        var id = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();

        var json = await StoredJsonAsync(id);
        json.Should().Contain("#ops", "the stored document is the one this workflow was saved as");
        json.Should().NotContain(plaintext);

        await using var session = _fixture.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var stored = await session.LoadAsync<WorkflowDefinition>(id, TestContext.Current.CancellationToken);
        Protector().Unprotect(stored!.Actions.Single().Parameters["ApiKey"]).Should().Be(plaintext);
    }

    [Fact]
    public async Task A_workflow_stored_in_clear_before_the_upgrade_is_encrypted_in_place_once()
    {
        var apiKey = NewPlaintext();
        var password = NewPlaintext();
        var id = Guid.NewGuid();
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();

        await using (var session = store.LightweightSession())
        {
            session.Store(new WorkflowDefinition
            {
                Id = id,
                Name = "legacy-" + id.ToString("N"),
                TriggerContentType = "article",
                TriggerEvent = "Published",
                Actions =
                [
                    new WorkflowAction
                    {
                        Type = "CredentialEcho",
                        Parameters = new Dictionary<string, string> { ["ApiKey"] = apiKey, ["Password"] = password, ["Channel"] = "#ops" },
                    },
                ],
            });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        (await StoredJsonAsync(id)).Should().Contain(apiKey, "the document has to start out in clear for this to be an upgrade");

        var migration = new WorkflowCredentialMigrationService(
            store, Protector(), _fixture.Services.GetRequiredService<ILogger<WorkflowCredentialMigrationService>>());
        await migration.ProtectAllTenantsAsync(TestContext.Current.CancellationToken);

        var afterFirst = await StoredJsonAsync(id);
        afterFirst.Should().NotContain(apiKey);
        afterFirst.Should().NotContain(password);
        afterFirst.Should().Contain("#ops");

        await migration.ProtectAllTenantsAsync(TestContext.Current.CancellationToken);
        (await StoredJsonAsync(id)).Should().Be(afterFirst, "an envelope is not encrypted a second time");

        await using var check = store.QuerySession();
        var stored = await check.LoadAsync<WorkflowDefinition>(id, TestContext.Current.CancellationToken);
        Protector().Unprotect(stored!.Actions.Single().Parameters["ApiKey"]).Should().Be(apiKey);
    }

    [Fact]
    public async Task An_action_is_handed_the_decrypted_credential_by_the_runner()
    {
        var plaintext = NewPlaintext();
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentId = Guid.NewGuid();
        var run = new WorkflowRun
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            WorkflowName = "Credential decrypt",
            ContentId = contentId,
            ContentType = "article",
            TriggerEvent = "Published",
            TriggeringEventSequence = 1,
            Actions =
            [
                new WorkflowActionAttempt
                {
                    Ordinal = 0,
                    ActionType = "CredentialEcho",
                    IdempotencyKey = Guid.NewGuid().ToString("N"),
                    // What a run queued from an encrypted definition copies.
                    Parameters = new Dictionary<string, string> { ["ApiKey"] = Protector().Protect(plaintext) },
                },
            ],
        };

        await using (var session = store.LightweightSession())
        {
            session.Store(new Content { Id = contentId, ContentType = "article", Status = ContentStatus.Published });
            run.Recompute();
            session.Store(run);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var runner = new WorkflowRunner(
            _fixture.Services,
            _fixture.Services.GetRequiredService<ILogger<WorkflowRunner>>(),
            _fixture.Services.GetRequiredService<IConfiguration>());

        var key = run.Id.ToString();
        for (var polls = 0; polls < 200 && !CredentialEchoAction.ReceivedByRun.ContainsKey(key); polls++)
        {
            if (!await runner.RunOnceAsync(TestContext.Current.CancellationToken))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
            }
        }

        CredentialEchoAction.ReceivedByRun.Should().ContainKey(key);
        CredentialEchoAction.ReceivedByRun[key].Should().Be(plaintext);
    }

    [Fact]
    public void A_credential_the_key_cannot_decrypt_names_the_parameter_and_not_the_value()
    {
        var otherKey = new SecretProtector(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Secrets:Key"] = "a-different-key-that-is-at-least-32-characters" })
            .Build());
        var envelope = otherKey.Protect("ak_live_rotated");

        var (_, error) = WebhookSigning.UnprotectCredentials(
            new Dictionary<string, string> { ["ApiKey"] = envelope }, Protector());

        error.Should().NotBeNull();
        error.Should().Contain("ApiKey");
        error.Should().NotContain("ak_live_rotated");
    }

    [Fact]
    public void Secret_stays_ciphertext_and_a_value_in_clear_passes_through()
    {
        var secretEnvelope = Protector().Protect("whsec_value");

        var (parameters, error) = WebhookSigning.UnprotectCredentials(new Dictionary<string, string>
        {
            ["Secret"] = secretEnvelope,
            ["Token"] = "typed-before-the-upgrade",
        }, Protector());

        error.Should().BeNull();
        parameters.Should().HaveCount(2);
        parameters["Secret"].Should().Be(secretEnvelope, "Webhook decrypts its own Secret");
        parameters["Token"].Should().Be("typed-before-the-upgrade");
    }
}
