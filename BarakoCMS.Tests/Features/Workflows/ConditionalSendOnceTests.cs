using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// A child that sent a message and failed must not be carried on from. The Conditional hands the
/// unknown outcome up, so the runner ends it as unknown and the message is sent once.
/// </summary>
public class ConditionalSendOnceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FailingSms : ISmsService
    {
        public int Calls { get; private set; }

        public Task SendSmsAsync(string to, string message, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("The provider dropped the line after the message.");
        }
    }

    [Fact]
    public async Task A_conditional_whose_child_sms_fails_sends_once_and_hands_the_unknown_outcome_up()
    {
        var provider = new FailingSms();
        var children = new List<IWorkflowAction> { new SmsAction(provider, NullLogger<SmsAction>.Instance) };
        var services = new Mock<IServiceProvider>();
        services.Setup(x => x.GetService(typeof(IEnumerable<IWorkflowAction>))).Returns(children);
        var action = new ConditionalAction(services.Object, NullLogger<ConditionalAction>.Instance);

        var thenActions = System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new { Type = "SMS", Parameters = new Dictionary<string, string> { ["To"] = "+15550100", ["Message"] = "hello" } },
        });
        var parameters = new Dictionary<string, string>
        {
            ["Condition"] = "{{data.Priority}} == \"High\"",
            ["ThenActions"] = thenActions,
        };
        var content = new Content
        {
            Id = Guid.NewGuid(),
            Data = new Dictionary<string, object> { ["Priority"] = "High" },
        };

        var run = () => action.RunAsync(parameters, content, Ct);

        await run.Should().ThrowAsync<SendOutcomeUnknownException>();
        provider.Calls.Should().Be(1, "the runner does not run it again, so the one call is the only send");
    }
}
