using barakoCMS.Features.Workflows;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// An action that produces a file the way a document step would: it returns the file's id as an
/// output and declares the file as one it produced. A second type returns the same kind of output
/// without declaring a file, the way a third party's response could.
/// </summary>
/// <remarks>Registered on <see cref="IntegrationTestFixture"/>, since either hosted runner can claim it.</remarks>
internal sealed class FileRunnerAction : IWorkflowAction
{
    public string Type => "FileRunner";

    public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        throw new NotSupportedException("The runner calls RunAsync(WorkflowContext).");

    public Task<WorkflowActionResult> RunAsync(WorkflowContext context, CancellationToken ct)
    {
        var id = FileIdFor(context.Parameters.GetValueOrDefault("IdempotencyKey") ?? string.Empty);
        return Task.FromResult(WorkflowActionResult.Success(new Dictionary<string, string> { ["FileId"] = id.ToString() })
            with { ProducedFiles = [id] });
    }

    /// <summary>The id this action reports for an attempt, so a test can say what it expects.</summary>
    public static Guid FileIdFor(string idempotencyKey) =>
        new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(idempotencyKey)));
}

/// <summary>Declares more files than a step may keep, so the runner has to drop some and say so.</summary>
internal sealed class ManyFilesRunnerAction : IWorkflowAction
{
    public static readonly int Declared = WorkflowActionResult.MaxProducedFiles + 5;

    public string Type => "ManyFilesRunner";

    public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        throw new NotSupportedException("The runner calls RunAsync(WorkflowContext).");

    public Task<WorkflowActionResult> RunAsync(WorkflowContext context, CancellationToken ct) =>
        Task.FromResult(WorkflowActionResult.Success() with
        {
            ProducedFiles = [.. Enumerable.Range(0, Declared).Select(_ => Guid.NewGuid())],
        });
}

/// <summary>
/// Stores a real private file through the host's file store and declares it, the way a document
/// step would, so a later Email can attach it end to end.
/// </summary>
internal sealed class SavedFileRunnerAction(barakoCMS.Core.Interfaces.IFileStore files) : IWorkflowAction
{
    public string Type => "SavedFileRunner";

    public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        throw new NotSupportedException("The runner calls RunAsync(WorkflowContext).");

    public async Task<WorkflowActionResult> RunAsync(WorkflowContext context, CancellationToken ct)
    {
        using var bytes = new MemoryStream(FileSamples.Png(1, 2, 3, 4));
        var saved = await files.SaveAsync(
            new barakoCMS.Core.Interfaces.FileToStore
            {
                Content = bytes,
                FileName = "po-1042.png",
                ContentType = "image/png",
            }, ct);

        var file = saved.File ?? throw new InvalidOperationException(saved.Refused);
        return WorkflowActionResult.Success(new Dictionary<string, string> { ["FileId"] = file.Id.ToString() })
            with { ProducedFiles = [file.Id] };
    }
}

/// <summary>Returns a file id as an output and does not declare it as a file the step produced.</summary>
internal sealed class UndeclaredFileRunnerAction : IWorkflowAction
{
    public static readonly Guid SomeoneElsesFile = Guid.Parse("0b0f4f55-62a4-4f0e-9c4f-2a8f4a3f9b11");

    public string Type => "UndeclaredFileRunner";

    public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        throw new NotSupportedException("The runner calls RunAsync(WorkflowContext).");

    public Task<WorkflowActionResult> RunAsync(WorkflowContext context, CancellationToken ct) =>
        Task.FromResult(WorkflowActionResult.Success(new Dictionary<string, string> { ["FileId"] = SomeoneElsesFile.ToString() }));
}

/// <summary>A custom action that sets ProducedFiles to null, which the runner has to take as none.</summary>
internal sealed class NullFilesRunnerAction : IWorkflowAction
{
    public string Type => "NullFilesRunner";

    public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        throw new NotSupportedException("The runner calls RunAsync(WorkflowContext).");

    public Task<WorkflowActionResult> RunAsync(WorkflowContext context, CancellationToken ct) =>
        Task.FromResult(WorkflowActionResult.Success() with { ProducedFiles = null! });
}
