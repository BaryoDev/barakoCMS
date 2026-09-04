using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Models;
using FastEndpoints;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace BarakoCMS.Tests.Jobs;

/// <summary>
/// What the queue writes to the log about a request must not be something the request wrote.
/// A path or a verb is chosen by the caller, so a newline and an ANSI escape in it would forge a
/// second log line or repaint the terminal. The queue names the endpoint instead, and the one
/// value it does echo (a LogMessageCommand's text) is stripped of control characters first.
/// </summary>
public class JobQueueLoggingTests
{
    private const string Forged = "\r\n\u001b[31mFORGED";

    [Fact]
    public async Task A_discarded_job_is_logged_without_the_requests_method_or_path()
    {
        var (provider, http, log) = Provider();
        http.Request.Method = "POST" + Forged;
        http.Request.Path = "/api/anything" + Forged;
        var record = Record();

        await provider.StoreJobAsync(record, TestContext.Current.CancellationToken);
        await CompleteResponseAsync(http);

        log.Warnings.Should().ContainSingle();
        var line = log.Warnings[0];
        line.Should().Contain(record.TrackingID.ToString());
        line.Should().NotContain("\n").And.NotContain("\r").And.NotContain("\u001b");
        line.Should().NotContain("/api/anything", "the request's path is the caller's text, not ours");
    }

    [Fact]
    public async Task A_discarded_job_names_the_FastEndpoints_endpoint_that_queued_it()
    {
        var (provider, http, log) = Provider();
        http.Request.Path = "/api/anything" + Forged;
        var definition = new EndpointDefinition(typeof(ProbeEndpoint), typeof(EmptyRequest), typeof(object));
        http.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(definition), "probe"));

        await provider.StoreJobAsync(Record(), TestContext.Current.CancellationToken);
        await CompleteResponseAsync(http);

        log.Warnings.Should().ContainSingle();
        log.Warnings[0].Should().Contain(typeof(ProbeEndpoint).FullName!);
        log.Warnings[0].Should().NotContain("\u001b").And.NotContain("/api/anything");
    }

    [Fact]
    public async Task A_discarded_job_names_a_routed_endpoint_by_its_display_name()
    {
        var (provider, http, log) = Provider();
        http.Request.Path = "/api/anything" + Forged;
        http.SetEndpoint(new Endpoint(null, EndpointMetadataCollection.Empty, "HTTP: POST /api/things"));

        await provider.StoreJobAsync(Record(), TestContext.Current.CancellationToken);
        await CompleteResponseAsync(http);

        log.Warnings.Should().ContainSingle();
        log.Warnings[0].Should().Contain("HTTP: POST /api/things");
        log.Warnings[0].Should().NotContain("\u001b").And.NotContain("/api/anything");
    }

    [Fact]
    public async Task The_log_message_handler_strips_control_characters_from_its_message()
    {
        var log = new CapturingLogger<LogMessageCommandHandler>();
        var handler = new LogMessageCommandHandler(log);

        await handler.ExecuteAsync(new LogMessageCommand { Message = "hello" + Forged }, TestContext.Current.CancellationToken);

        log.Lines.Should().ContainSingle();
        log.Lines[0].Should().Contain("hello").And.Contain("FORGED");
        log.Lines[0].Should().NotContain("\n").And.NotContain("\r").And.NotContain("\u001b");
    }

    private static (MartenJobStorageProvider, DefaultHttpContext, CapturingLogger<MartenJobStorageProvider>) Provider()
    {
        var session = Substitute.For<IDocumentSession>();
        session.TenantId.Returns("tenant");
        session.Listeners.Returns(new List<IDocumentSessionListener>());

        var http = new DefaultHttpContext();
        http.Features.Set<IHttpResponseFeature>(new CompletionCapturingResponseFeature());
        http.RequestServices = new ServiceCollection().AddSingleton(session).BuildServiceProvider();
        http.Response.StatusCode = StatusCodes.Status200OK;

        var log = new CapturingLogger<MartenJobStorageProvider>();
        var provider = new MartenJobStorageProvider(
            Substitute.For<IDocumentStore>(),
            new HttpContextAccessor { HttpContext = http },
            new JobOptions(),
            log);

        return (provider, http, log);
    }

    private static JobRecord Record() => new()
    {
        TrackingID = Guid.NewGuid(),
        CommandType = typeof(LogMessageCommand).FullName!,
        Command = new LogMessageCommand { Message = "m" },
    };

    private static async Task CompleteResponseAsync(HttpContext http)
    {
        var feature = (CompletionCapturingResponseFeature)http.Features.Get<IHttpResponseFeature>()!;
        await feature.RunAsync();
    }

    /// <summary>
    /// Only a name for the definition to carry. A real Endpoint subclass here would be discovered
    /// by FastEndpoints when the integration host scans this assembly.
    /// </summary>
    private sealed class ProbeEndpoint;

    /// <summary>
    /// The default feature drops OnCompleted on the floor; this one keeps the callbacks so a test
    /// can play the end of the response.
    /// </summary>
    private sealed class CompletionCapturingResponseFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _completed = [];

        public override void OnCompleted(Func<object, Task> callback, object state) => _completed.Add((callback, state));

        public async Task RunAsync()
        {
            foreach (var (callback, state) in _completed)
                await callback(state);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            Lines.Add(line);
            if (logLevel == LogLevel.Warning)
                Warnings.Add(line);
        }
    }
}
