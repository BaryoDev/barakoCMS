using System.Security.Claims;
using barakoCMS.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace BarakoCMS.Tests.Features.EmailTemplates;

/// <summary>The preview's limit is counted per signed-in user, with nothing configured.</summary>
public class EmailPreviewRateLimitTests
{
    private static readonly Endpoint Preview = new(
        _ => Task.CompletedTask,
        new EndpointMetadataCollection(new EnableRateLimitingAttribute(RateLimitSetup.EmailPreviewPolicy)),
        "preview");

    private static HttpContext Request(Guid user)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(RateLimitAfterAuthentication.UserClaim, user.ToString())], "test")),
        };
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(Preview);
        return context;
    }

    /// <summary>
    /// Red while the policy was counted per IP before authentication: with nothing configured this
    /// middleware held no limiter, so it passed every request through.
    /// </summary>
    [Fact]
    public async Task A_user_past_the_window_is_refused_and_another_user_is_not()
    {
        var passed = 0;
        var middleware = new RateLimitAfterAuthentication(
            _ => { passed++; return Task.CompletedTask; },
            new ConfigurationBuilder().Build(),
            Mock.Of<IHostApplicationLifetime>(l => l.ApplicationStopped == CancellationToken.None));

        var first = Guid.NewGuid();
        for (var i = 0; i < RateLimitSetup.EmailPreview.PermitLimit; i++)
        {
            await middleware.InvokeAsync(Request(first));
        }

        var refused = Request(first);
        await middleware.InvokeAsync(refused);
        var other = Request(Guid.NewGuid());
        await middleware.InvokeAsync(other);

        passed.Should().Be(RateLimitSetup.EmailPreview.PermitLimit + 1);
        refused.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        other.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }
}
