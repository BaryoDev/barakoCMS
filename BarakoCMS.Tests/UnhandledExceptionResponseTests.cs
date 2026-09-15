using System.Net;
using barakoCMS.Features.Monitoring.Meta;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// An exception nobody handled answers 500 without its message, and with the headers every other
/// response carries.
/// </summary>
[Collection("Sequential")]
public class UnhandledExceptionResponseTests
{
    private const string ThrowingPath = "/__test/unhandled-exception";
    private const string Marker = "marker-7f3c1a-internal-detail";

    private readonly IntegrationTestFixture _factory;

    public UnhandledExceptionResponseTests(IntegrationTestFixture factory) => _factory = factory;

    /// <summary>
    /// Appended after the whole application pipeline, so a request no endpoint matched reaches it
    /// inside the global exception handler, the way a throwing endpoint would.
    /// </summary>
    private sealed class ThrowingFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Use((HttpContext context, RequestDelegate _) =>
                context.Request.Path == ThrowingPath
                    ? throw new InvalidOperationException(Marker)
                    : Task.CompletedTask);
        };
    }

    [Fact]
    public async Task A_500_does_not_carry_the_exception_message_and_keeps_the_contract_headers()
    {
        using var host = _factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IStartupFilter, ThrowingFilter>()));
        using var client = host.CreateClient();

        var response = await client.GetAsync(ThrowingPath, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, body);
        body.Should().NotBeEmpty();
        body.Should().NotContain(Marker, "an exception message can name a table, a setting or request data");

        response.Headers.TryGetValues(ApiContract.HeaderName, out var contract).Should().BeTrue();
        contract!.Should().ContainSingle().Which.Should().Be(ApiContract.Version.ToString());
        response.Headers.TryGetValues("X-Content-Type-Options", out var nosniff).Should().BeTrue();
        nosniff!.Should().ContainSingle().Which.Should().Be("nosniff");
    }
}
