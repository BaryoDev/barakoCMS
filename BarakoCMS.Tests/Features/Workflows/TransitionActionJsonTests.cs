using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// <c>TransitionAction</c> with <c>Data.*</c> on a json field. Its parameters are text, so JSON text
/// for a field that holds a list or an object is sent as that list or object, never as the text.
/// </summary>
[Collection("Integration4")]
public class TransitionActionJsonTests(IntegrationTestFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("json", "[{\"Code\":\"PARA\",\"Quantity\":2}]", true)]
    [InlineData("object", "{\"Ward\":\"A\"}", false)]
    [InlineData("array", "[1,2]", true)]
    public async Task JSON_text_for_a_json_field_is_sent_as_the_list_or_object_it_holds(string fieldType, string value, bool list)
    {
        var (result, content) = await RunAsync(fieldType, value);

        result.Succeeded.Should().BeTrue(result.Error);
        content.LifecycleState.Should().Be("Done");
        if (list)
        {
            content.Data["Lines"].Should().BeAssignableTo<List<object>>();
        }
        else
        {
            content.Data["Lines"].Should().BeAssignableTo<Dictionary<string, object>>().Which["Ward"].Should().Be("A");
        }
    }

    [Fact]
    public async Task Text_that_is_not_json_for_a_json_field_fails_the_action_and_does_not_move_the_entry()
    {
        var (result, content) = await RunAsync("json", "not json");

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse("the same text is refused the same way on a retry");
        result.Error.Should().Contain("Lines").And.NotContain("not json");
        content.LifecycleState.Should().Be("Waiting");
    }

    [Fact]
    public async Task A_text_field_keeps_JSON_looking_text_as_text()
    {
        var (result, content) = await RunAsync("string", "[1,2]");

        result.Succeeded.Should().BeTrue(result.Error);
        content.Data["Lines"].Should().Be("[1,2]");
    }

    [Fact]
    public async Task A_template_mixing_JSON_with_a_value_cannot_add_keys_to_a_json_field()
    {
        var source = new Content { Id = Guid.NewGuid(), Data = new() { ["Name"] = "x\",\"admin\":true,\"y\":\"" } };

        var (result, content) = await RunAsync("json", "{\"note\":\"{{data.Name}}\"}", source);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("Lines").And.NotContain("admin");
        content.LifecycleState.Should().Be("Waiting");
    }

    private async Task<(WorkflowActionResult Result, Content Content)> RunAsync(string fieldType, string value, Content? source = null)
    {
        var template = source is null ? "{{data.Src}}" : value;
        source ??= SourceHolding(value);
        var type = "order-" + Guid.NewGuid().ToString("n")[..10];
        Guid id;
        using (var scope = fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = type,
                Fields =
                [
                    new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" },
                    new FieldDefinition { Name = "Lines", DisplayName = "Bill lines", Type = fieldType },
                ],
                Lifecycle = new LifecycleDefinition
                {
                    States = ["Waiting", "Done"],
                    InitialState = "Waiting",
                    Transitions = [new StateTransition { Name = "Call", From = "Waiting", To = "Done", OptionalFields = ["Lines"] }],
                },
            });
            await session.SaveChangesAsync(Ct);

            var created = await scope.ServiceProvider.GetRequiredService<IContentCreator>().StageAsync(
                new ContentCreateRequest { ContentType = type, Data = new() { ["Name"] = "A order" } },
                Guid.NewGuid(), batch: null, Ct);
            await session.SaveChangesAsync(Ct);
            id = created.Id;
        }

        WorkflowActionResult result;
        using (var scope = fixture.Services.CreateScope())
        {
            var action = ActivatorUtilities.CreateInstance<TransitionAction>(scope.ServiceProvider);
            var subject = source;
            subject.ContentType = type;
            result = await action.RunAsync(
                ActionParameters.Resolve(
                    "Transition",
                    new Dictionary<string, string>
                    {
                        ["Target"] = id.ToString(),
                        ["ContentType"] = type,
                        ["Transition"] = "Call",
                        ["Data.Lines"] = template,
                    },
                    subject),
                subject,
                Ct);
        }

        using var read = fixture.Services.CreateScope();
        return (result, (await read.ServiceProvider.GetRequiredService<IDocumentSession>().LoadAsync<Content>(id, Ct))!);
    }

    /// <summary>An entry whose Src field holds the list or object <paramref name="value"/> reads as, or the text if it is not one.</summary>
    private static Content SourceHolding(string value) => new()
    {
        Id = Guid.NewGuid(),
        Data = new() { ["Src"] = barakoCMS.Core.Validation.JsonTextFields.TryRead(value, out var read) ? read : value },
    };
}
