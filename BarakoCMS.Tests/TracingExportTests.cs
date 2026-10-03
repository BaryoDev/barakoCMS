using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using barakoCMS.Infrastructure.Tracing;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace BarakoCMS.Tests;

/// <summary>
/// Issue #691: a collector that does not answer costs spans, never the code that ends a span.
/// </summary>
/// <remarks>
/// The collector here is a loopback socket that takes the connection and never replies, which is
/// the slow case: an export to it waits out its whole timeout. Nothing leaves the machine.
///
/// The tracer is a real one with the production export configuration, but it hears a source this
/// test made up and nothing else, and it is disposed with the test. It never sees a request
/// another test is making. Building it runs the OpenTelemetry SDK's one-time initialiser, which
/// makes W3C the id format of every span in the process from then on; nothing in this suite
/// depends on the older format.
/// </remarks>
public class TracingExportTests
{
    private const int ExportTimeoutSeconds = 2;
    private const int Spans = 1000;

    /// <summary>The bound: a thousand spans end in under a second while one export alone waits two.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(1);

    [Fact]
    public void Ending_a_span_does_not_wait_for_a_collector_that_never_answers()
    {
        var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();

        try
        {
            var port = ((IPEndPoint)silent.LocalEndpoint).Port;
            var options = TracingOptions.FromConfiguration(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [TracingOptions.EndpointKey] = $"http://127.0.0.1:{port}",
                    [TracingOptions.TimeoutKey] = ExportTimeoutSeconds.ToString(),
                    // Small, so the queue is full long before the loop ends and most spans meet a
                    // full queue: the path where a blocking design would stall.
                    [TracingOptions.MaxQueueSizeKey] = "100",
                })
                .Build());

            var sourceName = "BarakoCMS.Tests.TracingExport." + Guid.NewGuid().ToString("N");
            using var source = new ActivitySource(sourceName);
            using var tracer = TracingSetup.Export(Sdk.CreateTracerProviderBuilder().AddSource(sourceName), options).Build();

            var recorded = 0;
            var clock = Stopwatch.StartNew();
            for (var i = 0; i < Spans; i++)
            {
                using var span = source.StartActivity("probe");
                if (span is { IsAllDataRequested: true }) recorded++;
            }

            clock.Stop();

            recorded.Should().Be(Spans, "every span was recorded and handed to the exporter, or the timing means nothing");
            clock.Elapsed.Should().BeLessThan(Bound,
                "one export to this collector waits {0} seconds, so a span end that waited for export "
              + "would take that long for a single span", ExportTimeoutSeconds);
        }
        finally
        {
            silent.Stop();
        }
    }
}
