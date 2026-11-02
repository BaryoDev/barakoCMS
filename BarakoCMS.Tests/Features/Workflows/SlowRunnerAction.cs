using System.Collections.Concurrent;
using System.Diagnostics;
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
///
/// A test that needs an action to outlive a stop makes the group's gauge itself, with
/// <see cref="Gauge.HoldAfterStop"/> set. The action then ignores the token until that task ends,
/// as a handler that does not watch its token would.
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
        catch (OperationCanceledException) when (gauge.HoldAfterStop is not null)
        {
            // Capped, so a test that fails before it lets go does not leave this in flight for good.
            await gauge.HoldAfterStop.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            throw;
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
        private readonly Dictionary<string, long> _startedAt = new();
        private readonly List<(long Start, long End)> _spans = [];

        /// <summary>When set, an action that is cancelled stays in flight until this ends.</summary>
        public Task? HoldAfterStop { get; init; }

        /// <summary>Called by each action as it starts, before it waits.</summary>
        public Action? OnEnter { get; init; }

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
                _startedAt[key] = Stopwatch.GetTimestamp();
            }

            OnEnter?.Invoke();
        }

        public void Leave(string key)
        {
            lock (_gate)
            {
                InFlight--;
                _events.Add($"end {key}");
                if (_startedAt.Remove(key, out var start)) _spans.Add((start, Stopwatch.GetTimestamp()));
            }
        }

        /// <summary>
        /// The time the actions ran added up, and the time at least one was running. The two are
        /// equal when they ran one at a time, and the second is smaller by the overlap. Neither
        /// counts the gaps between actions, which is where a loaded machine or a slow database spends its time.
        /// </summary>
        public (TimeSpan Busy, TimeSpan Covered) Time()
        {
            List<(long Start, long End)> spans;
            lock (_gate) spans = _spans.OrderBy(x => x.Start).ToList();

            long busy = 0, covered = 0, reached = long.MinValue;
            foreach (var (start, end) in spans)
            {
                busy += end - start;
                covered += Math.Max(0, end - Math.Max(start, reached));
                reached = Math.Max(reached, end);
            }

            return (Stopwatch.GetElapsedTime(0, busy), Stopwatch.GetElapsedTime(0, covered));
        }
    }
}
