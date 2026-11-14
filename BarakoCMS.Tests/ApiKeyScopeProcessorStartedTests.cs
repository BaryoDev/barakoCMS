using System.Security.Claims;
using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using NSubstitute;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// An earlier preprocessor may already have answered. The scope check must then return without
/// setting a status or writing a body, as the station scope check does.
/// </summary>
public class ApiKeyScopeProcessorStartedTests
{
    // Behaves like a real response once it has started: setting the status throws.
    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode
        {
            get => StatusCodes.Status200OK;
            set => throw new InvalidOperationException("StatusCode cannot be set because the response has already started.");
        }

        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = new MemoryStream();
        public bool HasStarted => true;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }

    [Fact]
    public async Task PreProcess_ResponseAlreadyStarted_ReturnsWithoutThrowingOrWriting()
    {
        var http = new DefaultHttpContext();
        http.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        http.Request.Method = HttpMethods.Get;
        http.Request.Path = "/api/users";
        // An API key with no scopes, on a route keys may not reach: unguarded, this is a 403.
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("auth_method", "apikey")], "test"));

        var context = Substitute.For<IPreProcessorContext>();
        context.HttpContext.Returns(http);

        var act = () => new ApiKeyScopeProcessor().PreProcessAsync(context, CancellationToken.None);

        await act.Should().NotThrowAsync();
        http.Response.Body.Length.Should().Be(0);
    }
}
