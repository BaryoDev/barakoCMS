using System.Security.Claims;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;

namespace BarakoCMS.Tests;

/// <summary>
/// What the entry validator lets a caller put in a <c>file</c> field, against a file store held in
/// memory and with no database: the schema is handed in, and it has no slug or reference field, so
/// the validator never opens its session.
/// </summary>
/// <remarks>
/// Every refusal is paired with a caller or a file that is accepted, so a validator that refused
/// everything would fail here as one that accepted everything would.
/// </remarks>
public class FileFieldWriteTests
{
    private const string Type = "gallery";

    private static readonly ContentTypeDefinition Schema = new()
    {
        Id = Guid.NewGuid(),
        Name = Type,
        DisplayName = "Gallery",
        Fields =
        [
            new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
            new FieldDefinition { Name = "Cover", DisplayName = "Cover", Type = "file" },
        ],
    };

    /// <summary>The user a write is made for.</summary>
    private static ClaimsPrincipal? From(ClaimsPrincipal user) => user;

    /// <summary>A write no user makes, such as a job or a system actor.</summary>
    private static ClaimsPrincipal? NoRequest() => null;

    private static Task<(bool IsValid, List<string> Errors)> WriteAsync(
        IFileStore? files, ClaimsPrincipal? caller, object? cover, Content? existing = null)
    {
        var data = new Dictionary<string, object> { ["Title"] = "a" };
        if (cover is not null) data["Cover"] = cover;

        return new ContentValidatorService(null!, files)
            .ValidateFieldsAsync(Schema, Type, data, existing, caller);
    }

    private static Content Holding(object cover) => new()
    {
        Id = Guid.NewGuid(),
        ContentType = Type,
        Data = new Dictionary<string, object> { ["Title"] = "a", ["Cover"] = cover },
    };

    [Fact]
    public async Task The_owner_attaches_a_private_file_and_anyone_attaches_a_public_one()
    {
        var files = new FakeFileStore();
        var owner = Guid.NewGuid();
        var mine = files.Add(isPublic: false, owner: owner);
        var open = files.Add(isPublic: true, owner: owner);

        (await WriteAsync(files, From(FakeFileStore.User(owner)), mine.ToString())).IsValid.Should().BeTrue();
        (await WriteAsync(files, From(FakeFileStore.User(Guid.NewGuid())), open.ToString())).IsValid.Should().BeTrue();
        (await WriteAsync(files, From(new ClaimsPrincipal(new ClaimsIdentity())), open.ToString())).IsValid.Should().BeTrue();

        files.SingleReads.Should().HaveCount(3);
        files.SingleReads.Should().OnlyContain(read => read == "caller", "a request has a caller, and the store is asked as that caller");
    }

    [Fact]
    public async Task Every_value_a_caller_may_not_use_is_refused_in_the_same_words()
    {
        var files = new FakeFileStore();
        var owner = Guid.NewGuid();
        var theirs = files.Add(isPublic: false, owner: owner, fileName: "payslip-march.pdf");
        var stranger = From(FakeFileStore.User(Guid.NewGuid()));
        var absent = Guid.NewGuid();

        var refused = new object[]
        {
            theirs.ToString(),
            absent.ToString(),
            theirs.ToString("N"),
            "not-an-id",
            $"/api/files/{theirs}",
            42,
        };

        var messages = new List<string>();
        foreach (var value in refused)
        {
            var (isValid, errors) = await WriteAsync(files, stranger, value);
            isValid.Should().BeFalse("{0} is not a file this caller may use", value);
            errors.Should().ContainSingle();
            messages.Add(errors[0]);
        }

        messages.Should().HaveCount(6);
        messages.Distinct().Should().ContainSingle("a caller must not learn which of the reasons applied");
        messages[0].Should().Contain("Cover");
        messages[0].Should().NotContain(theirs.ToString()).And.NotContain(absent.ToString()).And.NotContain("payslip");

        // The control: the same id is accepted from the user the file belongs to.
        (await WriteAsync(files, From(FakeFileStore.User(owner)), theirs.ToString())).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_write_with_no_request_behind_it_takes_a_public_file_only()
    {
        var files = new FakeFileStore();
        var owner = Guid.NewGuid();
        var mine = files.Add(isPublic: false, owner: owner);
        var open = files.Add(isPublic: true, owner: owner);

        (await WriteAsync(files, NoRequest(), mine.ToString())).IsValid.Should().BeFalse();
        (await WriteAsync(files, NoRequest(), open.ToString())).IsValid.Should().BeTrue();

        files.SingleReads.Should().HaveCount(2);
        files.SingleReads.Should().OnlyContain(read => read == "public", "there is no caller to ask the store as");
    }

    [Fact]
    public async Task The_value_an_entry_already_holds_is_kept_without_asking_the_store_again()
    {
        var files = new FakeFileStore();
        var attached = files.Add(isPublic: false, owner: Guid.NewGuid());
        var another = files.Add(isPublic: false, owner: Guid.NewGuid());
        var colleague = From(FakeFileStore.User(Guid.NewGuid()));
        var entry = Holding(attached.ToString());

        // A colleague who may not download the file saves the entry with the field as it was.
        (await WriteAsync(files, colleague, attached.ToString(), entry)).IsValid.Should().BeTrue();
        (await WriteAsync(files, colleague, attached.ToString().ToUpperInvariant(), entry)).IsValid.Should().BeTrue();
        files.SingleReads.Should().BeEmpty("the stored value is not a new attachment");

        // The same save changing the field to another file that is not theirs is a new attachment.
        (await WriteAsync(files, colleague, another.ToString(), entry)).IsValid.Should().BeFalse();

        // Without the stored entry, as on a create, the first value is refused as well.
        (await WriteAsync(files, colleague, attached.ToString())).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Text_stored_before_the_field_was_a_file_field_is_kept_and_new_text_is_refused()
    {
        var files = new FakeFileStore();
        var caller = From(FakeFileStore.User(Guid.NewGuid()));
        var entry = Holding("https://cdn.example.com/cover.png");

        (await WriteAsync(files, caller, "https://cdn.example.com/cover.png", entry)).IsValid.Should().BeTrue();
        (await WriteAsync(files, caller, "https://cdn.example.com/other.png", entry)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task With_no_file_store_a_new_value_is_refused_naming_the_module_and_a_stored_one_is_kept()
    {
        var caller = From(FakeFileStore.User(Guid.NewGuid(), "SuperAdmin"));
        var id = Guid.NewGuid().ToString();

        var stores = new IFileStore?[] { null, new NoFileStore() };
        stores.Should().HaveCount(2);

        foreach (var store in stores)
        {
            var (isValid, errors) = await WriteAsync(store, caller, id);
            isValid.Should().BeFalse();
            errors.Should().ContainSingle().Which.Should().Contain("Cover").And.Contain("BarakoCMS.Files");
            errors[0].Should().NotContain(id);

            (await WriteAsync(store, caller, id, Holding(id))).IsValid.Should().BeTrue(
                "an entry written while the module was enabled can still be edited");

            (await WriteAsync(store, caller, cover: null)).IsValid.Should().BeTrue("an entry with no file needs no store");
        }
    }

    [Fact]
    public async Task A_required_file_field_left_out_is_reported_as_required()
    {
        var schema = new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = Type,
            DisplayName = "Gallery",
            Fields = [new FieldDefinition { Name = "Cover", DisplayName = "Cover", Type = "file", IsRequired = true }],
        };

        var (isValid, errors) = await new ContentValidatorService(null!, new FakeFileStore())
            .ValidateFieldsAsync(schema, Type, new Dictionary<string, object>(), existing: null);

        isValid.Should().BeFalse();
        errors.Should().ContainSingle().Which.Should().Contain("Cover").And.Contain("required");
    }

    [Fact]
    public async Task The_overloads_that_name_no_caller_take_a_public_file_only()
    {
        var files = new FakeFileStore();
        var owner = Guid.NewGuid();
        var mine = files.Add(isPublic: false, owner: owner).ToString();
        var open = files.Add(isPublic: true, owner: owner).ToString();
        var validator = new ContentValidatorService(null!, files);

        Dictionary<string, object> Data(string cover) => new() { ["Title"] = "a", ["Cover"] = cover };

        (await validator.ValidateFieldsAsync(Schema, Type, Data(mine), existing: null)).IsValid.Should().BeFalse(
            "a write that names no user is not the owner's");
        (await validator.ValidateFieldsAsync(Schema, Type, Data(open), existing: null)).IsValid.Should().BeTrue();

        // The control: the same file, for the user it belongs to.
        (await validator.ValidateFieldsAsync(Schema, Type, Data(mine), existing: null, FakeFileStore.User(owner)))
            .IsValid.Should().BeTrue();

        files.SingleReads.Should().Equal("public", "public", "caller");
    }

    [Fact]
    public async Task A_file_field_sent_twice_in_different_case_is_refused()
    {
        var files = new FakeFileStore();
        var caller = FakeFileStore.User(Guid.NewGuid());
        var open = files.Add(isPublic: true).ToString();
        var theirs = files.Add(isPublic: false, owner: Guid.NewGuid()).ToString();
        var validator = new ContentValidatorService(null!, files);

        var (isValid, errors) = await validator.ValidateFieldsAsync(
            Schema, Type, new Dictionary<string, object> { ["Title"] = "a", ["Cover"] = open, ["cover"] = theirs }, null, caller);

        isValid.Should().BeFalse("only the first spelling would be checked, and delivery reads both");
        errors.Should().ContainSingle().Which.Should().Contain("Cover").And.Contain("more than once");
        errors[0].Should().NotContain(theirs);

        // A null first spelling is not a way past it, whether the body's null arrives as null or as
        // a JsonElement of kind Null.
        var nulls = new object?[] { null, System.Text.Json.JsonDocument.Parse("null").RootElement };
        nulls.Should().HaveCount(2);
        foreach (var first in nulls)
        {
            var data = new Dictionary<string, object> { ["Title"] = "a", ["Cover"] = first!, ["cover"] = theirs };
            var (nullFirstValid, nullFirstErrors) = await validator.ValidateFieldsAsync(Schema, Type, data, null, caller);

            nullFirstValid.Should().BeFalse("a null first key skipped every check on the second");
            nullFirstErrors.Should().ContainSingle().Which.Should().Contain("more than once");
        }

        // The control: the first spelling alone is accepted.
        (await validator.ValidateFieldsAsync(
            Schema, Type, new Dictionary<string, object> { ["Title"] = "a", ["Cover"] = open }, null, caller))
            .IsValid.Should().BeTrue();
    }
}
