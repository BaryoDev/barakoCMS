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
/// No database: the file store is a dictionary and the provider records what it was handed. Every
/// refusal is paired with a count of what was sent, because a refusal that still sends the message
/// without its file is the failure these tests exist to catch.
/// </remarks>
public class EmailActionAttachmentTests
{
    private static readonly byte[] Receipt = "%PDF-1.4 receipt"u8.ToArray();

    private sealed class FakeFiles : IFileStore
    {
        private readonly Dictionary<Guid, (StoredFileInfo Info, byte[] Bytes)> _files = new();

        public int Calls { get; private set; }

        public Guid Add(string name, byte[] bytes, string contentType = "application/pdf", long? recordedSize = null)
        {
            var id = Guid.NewGuid();
            _files[id] = (new StoredFileInfo
            {
                Id = id,
                FileName = name,
                ContentType = contentType,
                Size = recordedSize ?? bytes.Length,
            }, bytes);
            return id;
        }

        public Task<StoredFileInfo?> FindAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_files.TryGetValue(id, out var file) ? file.Info : null);
        }

        public Task<Stream?> OpenReadAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<Stream?>(_files.TryGetValue(id, out var file) ? new MemoryStream(file.Bytes) : null);
        }
    }

    private sealed class ThrowingFiles : IFileStore
    {
        public const string Secret = "bucket-name-nobody-should-read";

        public Task<StoredFileInfo?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new IOException($"The store {Secret} is down.");

        public Task<Stream?> OpenReadAsync(Guid id, CancellationToken cancellationToken = default) =>
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

    private static IConfiguration Limits(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    private static EmailAction Build(IEmailService email, IFileStore? files, IConfiguration? configuration = null) =>
        new(email, NullLogger<EmailAction>.Instance, tenant: null, files: files, configuration: configuration);

    private static Content Entry(params (string Field, object Value)[] data) => new()
    {
        Id = Guid.NewGuid(),
        ContentType = "registration",
        Data = data.ToDictionary(d => d.Field, d => d.Value),
    };

    private static Dictionary<string, string> Mail(string attachments, string key = "Attachments") => new()
    {
        ["To"] = "guest@example.com",
        ["Subject"] = "Your receipt",
        ["Body"] = "<p>Attached.</p>",
        [key] = attachments,
    };

    private static Task<barakoCMS.Features.Workflows.WorkflowActionResult> RunAsync(
        EmailAction action, Dictionary<string, string> parameters, Content entry) =>
        action.RunAsync(parameters, entry, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_file_the_entry_names_is_attached_with_its_stored_name_type_and_bytes()
    {
        var files = new FakeFiles();
        var id = files.Add("receipt.pdf", Receipt);
        var recorder = new RecordingEmailService();

        var result = await RunAsync(Build(recorder, files), Mail(id.ToString()), Entry(("Receipt", id.ToString())));

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
        var value = string.Format(link, id);
        var recorder = new RecordingEmailService();

        var result = await RunAsync(Build(recorder, files), Mail(value), Entry(("Stub", value)));

        result.Succeeded.Should().BeTrue(result.Error ?? string.Empty);
        var sent = recorder.Messages.Should().ContainSingle().Which;
        sent.Attachments.Should().HaveCount(1);
        sent.Attachments[0].Content.Should().Equal(Receipt);
    }

    [Fact]
    public async Task Two_files_in_a_list_field_are_both_attached_in_order()
    {
        var files = new FakeFiles();
        var first = files.Add("one.pdf", [1, 2, 3]);
        var second = files.Add("two.pdf", [4, 5]);
        var list = System.Text.Json.JsonSerializer.SerializeToElement(new[] { first.ToString(), second.ToString() });
        var recorder = new RecordingEmailService();

        // What {{data.Files}} resolves to for a list field: the JSON text of the array.
        var result = await RunAsync(Build(recorder, files), Mail(list.ToString()), Entry(("Files", list)));

        result.Succeeded.Should().BeTrue(result.Error ?? string.Empty);
        var sent = recorder.Messages.Should().ContainSingle().Which;
        sent.Attachments.Should().HaveCount(2);
        sent.Attachments.Select(a => a.FileName).Should().Equal("one.pdf", "two.pdf");
    }

    /// <summary>
    /// The file exists and the tenant is right. The only thing wrong is that this entry does not
    /// name it, which is what stops a workflow mailing out a file that belongs to another entry.
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
        refused.Error.Should().Contain("not a file of this entry");
        refused.Error.Should().NotContain(theirs.ToString(), "the reason names the position, not the value it was handed");
        recorder.Messages.Should().BeEmpty("a message without its file is not a smaller success");
        files.Calls.Should().Be(0, "the store is not asked about a file the entry does not name");

        // The control: the same action, store and entry attach the file the entry does name.
        var allowed = await RunAsync(Build(recorder, files), Mail(mine.ToString()), entry);
        allowed.Succeeded.Should().BeTrue(allowed.Error ?? string.Empty);
        recorder.Messages.Should().ContainSingle().Which.Attachments.Should().HaveCount(1);
    }

    [Fact]
    public async Task One_refused_file_in_a_list_sends_nothing_rather_than_the_rest()
    {
        var files = new FakeFiles();
        var mine = files.Add("mine.pdf", Receipt);
        var theirs = files.Add("theirs.pdf", Receipt);
        var recorder = new RecordingEmailService();

        var result = await RunAsync(
            Build(recorder, files), Mail($"{mine}, {theirs}"), Entry(("Receipt", mine.ToString())));

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("Attachment 2");
        recorder.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_the_store_does_not_have_is_refused_and_nothing_is_sent()
    {
        var files = new FakeFiles();
        var missing = Guid.NewGuid();
        var recorder = new RecordingEmailService();

        var result = await RunAsync(Build(recorder, files), Mail(missing.ToString()), Entry(("Receipt", missing.ToString())));

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("not a stored file of this tenant");
        files.Calls.Should().BeGreaterThan(0, "this refusal is the store's answer, not the entry check's");
        recorder.Messages.Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "names no file")]
    [InlineData("   ", "names no file")]
    [InlineData("{{data.Receipt}}", "not a file id")]
    [InlineData("receipt.pdf", "not a file id")]
    [InlineData("3fa85f6457174562b3fc2c963f66afa6", "not a file id")]
    public async Task A_value_that_names_no_file_fails_the_action_instead_of_sending_without_one(string value, string reason)
    {
        var recorder = new RecordingEmailService();

        var result = await RunAsync(Build(recorder, new FakeFiles()), Mail(value), Entry(("Receipt", value)));

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain(reason);
        recorder.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task The_parameter_is_read_whatever_case_it_was_written_in()
    {
        var files = new FakeFiles();
        var id = files.Add("receipt.pdf", Receipt);
        var recorder = new RecordingEmailService();

        var result = await RunAsync(
            Build(recorder, files), Mail(id.ToString(), key: "attachments"), Entry(("Receipt", id.ToString())));

        result.Succeeded.Should().BeTrue(result.Error ?? string.Empty);
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

        var within = await RunAsync(Build(recorder, files, limits), Mail(small.ToString()), entry);
        within.Succeeded.Should().BeTrue(within.Error ?? string.Empty);
        recorder.Messages.Should().ContainSingle().Which.Attachments.Should().HaveCount(1);
    }

    /// <summary>
    /// The limit is on the bytes read, not only on the size the record claims.
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

        var plain = Mail("unused");
        plain.Remove("Attachments");
        var withoutFile = await RunAsync(Build(recorder, files, limits), plain, Entry());

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

        var find = () => store.FindAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        var open = () => store.OpenReadAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

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
        var plain = Mail("unused");
        plain.Remove("Attachments");
        (await RunAsync(Build(provider, files), plain, Entry())).Succeeded.Should().BeTrue();
        provider.Sent.Should().Be(1);
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
