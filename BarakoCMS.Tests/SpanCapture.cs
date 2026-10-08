using System.Collections.Concurrent;
using System.Diagnostics;
using barakoCMS.Infrastructure.Tracing;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace BarakoCMS.Tests;

/// <summary>
/// A real tracer with the production scrubber in front of an exporter that keeps what it is given,
/// so a test sees exactly what would have been exported.
/// </summary>
/// <remarks>
/// The sources are process-wide, so other tests running at the same time can add spans here too.
/// Every assertion picks its own spans out by an id the test made up.
/// </remarks>
internal sealed class SpanCapture : IDisposable
{
    private readonly TracerProvider _tracer;
    private readonly Exporter _exporter = new();

    public SpanCapture(params string[] sources)
    {
        _tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource(sources)
            .AddProcessor(new SpanScrubber())
            .AddProcessor(new SimpleActivityExportProcessor(_exporter))
            .Build();
    }

    /// <summary>The spans exported so far.</summary>
    public IReadOnlyList<Activity> Exported => _exporter.Spans.ToList();

    public void Dispose() => _tracer.Dispose();

    private sealed class Exporter : BaseExporter<Activity>
    {
        public ConcurrentQueue<Activity> Spans { get; } = new();

        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var span in batch) Spans.Enqueue(span);
            return ExportResult.Success;
        }
    }
}
