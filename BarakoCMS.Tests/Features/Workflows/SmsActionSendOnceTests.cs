using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// An SMS provider failure may be a message the provider took, and there is no key that makes a
/// second send safe, so the action sends once and the run records it as unknown.
/// </summary>
public class SmsActionSendOnceTests
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
    public async Task A_failed_sms_is_sent_once_and_ends_unknown()
    {
        var provider = new FailingSms();
        var action = new SmsAction(provider, NullLogger<SmsAction>.Instance);

        var send = () => action.RunAsync(
            new Dictionary<string, string> { ["To"] = "+15550100", ["Message"] = "hello" },
            new Content { Id = Guid.NewGuid() }, Ct);

        var thrown = await send.Should().ThrowAsync<SendOutcomeUnknownException>();
        thrown.Which.Message.Should().NotContain("dropped the line");
        provider.Calls.Should().Be(1);
    }
}
