using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.Email;

/// <summary>
/// Which stored files the Email action attaches, and what it does with the ones it will not.
/// </summary>
/// <remarks>
/// No database: the file store is a dictionary that hands a file over when it is public, and the
/// provider records what it was handed. Every refusal is paired with a count of what was sent,
/// because a refusal that still sends the message without its file is the failure these tests
/// exist to catch.
/// </remarks>
public class EmailActionAttachmentTests
{
    private static readonly byte[] Receipt = "%PDF-1.4 receipt"u8.ToArray();

    private const string Refused = "cannot be attached";

    private sealed class FakeFiles : IFileStore
    {
        private readonly Dictionary<Guid, (StoredFileInfo Info, byte[] Bytes, bool IsPublic)> _files = new();

        public int Calls { get; private set; }

        public Guid Add(
            string name, byte[] bytes, string contentType = "application/pdf", long? recordedSize = null, bool isPublic = true)
        {
            var id = Guid.NewGuid();
            _files[id] = (new StoredFileInfo
            {
                Id = id,
                FileName = name,
                ContentType = contentType,
                Size = recordedSize ?? bytes.Length,
            }, bytes, isPublic);
            return id;
        }

        private bool IsPublic(Guid id) => _files.TryGetValue(id, out var file) && file.IsPublic;

        public Task<StoredFileInfo?> FindPublicAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(IsPublic(id) ? _files[id].Info : null);
        }

        public Task<Stream?> OpenPublicAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<Stream?>(IsPublic(id) ? new MemoryStream(_files[id].Bytes) : null);
        }
    }

    private sealed class ThrowingFiles : IFileStore
    {
        public const string Secret = "bucket-name-nobody-should-read";

        public Task<StoredFileInfo?> FindPublicAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new IOException($"The store {Secret} is down.");

        public Task<Stream?> OpenPublicAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new IOException($"The store {Secret} is down.");
    }

    /// <summary>A provider written before attachments existed: it implements the one required member.</summary>
    private sealed class PlainProvider : IEmailService
    {
        public int Sent { get; private set; }

        public Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
        {
            Sent++;
            return Task.CompletedTask;
        }
    }

    /// <summary>A provider that does send attachments and throws the same exception type for a reason of its own.</summary>
    private sealed class PickyProvider : IEmailService
    {
        public Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SendEmailAsync(
            string to, string subject, string body, IReadOnlyList<EmailAttachment> attachments, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This relay does not take PDF files today.");
    }

    private static IConfiguration Limits(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    private static EmailAction Build(IEmailService email, IFileStore? files, IConfiguration? configuration = null) =>
        new(email, NullLogger<EmailAction>.Instance, tenant: null, files: files, configuration: configuration);

    private static Content Entry(params (string Field, object Value)[] data) => EntrySavedBy(Guid.NewGuid(), data);

    private static Content EntrySavedBy(Guid writer, params (string Field, object Value)[] data) => new()
    {
        Id = Guid.NewGuid(),
        ContentType = "registration",
        LastModifiedBy = writer,
        Data = data.ToDictionary(d => d.Field, d => d.Value),
    };

    private static Dictionary<string, string> Mail(string attachments, string key = "Attachments") => new()
    {
        ["To"] = "guest@example.com",
        ["Subject"] = "Your receipt",
        ["Body"] = "<p>Attached.</p>",
        [key] = attachments,
    };

    private static Dictionary<string, string> PlainMail()
    {
        var mail = Mail("unused");
        mail.Remove("Attachments");
        return mail;
    }

    private static Task<barakoCMS.Features.Workflows.WorkflowActionResult> RunAsync(
        EmailAction action, Dictionary<string, string> parameters, Content entry) =>
        action.RunAsync(parameters, entry, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_file_the_entry_names_is_attached_with_its_stored_name_type_and_bytes()
    {
        var files = new FakeFiles();
        var id = files.Add("receipt.pdf", Receipt);
        var recorder = new RecordingEmailService();

        var result = await RunAsync(Build(recorder, files), Mail("{{data.Receipt}}"), Entry(("Receipt", id.ToString())));

        result.Succeeded.Should().BeTrue(result.Error ?? string.Empty);
        var sent = recorder.Messages.Should().ContainSingle().Which;
        sent.To.Should().Be("guest@example.com");
        sent.Attachments.Should().HaveCount(1);
        sent.Attachments[0].FileName.Should().Be("receipt.pdf");
        sent.Attachments[0].ContentType.Should().Be("application/pdf");
        sent.Attachments[0].Content.Should().Equal(Receipt);
    }

    [Theory]
    [InlineData("/api/files/{0}")]
    [InlineData("/api/public/files/{0}?w=400")]
    [InlineData("https://cms.example.com/api/public/files/{0}")]
    public async Task A_file_the_entry_names_by_its_download_link_is_attached(string link)
    {
        var files = new FakeFiles();
        var id = files.Add("stub.pdf", Receipt);
        var recorder = new RecordingEmailService();

        var result = await RunAsync(
            Build(recorder, files), Mail("{{data.Stub}}"), Entry(("Stub", string.Format(link, id))));

        result.Succeeded.Should().BeTrue(result.Error ?? string.Empty);
        var sent = recorder.Messages.Should().ContainSingle().Which;
        sent.Attachments.Should().HaveCount(1);
        sent.Attachments[0].Content.Should().Equal(Receipt);
    }

    /// <summary>
    /// A list field as a stored entry holds it: a list of objects, which renders to text as its
    /// type name. The action reads the list from the entry instead of the rendered text.
    /// </summary>
    [Fact]
    public async Task Every_file_in_a_list_field_is_attached_in_order()
    {
        var files = new FakeFiles();
        var first = files.Add("one.pdf", [1, 2, 3]);
        var second = files.Add("two.pdf", [4, 5]);
        var recorder = new RecordingEmailService();

        var result = await RunAsync(
            Build(recorder, files),
            Mail("{{ data.Files }}"),
            Entry(("Files", new List<object> { first.ToString(), $"/api/files/{second}" })));

        result.Succeeded.Should().BeTrue(result.Error ?? string.Empty);
        var sent = recorder.Messages.Should().ContainSingle().Which;
        sent.Attachments.Should().HaveCount(2);
        sent.Attachments.Select(a => a.FileName).Should().Equal("one.pdf", "two.pdf");
    }

    [Fact]
    public async Task A_list_field_with_something_that_is_not_text_in_it_sends_nothing()
    {
        var files = new FakeFiles();
        var id = files.Add("one.pdf", Receipt);
        var recorder = new RecordingEmailService();

        var result = await RunAsync(
            Build(recorder, files), Mail("{{data.Files}}"), Entry(("Files", new List<object> { id.ToString(), 42L })));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("Attachment 2").And.Contain("not a file id");
        recorder.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task Two_placeholders_in_one_parameter_attach_both_files()
    {
        var files = new FakeFiles();
        var receipt = files.Add("receipt.pdf", Receipt);
        var stub = files.Add("stub.pdf", [9]);
        var recorder = new RecordingEmailService();

        var result = await RunAsync(
            Build(recorder, files),
            Mail("{{data.Receipt}}, {{data.Stub}}"),
            Entry(("Receipt", receipt.ToString()), ("Stub", stub.ToString())));

        result.Succeeded.Should().BeTrue(result.Error ?? string.Empty);
        recorder.Messages.Should().ContainSingle().Which.Attachments.Select(a => a.FileName)
            .Should().Equal("receipt.pdf", "stub.pdf");
    }

    /// <summary>
    /// An id typed into the workflow. The file exists and is public; the only thing wrong is that
    /// this entry does not name it.
    /// </summary>
    [Fact]
    public async Task A_file_the_entry_does_not_name_is_refused_and_nothing_is_sent()
    {
        var files = new FakeFiles();
        var mine = files.Add("mine.pdf", Receipt);
        var theirs = files.Add("theirs.pdf", "%PDF-1.4 somebody else's"u8.ToArray());
        var recorder = new RecordingEmailService();
        var entry = Entry(("Receipt", mine.ToString()));

        var refused = await RunAsync(Build(recorder, files), Mail(theirs.ToString()), entry);

        refused.Succeeded.Should().BeFalse();
        refused.Retryable.Should().BeFalse();
        refused.Error.Should().Contain(Refused);
        refused.Error.Should().NotContain(theirs.ToString(), "the reason names the position, not the value it was handed");
        recorder.Messages.Should().BeEmpty("a message without its file is not a smaller success");
        files.Calls.Should().Be(0, "the store is not asked about a file the entry does not name");

        // The control: the same action, store and entry attach the file the entry does name.
        var allowed = await RunAsync(Build(recorder, files), Mail(mine.ToString()), entry);
        allowed.Succeeded.Should().BeTrue(allowed.Error ?? string.Empty);
        recorder.Messages.Should().ContainSingle().Which.Attachments.Should().HaveCount(1);
    }

    /// <summary>
    /// The entry names the file through the templated field, so naming it decides nothing. What
    /// refuses it is that the file is not public, whoever is recorded as having saved the entry.
    /// </summary>
    [Fact]
    public async Task A_private_file_the_entry_names_is_refused_whoever_saved_the_entry()
    {
        var files = new FakeFiles();
        var closed = files.Add("receipt.pdf", Receipt, isPublic: false);
        var open = files.Add("terms.pdf", Receipt);
        var recorder = new RecordingEmailService();

        foreach (var writer in new[] { Guid.NewGuid(), Guid.Empty })
        {
            var refused = await RunAsync(
                Build(recorder, files), Mail("{{data.File}}"), EntrySavedBy(writer, ("File", closed.ToString())));

            refused.Succeeded.Should().BeFalse();
            refused.Retryable.Should().BeFalse();
            refused.Error.Should().Contain(Refused);
            refused.Error.Should().NotContain(closed.ToString());
        }

        recorder.Messages.Should().BeEmpty();

        // The control: the same parameter on an entry naming a public file sends it, with a user
        // recorded and with none.
        foreach (var writer in new[] { Guid.NewGuid(), Guid.Empty })
        {
            var allowed = await RunAsync(
                Build(recorder, files), Mail("{{data.File}}"), EntrySavedBy(writer, ("File", open.ToString())));
            allowed.Succeeded.Should().BeTrue(allowed.Error ?? string.Empty);
        }

        recorder.Messages.Should().HaveCount(2);
        recorder.Messages.Should().OnlyContain(m => m.Attachments.Count == 1);
    }

    /// <summary>
    /// Not named, not there and not public all give the same reason, so a run cannot be used to
    /// learn which files exist.
    /// </summary>
    [Fact]
    public async Task Every_refusal_of_a_file_gives_the_same_reason()
    {
        var files = new FakeFiles();
        var mine = files.Add("mine.pdf", Receipt);
        var closed = files.Add("closed.pdf", Receipt, isPublic: false);
        var missing = Guid.NewGuid();
        var recorder = new RecordingEmailService();

        var notNamed = await RunAsync(Build(recorder, files), Mail(mine.ToString()), Entry(("Receipt", missing.ToString())));
        var notThere = await RunAsync(Build(recorder, files), Mail("{{data.Receipt}}"), Entry(("Receipt", missing.ToString())));
        var notPublic = await RunAsync(Build(recorder, files), Mail("{{data.Receipt}}"), Entry(("Receipt", closed.ToString())));

        notNamed.Succeeded.Should().BeFalse();
        notNamed.Error.Should().Contain(Refused);
        notThere.Error.Should().Be(notNamed.Error);
        notPublic.Error.Should().Be(notNamed.Error);
        notThere.Retryable.Should().BeFalse();
        notPublic.Retryable.Should().BeFalse();
        recorder.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task One_refused_file_in_a_list_sends_nothing_rather_than_the_rest()
    {
        var files = new FakeFiles();
        var mine = files.Add("mine.pdf", Receipt);
        var theirs = files.Add("theirs.pdf", Receipt, isPublic: false);
        var recorder = new RecordingEmailService();

        var result = await RunAsync(
            Build(recorder, files),
            Mail("{{data.Files}}"),
            Entry(("Files", new List<object> { mine.ToString(), theirs.ToString() })));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("Attachment 2");
        recorder.Messages.Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "names no file")]
    [InlineData("   ", "names no file")]
    [InlineData("{{data.Missing}}", "not a file id")]
    [InlineData("receipt.pdf", "not a file id")]
    [InlineData("3fa85f6457174562b3fc2c963f66afa6", "not a file id")]
    [InlineData("3fa85f64-5717-4562-b3fc-2c963f66afa6,", "names no file")]
    public async Task A_parameter_that_names_no_file_fails_the_action_instead_of_sending_without_one(string value, string reason)
    {
        var recorder = new RecordingEmailService();

        var result = await RunAsync(
            Build(recorder, new FakeFiles()), Mail(value), Entry(("Receipt", "3fa85f64-5717-4562-b3fc-2c963f66afa6")));

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain(reason);
        recorder.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_field_fails_the_action_instead_of_sending_without_the_file()
    {
        var recorder = new RecordingEmailService();

        foreach (var entry in new[] { Entry(("Receipt", string.Empty)), Entry(("Receipt", new List<object>())) })
        {
            var result = await RunAsync(Build(recorder, new FakeFiles()), Mail("{{data.Receipt}}"), entry);

            result.Succeeded.Should().BeFalse();
            result.Retryable.Should().BeFalse();
        }

        recorder.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task The_parameter_is_read_whatever_case_it_was_written_in()
    {
        var files = new FakeFiles();
        var id = files.Add("receipt.pdf", Receipt);
        var recorder = new RecordingEmailService();

        var result = await RunAsync(
            Build(recorder, files), Mail("{{data.Receipt}}", key: "attachments"), Entry(("Receipt", id.ToString())));

        result.Succeeded.Should().BeTrue(result.Error ?? string.Empty);
        recorder.Messages.Should().ContainSingle().Which.Attachments.Should().HaveCount(1);
    }

    [Fact]
    public async Task The_parameter_written_twice_in_different_case_fails_the_action()
    {
        var files = new FakeFiles();
        var id = files.Add("receipt.pdf", Receipt);
        var entry = Entry(("Receipt", id.ToString()));
        var recorder = new RecordingEmailService();

        var twice = Mail("{{data.Receipt}}");
        twice["attachments"] = "{{data.Receipt}}";
        var result = await RunAsync(Build(recorder, files), twice, entry);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("declared more than once");
        recorder.Messages.Should().BeEmpty();

        // The control: written once, the same parameter sends the file.
        (await RunAsync(Build(recorder, files), Mail("{{data.Receipt}}"), entry)).Succeeded.Should().BeTrue();
        recorder.Messages.Should().ContainSingle().Which.Attachments.Should().HaveCount(1);
    }

    [Fact]
    public async Task More_files_than_the_count_limit_fails_naming_the_limit()
    {
        var files = new FakeFiles();
        var ids = Enumerable.Range(0, 3).Select(i => files.Add($"{i}.pdf", [(byte)i])).ToList();
        var entry = Entry(("Files", string.Join(" ", ids)));
        var limits = Limits((EmailAttachmentLimits.MaxCountKey, "2"));
        var recorder = new RecordingEmailService();

        var over = await RunAsync(Build(recorder, files, limits), Mail(string.Join(",", ids)), entry);

        over.Succeeded.Should().BeFalse();
        over.Retryable.Should().BeFalse();
        over.Error.Should().Contain(EmailAttachmentLimits.MaxCountKey).And.Contain("3").And.Contain("2");
        recorder.Messages.Should().BeEmpty("two of three is a dropped attachment");

        var within = await RunAsync(Build(recorder, files, limits), Mail(string.Join(",", ids.Take(2))), entry);
        within.Succeeded.Should().BeTrue(within.Error ?? string.Empty);
        recorder.Messages.Should().ContainSingle().Which.Attachments.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_default_count_limit_is_five()
    {
        var files = new FakeFiles();
        var ids = Enumerable.Range(0, 6).Select(i => files.Add($"{i}.pdf", [(byte)i])).ToList();
        var entry = Entry(("Files", string.Join(" ", ids)));
        var recorder = new RecordingEmailService();

        var six = await RunAsync(Build(recorder, files), Mail(string.Join(",", ids)), entry);
        six.Succeeded.Should().BeFalse();
        six.Error.Should().Contain(EmailAttachmentLimits.MaxCountKey);
        recorder.Messages.Should().BeEmpty();

        var five = await RunAsync(Build(recorder, files), Mail(string.Join(",", ids.Take(5))), entry);
        five.Succeeded.Should().BeTrue(five.Error ?? string.Empty);
        recorder.Messages.Should().ContainSingle().Which.Attachments.Should().HaveCount(5);
    }

    [Fact]
    public async Task A_file_over_the_size_limit_fails_naming_the_limit()
    {
        var files = new FakeFiles();
        var big = files.Add("big.pdf", new byte[11]);
        var small = files.Add("small.pdf", new byte[10]);
        var entry = Entry(("Files", $"{big} {small}"));
        var limits = Limits((EmailAttachmentLimits.MaxFileBytesKey, "10"));
        var recorder = new RecordingEmailService();

        var over = await RunAsync(Build(recorder, files, limits), Mail(big.ToString()), entry);

        over.Succeeded.Should().BeFalse();
        over.Retryable.Should().BeFalse();
        over.Error.Should().Contain(EmailAttachmentLimits.MaxFileBytesKey).And.Contain("10");
        recorder.Messages.Should().BeEmpty();
        files.Calls.Should().Be(1, "a record over the limit is refused before the store is asked for its bytes");

        var within = await RunAsync(Build(recorder, files, limits), Mail(small.ToString()), entry);
        within.Succeeded.Should().BeTrue(within.Error ?? string.Empty);
        recorder.Messages.Should().ContainSingle().Which.Attachments.Should().HaveCount(1);
    }

    /// <summary>
    /// The limit is on the bytes the stream holds, not only on the size the record claims.
    /// </summary>
    [Fact]
    public async Task A_file_larger_than_its_record_says_is_still_held_to_the_size_limit()
    {
        var files = new FakeFiles();
        var lying = files.Add("lying.pdf", new byte[500], recordedSize: 1);
        var recorder = new RecordingEmailService();

        var result = await RunAsync(
            Build(recorder, files, Limits((EmailAttachmentLimits.MaxFileBytesKey, "10"))),
            Mail(lying.ToString()),
            Entry(("Receipt", lying.ToString())));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain(EmailAttachmentLimits.MaxFileBytesKey);
        recorder.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task Files_over_the_total_limit_together_fail_naming_the_limit()
    {
        var files = new FakeFiles();
        var first = files.Add("one.pdf", new byte[6]);
        var second = files.Add("two.pdf", new byte[6]);
        var entry = Entry(("Files", $"{first} {second}"));
        var limits = Limits((EmailAttachmentLimits.MaxTotalBytesKey, "10"));
        var recorder = new RecordingEmailService();

        var over = await RunAsync(Build(recorder, files, limits), Mail($"{first},{second}"), entry);

        over.Succeeded.Should().BeFalse();
        over.Retryable.Should().BeFalse();
        over.Error.Should().Contain(EmailAttachmentLimits.MaxTotalBytesKey).And.Contain("10");
        recorder.Messages.Should().BeEmpty();

        var within = await RunAsync(Build(recorder, files, limits), Mail(first.ToString()), entry);
        within.Succeeded.Should().BeTrue(within.Error ?? string.Empty);
        recorder.Messages.Should().ContainSingle().Which.Attachments.Should().HaveCount(1);
    }

    /// <summary>
    /// Each record says one byte, so the records pass every limit. Only the count of bytes actually
    /// read can refuse these, and each file alone is within the per-file limit.
    /// </summary>
    [Fact]
    public async Task Files_whose_records_understate_them_are_still_held_to_the_total_limit()
    {
        var files = new FakeFiles();
        var first = files.Add("one.pdf", new byte[6], recordedSize: 1);
        var second = files.Add("two.pdf", new byte[6], recordedSize: 1);
        var entry = Entry(("Files", $"{first} {second}"));
        var limits = Limits((EmailAttachmentLimits.MaxTotalBytesKey, "10"), (EmailAttachmentLimits.MaxFileBytesKey, "8"));
        var recorder = new RecordingEmailService();

        var over = await RunAsync(Build(recorder, files, limits), Mail($"{first},{second}"), entry);

        over.Succeeded.Should().BeFalse();
        over.Retryable.Should().BeFalse();
        over.Error.Should().Contain(EmailAttachmentLimits.MaxTotalBytesKey);
        recorder.Messages.Should().BeEmpty();

        var within = await RunAsync(Build(recorder, files, limits), Mail(first.ToString()), entry);
        within.Succeeded.Should().BeTrue(within.Error ?? string.Empty);
        recorder.Messages.Should().ContainSingle().Which.Attachments.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(EmailAttachmentLimits.MaxCountKey)]
    [InlineData(EmailAttachmentLimits.MaxFileBytesKey)]
    [InlineData(EmailAttachmentLimits.MaxTotalBytesKey)]
    public async Task Zero_in_a_limit_turns_attachments_off_and_leaves_plain_email_alone(string key)
    {
        var files = new FakeFiles();
        var id = files.Add("receipt.pdf", Receipt);
        var limits = Limits((key, "0"));
        var recorder = new RecordingEmailService();

        var withFile = await RunAsync(
            Build(recorder, files, limits), Mail("{{data.Receipt}}"), Entry(("Receipt", id.ToString())));

        withFile.Succeeded.Should().BeFalse();
        withFile.Retryable.Should().BeFalse();
        withFile.Error.Should().Contain(key);
        recorder.Messages.Should().BeEmpty();

        var withoutFile = await RunAsync(Build(recorder, files, limits), PlainMail(), Entry());
        withoutFile.Succeeded.Should().BeTrue(withoutFile.Error ?? string.Empty);
        recorder.Messages.Should().ContainSingle().Which.Attachments.Should().BeEmpty();
    }

    [Theory]
    [InlineData("ten megabytes")]
    [InlineData("-1")]
    [InlineData("1.5")]
    public async Task A_limit_that_is_not_a_whole_number_fails_attachments_and_leaves_plain_email_alone(string value)
    {
        var files = new FakeFiles();
        var id = files.Add("receipt.pdf", Receipt);
        var limits = Limits((EmailAttachmentLimits.MaxFileBytesKey, value));
        var recorder = new RecordingEmailService();

        var withFile = await RunAsync(Build(recorder, files, limits), Mail(id.ToString()), Entry(("Receipt", id.ToString())));

        withFile.Succeeded.Should().BeFalse();
        withFile.Error.Should().Contain(EmailAttachmentLimits.MaxFileBytesKey);
        withFile.Error.Should().NotContain(value);
        recorder.Messages.Should().BeEmpty();

        var withoutFile = await RunAsync(Build(recorder, files, limits), PlainMail(), Entry());

        withoutFile.Succeeded.Should().BeTrue(withoutFile.Error ?? string.Empty);
        recorder.Messages.Should().ContainSingle().Which.Attachments.Should().BeEmpty();
    }

    /// <summary>
    /// The stored name came from whoever uploaded the file and lands in a header of the message.
    /// </summary>
    [Fact]
    public async Task A_stored_name_and_type_reach_the_provider_without_control_characters_or_a_path()
    {
        var files = new FakeFiles();
        var id = files.Add(
            "../../etc/re\r\nBcc: someone@evil.example\u0000ce\u202Eipt.pdf", Receipt, contentType: "text/html\r\nX-Injected: 1");
        var recorder = new RecordingEmailService();

        var result = await RunAsync(Build(recorder, files), Mail(id.ToString()), Entry(("Receipt", id.ToString())));

        result.Succeeded.Should().BeTrue(result.Error ?? string.Empty);
        var sent = recorder.Messages.Should().ContainSingle().Which;
        sent.Attachments.Should().HaveCount(1);
        sent.Attachments[0].FileName.Should().Be("reBcc: someone@evil.exampleceipt.pdf");
        sent.Attachments[0].ContentType.Should().Be("application/octet-stream");
    }

    [Fact]
    public async Task A_name_with_nothing_left_after_cleaning_becomes_a_fixed_name()
    {
        var files = new FakeFiles();
        var id = files.Add("\r\n\t", Receipt);
        var recorder = new RecordingEmailService();

        var result = await RunAsync(Build(recorder, files), Mail(id.ToString()), Entry(("Receipt", id.ToString())));

        result.Succeeded.Should().BeTrue(result.Error ?? string.Empty);
        var sent = recorder.Messages.Should().ContainSingle().Which;
        sent.Attachments.Should().HaveCount(1);
        sent.Attachments[0].FileName.Should().Be("attachment");
    }

    [Fact]
    public async Task Without_a_module_that_stores_files_the_action_fails_and_names_the_module()
    {
        var id = Guid.NewGuid();
        var recorder = new RecordingEmailService();

        foreach (var files in new IFileStore?[] { null, new NoFileStore() })
        {
            var result = await RunAsync(Build(recorder, files), Mail(id.ToString()), Entry(("Receipt", id.ToString())));

            result.Succeeded.Should().BeFalse();
            result.Retryable.Should().BeFalse();
            result.Error.Should().Contain("BarakoCMS.Files");
        }

        recorder.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task The_default_file_store_refuses_every_read_and_names_the_module()
    {
        var store = new NoFileStore();

        var find = () => store.FindPublicAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        var open = () => store.OpenPublicAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        (await find.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("BarakoCMS.Files");
        (await open.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("BarakoCMS.Files");
    }

    /// <summary>
    /// A provider that predates attachments must not send the message with its files missing.
    /// </summary>
    [Fact]
    public async Task A_provider_that_cannot_send_attachments_fails_for_good_and_sends_nothing()
    {
        var files = new FakeFiles();
        var id = files.Add("receipt.pdf", Receipt);
        var provider = new PlainProvider();

        var result = await RunAsync(Build(provider, files), Mail(id.ToString()), Entry(("Receipt", id.ToString())));

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("does not send attachments");
        provider.Sent.Should().Be(0);

        // The control: the same provider still sends an email that names no attachment.
        (await RunAsync(Build(provider, files), PlainMail(), Entry())).Succeeded.Should().BeTrue();
        provider.Sent.Should().Be(1);
    }

    /// <summary>
    /// Only the default members' own refusal means "cannot send attachments". A provider that does
    /// send them and throws the same exception type for its own reason is an ordinary failure.
    /// </summary>
    [Fact]
    public async Task A_providers_own_not_supported_exception_is_an_ordinary_retryable_failure()
    {
        var files = new FakeFiles();
        var id = files.Add("receipt.pdf", Receipt);

        var result = await RunAsync(Build(new PickyProvider(), files), Mail(id.ToString()), Entry(("Receipt", id.ToString())));

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeTrue();
        result.Error.Should().Contain("NotSupportedException");
        result.Error.Should().NotContain("does not send attachments");
        result.Error.Should().NotContain("PDF files today", "a provider's message does not go on the run");
    }

    [Fact]
    public async Task A_store_that_fails_is_a_retryable_failure_that_does_not_repeat_what_the_store_said()
    {
        var id = Guid.NewGuid();
        var recorder = new RecordingEmailService();

        var result = await RunAsync(
            Build(recorder, new ThrowingFiles()), Mail(id.ToString()), Entry(("Receipt", id.ToString())));

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeTrue("a store that is down may be back for the next attempt");
        result.Error.Should().Contain("IOException");
        result.Error.Should().NotContain(ThrowingFiles.Secret);
        recorder.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task With_no_email_provider_an_attachment_is_not_read_and_the_failure_says_so()
    {
        var files = new FakeFiles();
        var id = files.Add("receipt.pdf", Receipt);
        var mock = new MockEmailService(NullLogger<MockEmailService>.Instance);

        var result = await RunAsync(Build(mock, files), Mail(id.ToString()), Entry(("Receipt", id.ToString())));

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("No email provider is configured");
        files.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("3fa85f64-5717-4562-b3fc-2c963f66afa6", true)]
    [InlineData("/api/files/3fa85f64-5717-4562-b3fc-2c963f66afa6", true)]
    [InlineData("/api/public/files/3FA85F64-5717-4562-B3FC-2C963F66AFA6?w=400", true)]
    [InlineData("/api/files/3fa85f64-5717-4562-b3fc-2c963f66afa6/meta", true)]
    [InlineData("/api/contents/3fa85f64-5717-4562-b3fc-2c963f66afa6", false)]
    [InlineData("/api/files/3fa85f64-5717-4562-b3fc-2c963f66afa6extra", false)]
    [InlineData("3fa85f6457174562b3fc2c963f66afa6", false)]
    [InlineData("", false)]
    public void A_file_id_is_read_from_the_id_or_a_files_link_and_nothing_else(string item, bool expected)
    {
        EmailAttachments.TryReadFileId(item, out var id).Should().Be(expected);

        if (expected)
        {
            id.Should().Be(Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6"));
        }
    }

    [Fact]
    public void An_entry_names_a_file_through_text_at_any_depth_of_its_data()
    {
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var nested = System.Text.Json.JsonSerializer.SerializeToElement(
            new { documents = new[] { new { url = $"/api/files/{id.ToString().ToUpperInvariant()}" } } });

        EmailAttachments.References(Entry(("Receipt", id.ToString())), id).Should().BeTrue();
        EmailAttachments.References(Entry(("Meta", nested)), id).Should().BeTrue();
        EmailAttachments.References(Entry(("Docs", new List<object> { new Dictionary<string, object> { ["file"] = id.ToString() } })), id)
            .Should().BeTrue();

        EmailAttachments.References(Entry(("Receipt", id.ToString())), other).Should().BeFalse();
        EmailAttachments.References(Entry(("Meta", nested)), other).Should().BeFalse();
        EmailAttachments.References(Entry(("Count", 3), ("Paid", true)), id).Should().BeFalse();
        EmailAttachments.References(Entry(), id).Should().BeFalse();
    }
}
