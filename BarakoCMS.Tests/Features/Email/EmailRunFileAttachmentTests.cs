using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Models;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.Email;

/// <summary>
/// The Email action attaching a file an earlier step of the same run produced, which may be
/// private because the run made it and nobody chose it by writing a field.
/// </summary>
/// <remarks>
/// No database. The store holds public and private files and counts the questions it was asked,
/// so a refusal can be shown to have happened before the store was read. Every refusal is paired
/// with a count of what was sent and, where it fits, a control that sends the file the run did
/// produce.
/// </remarks>
public class EmailRunFileAttachmentTests
{
    private static readonly byte[] Order = "%PDF-1.4 purchase order"u8.ToArray();

    private sealed class RunFiles : IFileStore
    {
        private readonly Dictionary<Guid, (StoredFileInfo Info, byte[] Bytes, bool IsPublic)> _files = new();

        public int RunCalls { get; private set; }

        public Guid Add(string name, byte[] bytes, bool isPublic = false, long? recordedSize = null)
        {
            var id = Guid.NewGuid();
            _files[id] = (new StoredFileInfo
            {
                Id = id,
                FileName = name,
                ContentType = "application/pdf",
                Size = recordedSize ?? bytes.Length,
            }, bytes, isPublic);
            return id;
        }

        public Task<StoredFileInfo?> FindPublicAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_files.TryGetValue(id, out var f) && f.IsPublic ? f.Info : null);

        public Task<Stream?> OpenPublicAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(_files.TryGetValue(id, out var f) && f.IsPublic ? new MemoryStream(f.Bytes) : null);

        public Task<StoredFileInfo?> FindForRunAsync(Guid id, CancellationToken cancellationToken = default)
        {
            RunCalls++;
            return Task.FromResult(_files.TryGetValue(id, out var f) ? f.Info : null);
        }

        public Task<Stream?> OpenForRunAsync(Guid id, CancellationToken cancellationToken = default)
        {
            RunCalls++;
            return Task.FromResult<Stream?>(_files.TryGetValue(id, out var f) ? new MemoryStream(f.Bytes) : null);
        }
    }

    private static IConfiguration Limits(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    private static EmailAction Build(RecordingEmailService email, IFileStore files, IConfiguration? configuration = null) =>
        new(email, NullLogger<EmailAction>.Instance, tenant: null, files: files, configuration: configuration);

    private static Content Entry(params (string Field, object Value)[] data) => new()
    {
        Id = Guid.NewGuid(),
        ContentType = "purchase-order",
        Data = data.ToDictionary(d => d.Field, d => d.Value),
    };

    /// <summary>
    /// What the runner hands the email step: step 0 succeeded, captured <c>FileId</c> (when
    /// <paramref name="captured"/> is set) and produced the files listed.
    /// </summary>
    private static WorkflowContext Context(string attachments, Content entry, string? captured, params Guid[] produced) =>
        Context(attachments, entry, captured is null ? null : (0, captured), 0, produced);

    private static WorkflowContext Context(
        string attachments, Content entry, (int Step, string Value)? captured, int producedStep, params Guid[] produced) => new()
    {
        Parameters = new Dictionary<string, string>
        {
            ["To"] = "supplier@example.com",
            ["Subject"] = "Purchase order",
            ["Body"] = "<p>Attached.</p>",
            ["Attachments"] = attachments,
        },
        Content = entry,
        TriggerEvent = "Published",
        Outputs = captured is { } c
            ? new Dictionary<int, IReadOnlyDictionary<string, string>>
            {
                [c.Step] = new Dictionary<string, string> { ["FileId"] = c.Value },
            }
            : new Dictionary<int, IReadOnlyDictionary<string, string>>(),
        ProducedFiles = new Dictionary<int, IReadOnlyList<Guid>> { [producedStep] = produced },
    };

    private static Task<WorkflowActionResult> RunAsync(EmailAction action, WorkflowContext context) =>
        action.RunAsync(context, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_private_file_an_earlier_step_produced_is_attached()
    {
        var files = new RunFiles();
        var id = files.Add("po-1042.pdf", Order, isPublic: false);
        var recorder = new RecordingEmailService();

        var result = await RunAsync(
            Build(recorder, files), Context("{{actions.0.FileId}}", Entry(), id.ToString(), id));

        result.Succeeded.Should().BeTrue(result.Error ?? string.Empty);
        var sent = recorder.Messages.Should().ContainSingle().Which;
        sent.To.Should().Be("supplier@example.com");
        sent.Attachments.Should().HaveCount(1);
        sent.Attachments[0].FileName.Should().Be("po-1042.pdf");
        sent.Attachments[0].ContentType.Should().Be("application/pdf");
        sent.Attachments[0].Content.Should().Equal(Order);
    }

    [Fact]
    public async Task A_run_file_and_a_public_file_the_entry_names_are_attached_together()
    {
        var files = new RunFiles();
        var produced = files.Add("po.pdf", Order, isPublic: false);
        var terms = files.Add("terms.pdf", [1, 2, 3], isPublic: true);
        var recorder = new RecordingEmailService();

        var result = await RunAsync(
            Build(recorder, files),
            Context("{{actions.0.FileId}}, {{data.Terms}}", Entry(("Terms", terms.ToString())), produced.ToString(), produced));

        result.Succeeded.Should().BeTrue(result.Error ?? string.Empty);
        recorder.Messages.Should().ContainSingle().Which.Attachments.Select(a => a.FileName)
            .Should().BeEquivalentTo(["po.pdf", "terms.pdf"]);
    }

    [Fact]
    public async Task A_private_file_id_typed_into_a_field_is_still_refused()
    {
        var files = new RunFiles();
        var closed = files.Add("payroll.pdf", Order, isPublic: false);
        var produced = files.Add("po.pdf", Order, isPublic: false);
        var recorder = new RecordingEmailService();
        var entry = Entry(("File", closed.ToString()));

        var refused = await RunAsync(
            Build(recorder, files), Context("{{data.File}}", entry, produced.ToString(), produced));

        refused.Succeeded.Should().BeFalse();
        refused.Retryable.Should().BeFalse();
        refused.Error.Should().Contain("cannot be attached").And.NotContain(closed.ToString());
        recorder.Messages.Should().BeEmpty();
        files.RunCalls.Should().Be(0, "a file named by a field is never read as a run file");

        // The control: the file the run did produce goes out on the same action and store.
        var allowed = await RunAsync(
            Build(recorder, files), Context("{{actions.0.FileId}}", entry, produced.ToString(), produced));
        allowed.Succeeded.Should().BeTrue(allowed.Error ?? string.Empty);
        recorder.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task A_private_file_id_typed_into_the_parameter_is_refused()
    {
        var files = new RunFiles();
        var closed = files.Add("payroll.pdf", Order, isPublic: false);
        var produced = files.Add("po.pdf", Order, isPublic: false);
        var recorder = new RecordingEmailService();

        var refused = await RunAsync(
            Build(recorder, files),
            Context(closed.ToString(), Entry(("File", closed.ToString())), produced.ToString(), produced));

        refused.Succeeded.Should().BeFalse();
        refused.Error.Should().Contain("cannot be attached");
        recorder.Messages.Should().BeEmpty();
        files.RunCalls.Should().Be(0);
    }

    [Fact]
    public async Task An_output_that_names_a_file_the_run_did_not_produce_is_refused()
    {
        var files = new RunFiles();
        var elsewhere = files.Add("another-runs.pdf", Order, isPublic: false);
        var produced = files.Add("po.pdf", Order, isPublic: false);
        var recorder = new RecordingEmailService();

        // Step 0 captured the id of a file that exists, but its attempt recorded a different one.
        var refused = await RunAsync(
            Build(recorder, files), Context("{{actions.0.FileId}}", Entry(), elsewhere.ToString(), produced));

        refused.Succeeded.Should().BeFalse();
        refused.Retryable.Should().BeFalse();
        refused.Error.Should().Contain("cannot be attached").And.NotContain(elsewhere.ToString());
        recorder.Messages.Should().BeEmpty();
        files.RunCalls.Should().Be(0, "the store is not asked about a file this run did not make");
    }

    [Fact]
    public async Task A_file_recorded_for_a_different_step_is_not_read_through_this_one()
    {
        var files = new RunFiles();
        var produced = files.Add("po.pdf", Order, isPublic: false);
        var recorder = new RecordingEmailService();

        // Step 1 captured the id, but only step 0 produced a file.
        var refused = await RunAsync(
            Build(recorder, files),
            Context("{{actions.1.FileId}}", Entry(), (1, produced.ToString()), 0, produced));

        refused.Succeeded.Should().BeFalse();
        recorder.Messages.Should().BeEmpty();
        files.RunCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_file_the_store_does_not_hold_fails_the_step_visibly()
    {
        var files = new RunFiles();
        var gone = Guid.NewGuid();
        var recorder = new RecordingEmailService();

        var failed = await RunAsync(
            Build(recorder, files), Context("{{actions.0.FileId}}", Entry(), gone.ToString(), gone));

        failed.Succeeded.Should().BeFalse();
        failed.Retryable.Should().BeFalse();
        failed.Error.Should().Contain("Attachment 1").And.NotContain(gone.ToString());
        recorder.Messages.Should().BeEmpty("a message without its file is not a smaller success");
        files.RunCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_reference_to_an_output_the_step_did_not_keep_fails_and_can_be_tried_again()
    {
        var files = new RunFiles();
        var recorder = new RecordingEmailService();

        var failed = await RunAsync(
            Build(recorder, files), Context("{{actions.0.FileId}}", Entry(), captured: null));

        failed.Succeeded.Should().BeFalse();
        failed.Retryable.Should().BeTrue();
        failed.Error.Should().Contain("actions.0.FileId");
        recorder.Messages.Should().BeEmpty();
        files.RunCalls.Should().Be(0);
    }

    [Fact]
    public async Task An_output_that_is_not_a_file_id_is_refused()
    {
        var files = new RunFiles();
        var recorder = new RecordingEmailService();

        var refused = await RunAsync(
            Build(recorder, files), Context("{{actions.0.FileId}}", Entry(), "not-an-id"));

        refused.Succeeded.Should().BeFalse();
        refused.Retryable.Should().BeFalse();
        recorder.Messages.Should().BeEmpty();
        files.RunCalls.Should().Be(0);
    }

    [Fact]
    public async Task The_older_entry_point_cannot_read_step_outputs_and_refuses()
    {
        var files = new RunFiles();
        var produced = files.Add("po.pdf", Order, isPublic: false);
        var recorder = new RecordingEmailService();
        var context = Context("{{actions.0.FileId}}", Entry(), produced.ToString(), produced);

        var refused = await Build(recorder, files).RunAsync(
            context.Parameters, context.Content, TestContext.Current.CancellationToken);

        refused.Succeeded.Should().BeFalse();
        recorder.Messages.Should().BeEmpty();
        files.RunCalls.Should().Be(0);
    }

    [Fact]
    public async Task The_file_size_limit_applies_to_a_run_file()
    {
        var files = new RunFiles();
        var big = files.Add("big.pdf", new byte[2048], isPublic: false);
        var recorder = new RecordingEmailService();
        var limits = Limits((EmailAttachmentLimits.MaxFileBytesKey, "1024"));

        var refused = await RunAsync(
            Build(recorder, files, limits), Context("{{actions.0.FileId}}", Entry(), big.ToString(), big));

        refused.Succeeded.Should().BeFalse();
        refused.Error.Should().Contain("1024").And.Contain(EmailAttachmentLimits.MaxFileBytesKey);
        recorder.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task The_size_limit_holds_when_the_recorded_size_understates_the_run_file()
    {
        var files = new RunFiles();
        var liar = files.Add("liar.pdf", new byte[2048], isPublic: false, recordedSize: 10);
        var recorder = new RecordingEmailService();
        var limits = Limits((EmailAttachmentLimits.MaxFileBytesKey, "1024"));

        var refused = await RunAsync(
            Build(recorder, files, limits), Context("{{actions.0.FileId}}", Entry(), liar.ToString(), liar));

        refused.Succeeded.Should().BeFalse();
        refused.Error.Should().Contain(EmailAttachmentLimits.MaxFileBytesKey);
        recorder.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task The_count_limit_counts_run_files_with_the_rest()
    {
        var files = new RunFiles();
        var first = files.Add("one.pdf", [1], isPublic: false);
        var second = files.Add("two.pdf", [2], isPublic: true);
        var recorder = new RecordingEmailService();
        var limits = Limits((EmailAttachmentLimits.MaxCountKey, "1"));
        var entry = Entry(("Two", second.ToString()));

        var refused = await RunAsync(
            Build(recorder, files, limits),
            Context("{{actions.0.FileId}}, {{data.Two}}", entry, first.ToString(), first));

        refused.Succeeded.Should().BeFalse();
        refused.Error.Should().Contain(EmailAttachmentLimits.MaxCountKey);
        recorder.Messages.Should().BeEmpty();
        files.RunCalls.Should().Be(0, "the count is checked before any file is read");

        var allowed = await RunAsync(
            Build(recorder, files, limits), Context("{{actions.0.FileId}}", entry, first.ToString(), first));
        allowed.Succeeded.Should().BeTrue(allowed.Error ?? string.Empty);
    }

    [Fact]
    public async Task The_total_size_limit_counts_run_files_with_the_rest()
    {
        var files = new RunFiles();
        var first = files.Add("one.pdf", new byte[700], isPublic: false);
        var second = files.Add("two.pdf", new byte[700], isPublic: true);
        var recorder = new RecordingEmailService();
        var limits = Limits((EmailAttachmentLimits.MaxTotalBytesKey, "1000"));

        var refused = await RunAsync(
            Build(recorder, files, limits),
            Context("{{actions.0.FileId}}, {{data.Two}}", Entry(("Two", second.ToString())), first.ToString(), first));

        refused.Succeeded.Should().BeFalse();
        refused.Error.Should().Contain(EmailAttachmentLimits.MaxTotalBytesKey);
        recorder.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task A_retried_email_step_attaches_the_same_file()
    {
        var files = new RunFiles();
        var produced = files.Add("po.pdf", Order, isPublic: false);
        var recorder = new RecordingEmailService();
        var context = Context("{{actions.0.FileId}}", Entry(), produced.ToString(), produced);

        var first = await RunAsync(Build(recorder, files), context);
        var again = await RunAsync(Build(recorder, files), context);

        first.Succeeded.Should().BeTrue(first.Error ?? string.Empty);
        again.Succeeded.Should().BeTrue(again.Error ?? string.Empty);
        var sent = recorder.Messages.ToArray();
        sent.Should().HaveCount(2);
        sent[1].Attachments[0].Content.Should().Equal(sent[0].Attachments[0].Content);
        sent[1].Attachments[0].FileName.Should().Be(sent[0].Attachments[0].FileName);
    }
}
