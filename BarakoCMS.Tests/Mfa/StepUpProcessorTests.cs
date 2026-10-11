using System.Security.Claims;
using barakoCMS.Infrastructure.Auth.Mfa;
using FastEndpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace BarakoCMS.Tests.Mfa;

/// <summary>
/// Stations and API keys cannot reach the step-up test endpoint through the host (their own gates
/// refuse it first, and a station needs an endpoint that opts in), so the processor is called directly.
/// </summary>
[Collection("Sequential")]
public class StepUpProcessorTests
{
    private static async Task<DefaultHttpContext> RunAsync(string authMethod)
    {
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("UserId", Guid.NewGuid().ToString()), new Claim("auth_method", authMethod)], "test"));
        var endpoint = new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new StepUpRequired()), "test");
        http.SetEndpoint(endpoint);

        var context = Substitute.For<IPreProcessorContext>();
        context.HttpContext.Returns(http);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JWT:Key"] = "test-super-secret-key-that-is-at-least-32-chars-long",
        }).Build();

        await new StepUpProcessor(config).PreProcessAsync(context, CancellationToken.None);
        return http;
    }

    [Theory]
    [InlineData("apikey")]
    [InlineData("station")]
    public async Task StepUpProcessor_ApiKeyOrStation_Returns403WithoutRetryHeader(string authMethod)
    {
        var http = await RunAsync(authMethod);

        http.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        http.Response.Headers.ContainsKey(StepUp.RequiredHeader).Should().BeFalse();
    }
}
