using System.Collections.Concurrent;
using System.Globalization;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// An action that takes a known time and reports how many of it were running at once.
/// </summary>
/// <remarks>
/// The run record cannot show whether two actions overlapped, so the action counts it. Each test
/// names a group of its own in the parameters, and reads only that group's gauge. Registered on
/// <see cref="IntegrationTestFixture"/> for the same reason as <see cref="CountingRunnerAction"/>:
/// either hosted runner can claim the attempt.
/// </remarks>
internal sealed class SlowRunnerAction : barakoCMS.Features.Workflows.IWorkflowAction
{
    public const string ActionType = "SlowRunner";

    public const string DelayParameter = "DelayMs";

    public const string GroupParameter = "Group";

    public static readonly ConcurrentDictionary<string, Gauge> Groups = new();

    public string Type => ActionType;

    public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        RunAsync(parameters, content, ct);

    public async Task<barakoCMS.Features.Workflows.WorkflowActionResult> RunAsync(
        Dictionary<string, string> parameters, Content content, CancellationToken ct)
    {
        var gauge = Groups.GetOrAdd(parameters.GetValueOrDefault(GroupParameter) ?? string.Empty, _ => new Gauge());
        var key = parameters.GetValueOrDefault("IdempotencyKey") ?? string.Empty;
        var delay = int.Parse(parameters.GetValueOrDefault(DelayParameter) ?? "0", CultureInfo.InvariantCulture);

        gauge.Enter(key);

        try
        {
            await Task.Delay(delay, ct);
        }
        finally
        {
            gauge.Leave(key);
        }

        return barakoCMS.Features.Workflows.WorkflowActionResult.Success();
    }

    internal sealed class Gauge
    {
        private readonly object _gate = new();
        private readonly List<string> _events = [];
        private readonly Dictionary<string, int> _runs = new();

        public int InFlight { get; private set; }

        public int MostAtOnce { get; private set; }

        /// <summary>"start key" and "end key", in the order they happened.</summary>
        public IReadOnlyList<string> Events
        {
            get
            {
                lock (_gate) return _events.ToList();
            }
        }

        public int TimesRun(string key)
        {
            lock (_gate) return _runs.GetValueOrDefault(key);
        }

        public void Enter(string key)
        {
            lock (_gate)
            {
                InFlight++;
                MostAtOnce = Math.Max(MostAtOnce, InFlight);
                _runs[key] = _runs.GetValueOrDefault(key) + 1;
                _events.Add($"start {key}");
            }
        }

        public void Leave(string key)
        {
            lock (_gate)
            {
                InFlight--;
                _events.Add($"end {key}");
            }
        }
    }
}
