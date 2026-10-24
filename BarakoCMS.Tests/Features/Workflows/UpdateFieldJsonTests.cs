using barakoCMS.Events;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// <c>UpdateFieldAction</c> on a json field. Its parameter is text, so JSON text for a field that
/// holds a list or an object is stored as that list or object, never as the text.
/// </summary>
[Collection("Integration1")]
public class UpdateFieldJsonTests(IntegrationTestFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("json", "[{\"Code\":\"PARA\",\"Quantity\":2}]", true)]
    [InlineData("object", "{\"Ward\":\"A\"}", false)]
    [InlineData("array", "[1,2]", true)]
    public async Task JSON_text_for_a_json_field_is_stored_as_the_list_or_object_it_holds(string fieldType, string value, bool list)
    {
        var (result, content) = await RunAsync(fieldType, value);

        result.Succeeded.Should().BeTrue(result.Error);
        if (list)
        {
            content.Data["Body"].Should().BeAssignableTo<List<object>>();
        }
        else
        {
            content.Data["Body"].Should().BeAssignableTo<Dictionary<string, object>>().Which["Ward"].Should().Be("A");
        }
    }

    [Fact]
    public async Task Text_that_is_not_json_for_a_json_field_fails_the_action_and_leaves_the_entry_as_it_was()
    {
        var (result, content) = await RunAsync("json", "not json");

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse("the same text is refused the same way on a retry");
        result.Error.Should().Contain("Body").And.NotContain("not json");
        content.Data.Should().NotContainKey("Body");
    }

    [Fact]
    public async Task A_text_field_keeps_JSON_looking_text_as_text()
    {
        var (result, content) = await RunAsync("string", "[1,2]");

        result.Succeeded.Should().BeTrue(result.Error);
        content.Data["Body"].Should().Be("[1,2]");
    }

    [Fact]
    public async Task A_template_mixing_JSON_with_a_value_cannot_add_keys_to_a_json_field()
    {
        var source = new Content { Id = Guid.NewGuid(), Data = new() { ["Name"] = "x\",\"admin\":true,\"y\":\"" } };
        var parameters = ActionParameters.Resolve(
            "UpdateField",
            new Dictionary<string, string> { ["Field"] = "data.Body", ["Value"] = "{\"note\":\"{{data.Name}}\"}" },
            source);

        var (result, content) = await RunAsync("json", parameters);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("Body").And.NotContain("admin");
        content.Data.Should().NotContainKey("Body");
    }

    private Task<(WorkflowActionResult Result, Content Content)> RunAsync(string fieldType, string value) =>
        RunAsync(fieldType, ActionParameters.Resolve(
            "UpdateField",
            new Dictionary<string, string> { ["Field"] = "data.Body", ["Value"] = "{{data.Src}}" },
            SourceHolding(value)));

    /// <summary>An entry whose Src field holds the list or object <paramref name="value"/> reads as, or the text if it is not one.</summary>
    private static Content SourceHolding(string value) => new()
    {
        Id = Guid.NewGuid(),
        Data = new() { ["Src"] = barakoCMS.Core.Validation.JsonTextFields.TryRead(value, out var read) ? read : value },
    };

    private async Task<(WorkflowActionResult Result, Content Content)> RunAsync(string fieldType, Dictionary<string, string> parameters)
    {
        var store = fixture.Services.GetRequiredService<IDocumentStore>();
        var tenant = "update-field-json-" + Guid.NewGuid().ToString("n")[..8];
        var type = "doc" + Guid.NewGuid().ToString("n")[..8];
        var contentId = Guid.NewGuid();

        await using (var seed = store.LightweightSession(tenant))
        {
            seed.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = type,
                Fields = [new FieldDefinition { Name = "Body", DisplayName = "Body", Type = fieldType }],
            });

            var seedWriter = new ContentWriter(seed, new ContentSourcingPolicyService(seed));
            await seedWriter.CreateAsync(
                new ContentCreated(
                    contentId, type, new Dictionary<string, object> { ["Title"] = "Acme" }, ContentStatus.Draft,
                    Guid.NewGuid(), null, SensitivityLevel.Public, DateTime.UtcNow),
                Ct);
            await seed.SaveChangesAsync(Ct);
        }

        WorkflowActionResult result;
        await using (var session = store.LightweightSession(tenant))
        {
            var writer = new ContentWriter(session, new ContentSourcingPolicyService(session));
            var action = new UpdateFieldAction(
                session, writer, new ContentLifecycleRunner([], session), NullLogger<UpdateFieldAction>.Instance);

            result = await action.RunAsync(
                parameters,
                new Content { Id = contentId, LastModifiedBy = Guid.NewGuid() },
                Ct);
        }

        await using var read = store.QuerySession(tenant);
        return (result, (await read.LoadAsync<Content>(contentId, Ct))!);
    }
}
