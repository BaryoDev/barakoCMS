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
/// <c>CreateTaskAction</c> with <c>Data.*</c> on a json field. Its parameters are text, so JSON text
/// for a field that holds a list or an object is stored as that list or object, never as the text.
/// </summary>
[Collection("Integration1")]
public class CreateTaskActionJsonTests(IntegrationTestFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("json", "[{\"Code\":\"PARA\",\"Quantity\":2}]", true)]
    [InlineData("object", "{\"Ward\":\"A\"}", false)]
    [InlineData("array", "[1,2]", true)]
    public async Task JSON_text_for_a_json_field_is_stored_as_the_list_or_object_it_holds(string fieldType, string value, bool list)
    {
        var (result, created) = await RunAsync(fieldType, value);

        result.Succeeded.Should().BeTrue(result.Error);
        if (list)
        {
            created!.Data["Body"].Should().BeAssignableTo<List<object>>();
        }
        else
        {
            created!.Data["Body"].Should().BeAssignableTo<Dictionary<string, object>>().Which["Ward"].Should().Be("A");
        }
    }

    [Fact]
    public async Task Text_that_is_not_json_for_a_json_field_fails_the_action_and_creates_nothing()
    {
        var (result, created) = await RunAsync("json", "not json");

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse("the same text is refused the same way on a retry");
        result.Error.Should().Contain("Body").And.NotContain("not json");
        created.Should().BeNull();
    }

    [Fact]
    public async Task A_text_field_keeps_JSON_looking_text_as_text()
    {
        var (result, created) = await RunAsync("string", "[1,2]");

        result.Succeeded.Should().BeTrue(result.Error);
        created!.Data["Body"].Should().Be("[1,2]");
    }

    [Fact]
    public async Task A_template_mixing_JSON_with_a_value_cannot_add_keys_to_a_json_field()
    {
        var source = new Content { Id = Guid.NewGuid(), Data = new() { ["Name"] = "x\",\"admin\":true,\"y\":\"" } };

        var (result, created) = await RunAsync("json", "{\"note\":\"{{data.Name}}\"}", source);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("Body").And.NotContain("admin");
        created.Should().BeNull();
    }

    private async Task<(WorkflowActionResult Result, Content? Created)> RunAsync(string fieldType, string value, Content? source = null)
    {
        var template = source is null ? "{{data.Src}}" : value;
        source ??= SourceHolding(value);
        var store = fixture.Services.GetRequiredService<IDocumentStore>();
        var tenant = "create-task-json-" + Guid.NewGuid().ToString("n")[..8];
        var type = "doc" + Guid.NewGuid().ToString("n")[..8];

        await using (var seed = store.LightweightSession(tenant))
        {
            seed.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = type,
                Fields = [new FieldDefinition { Name = "Body", DisplayName = "Body", Type = fieldType }],
            });
            await seed.SaveChangesAsync(Ct);
        }

        WorkflowActionResult result;
        await using (var session = store.LightweightSession(tenant))
        {
            var writer = new ContentWriter(session, new ContentSourcingPolicyService(session));
            var action = new CreateTaskAction(session, NullLogger<CreateTaskAction>.Instance, writer);

            var subject = source;
            subject.ContentType = "Order";
            subject.LastModifiedBy = Guid.NewGuid();
            result = await action.RunAsync(
                ActionParameters.Resolve(
                    "CreateTask",
                    new Dictionary<string, string> { ["ContentType"] = type, ["Title"] = "T", ["Data.Body"] = template },
                    subject),
                subject,
                Ct);
        }

        await using var read = store.QuerySession(tenant);
        return (result, await read.Query<Content>().FirstOrDefaultAsync(c => c.ContentType == type, Ct));
    }

    /// <summary>An entry whose Src field holds the list or object <paramref name="value"/> reads as, or the text if it is not one.</summary>
    private static Content SourceHolding(string value) => new()
    {
        Id = Guid.NewGuid(),
        Data = new() { ["Src"] = barakoCMS.Core.Validation.JsonTextFields.TryRead(value, out var read) ? read : value },
    };
}
