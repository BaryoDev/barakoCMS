using System.Security.Claims;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Public;
using barakoCMS.Features.Seo;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;

namespace BarakoCMS.Tests;

/// <summary>
/// How a response resolves the ids in <c>file</c> fields, against a file store held in memory that
/// counts what it is asked, with no host and no database.
/// </summary>
public class FileFieldResolutionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ContentTypeDefinition TypeWith(params string[] fileFields) => new()
    {
        Id = Guid.NewGuid(),
        Name = "gallery",
        DisplayName = "Gallery",
        IsPubliclyDeliverable = true,
        Fields =
        [
            new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" },
            .. fileFields.Select(name => new FieldDefinition { Name = name, DisplayName = name, Type = "file" }),
        ],
    };

    private static PublicContentResponse Entry(Dictionary<string, object> data, SeoMetadata? seo = null) =>
        new(Guid.NewGuid(), "gallery", null, data, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, seo);

    [Fact]
    public async Task Delivery_answers_a_public_file_as_an_object_and_leaves_every_other_file_out()
    {
        var files = new FakeFileStore();
        var open = files.Add(isPublic: true, fileName: "harbour.png", alt: "Boats at dawn", caption: "Taken in March");
        var locked = files.Add(isPublic: false, owner: Guid.NewGuid(), fileName: "payslip-march.pdf");
        var gone = Guid.NewGuid();
        var definition = TypeWith("Cover");

        var entries = new List<PublicContentResponse>
        {
            Entry(new() { ["Title"] = "public", ["Cover"] = open.ToString() }),
            Entry(new() { ["Title"] = "private", ["Cover"] = locked.ToString() }),
            Entry(new() { ["Title"] = "deleted", ["Cover"] = gone.ToString() }),
            Entry(new() { ["Title"] = "text", ["Cover"] = "https://cdn.example.com/cover.png" }),
            Entry(new() { ["Title"] = "empty", ["Cover"] = null! }),
            Entry(new() { ["Title"] = "none" }),
        };

        var resolved = await PublicFileFields.ResolveAsync(entries, definition, files, Ct);

        resolved.Should().HaveCount(6);
        resolved.Select(r => r.Id).Should().Equal(entries.Select(e => e.Id));
        resolved.Select(r => r.Data["Title"]).Should().Equal("public", "private", "deleted", "text", "empty", "none");

        var file = resolved[0].Data["Cover"].Should().BeOfType<ResolvedFile>().Subject;
        file.Id.Should().Be(open);
        file.Url.Should().Be($"/api/public/files/{open}");
        file.FileName.Should().Be("harbour.png");
        file.ContentType.Should().Be("image/png");
        file.Size.Should().Be(1234);
        file.Alt.Should().Be("Boats at dawn");
        file.Caption.Should().Be("Taken in March");

        resolved[1].Data.Should().NotContainKey("Cover", "a private file is not answered, not even as its id");
        resolved[2].Data.Should().NotContainKey("Cover");
        resolved[3].Data.Should().NotContainKey("Cover", "text that is not an id names no file");
        resolved[4].Data.Should().ContainKey("Cover").WhoseValue.Should().BeNull();
        resolved[5].Data.Should().NotContainKey("Cover");

        files.PublicBatches.Should().Equal(new[] { 3 }, "the three ids on the page are read together");
        files.CallerBatches.Should().BeEmpty("anonymous delivery never asks as a caller");
        files.SingleReads.Should().BeEmpty();
    }

    [Fact]
    public async Task Delivery_matches_a_data_key_to_its_field_without_case()
    {
        var files = new FakeFileStore();
        var open = files.Add(isPublic: true);
        var locked = files.Add(isPublic: false, owner: Guid.NewGuid());

        var resolved = await PublicFileFields.ResolveAsync(
            [Entry(new() { ["cover"] = open.ToString(), ["THUMB"] = locked.ToString() })],
            TypeWith("Cover", "Thumb"),
            files,
            Ct);

        resolved.Should().ContainSingle();
        resolved[0].Data.Should().ContainKey("cover").WhoseValue.Should().BeOfType<ResolvedFile>();
        resolved[0].Data.Should().NotContainKey("THUMB");
    }

    [Fact]
    public async Task A_public_file_whose_store_gives_no_address_is_left_out()
    {
        var files = new FakeFileStore();
        var unaddressed = files.Add(isPublic: true, withAddress: false);

        var resolved = await PublicFileFields.ResolveAsync(
            [Entry(new() { ["Cover"] = unaddressed.ToString() })], TypeWith("Cover"), files, Ct);

        resolved.Should().ContainSingle();
        resolved[0].Data.Should().NotContainKey("Cover");
    }

    [Fact]
    public async Task With_no_file_store_delivery_leaves_the_field_out_and_the_entry_still_reads()
    {
        var id = Guid.NewGuid().ToString();

        var stores = new IFileStore?[] { null, new NoFileStore() };
        stores.Should().HaveCount(2);

        foreach (var store in stores)
        {
            var resolved = await PublicFileFields.ResolveAsync(
                [Entry(new() { ["Title"] = "kept", ["Cover"] = id })], TypeWith("Cover"), store, Ct);

            resolved.Should().ContainSingle();
            resolved[0].Data.Should().ContainKey("Title").WhoseValue.Should().Be("kept");
            resolved[0].Data.Should().NotContainKey("Cover");
        }
    }

    [Fact]
    public async Task A_type_with_no_file_field_is_answered_as_it_was_and_the_store_is_not_asked()
    {
        var files = new FakeFileStore();
        var id = files.Add(isPublic: true).ToString();
        var entry = Entry(new() { ["Title"] = id });

        var resolved = await PublicFileFields.ResolveAsync([entry], TypeWith(), files, Ct);

        resolved.Should().ContainSingle().Which.Should().BeSameAs(entry);
        files.PublicBatches.Should().BeEmpty();
    }

    [Fact]
    public async Task The_seo_image_of_a_file_field_is_the_files_address_or_nothing()
    {
        var files = new FakeFileStore();
        var open = files.Add(isPublic: true);
        var locked = files.Add(isPublic: false, owner: Guid.NewGuid());
        var definition = TypeWith(SeoFields.SocialImage);

        SeoMetadata Read(Guid id) => new("t", null, null, id.ToString(), false);

        var resolved = await PublicFileFields.ResolveAsync(
            [
                Entry(new() { [SeoFields.SocialImage] = open.ToString() }, Read(open)),
                Entry(new() { [SeoFields.SocialImage] = locked.ToString() }, Read(locked)),
            ],
            definition,
            files,
            Ct);

        resolved.Should().HaveCount(2);
        resolved[0].Seo!.ImageUrl.Should().Be($"/api/public/files/{open}");
        resolved[0].Seo!.Title.Should().Be("t");
        resolved[1].Seo!.ImageUrl.Should().BeNull("the id of a private file is not an image address");
    }

    [Fact]
    public async Task A_page_of_a_hundred_entries_with_five_file_fields_is_one_read()
    {
        var files = new FakeFileStore();
        var names = new[] { "A", "B", "C", "D", "E" };
        var entries = Enumerable.Range(0, 100)
            .Select(_ => Entry(names.ToDictionary(n => n, n => (object)files.Add(isPublic: true).ToString())))
            .ToList();

        var resolved = await PublicFileFields.ResolveAsync(entries, TypeWith(names), files, Ct);

        resolved.Should().HaveCount(100);
        resolved.SelectMany(r => r.Data.Values).Should().HaveCount(500).And.AllBeOfType<ResolvedFile>();
        files.PublicBatches.Should().Equal(500);
    }

    [Fact]
    public async Task More_ids_than_one_read_takes_are_read_in_further_batches_and_none_is_dropped()
    {
        var files = new FakeFileStore();
        var ids = Enumerable.Range(0, FileFieldResolver.IdsPerRead * 2 + 1)
            .Select(_ => files.Add(isPublic: true))
            .ToHashSet();
        var caller = FakeFileStore.User(Guid.NewGuid());

        var anonymous = await FileFieldResolver.ReadAsync(files, ids, caller: null, Ct);
        var signedIn = await FileFieldResolver.ReadAsync(files, ids, caller, Ct);

        anonymous.Should().HaveCount(1001);
        anonymous.Keys.Should().BeEquivalentTo(ids);
        files.PublicBatches.Should().Equal(500, 500, 1);

        signedIn.Should().HaveCount(1001);
        files.CallerBatches.Should().Equal(500, 500, 1);
    }

    [Fact]
    public async Task An_authoring_read_answers_only_the_files_its_caller_may_download()
    {
        var files = new FakeFileStore();
        var owner = Guid.NewGuid();
        var mine = files.Add(isPublic: false, owner: owner, fileName: "mine.png");
        var theirs = files.Add(isPublic: false, owner: Guid.NewGuid(), fileName: "theirs.png");
        var open = files.Add(isPublic: true, fileName: "open.png");

        var data = new Dictionary<string, object>
        {
            ["Mine"] = mine.ToString(), ["Theirs"] = theirs.ToString(), ["Open"] = open.ToString(), ["Title"] = open.ToString(),
        };
        var fields = barakoCMS.Core.Validation.FileFields.Names(TypeWith("Mine", "Theirs", "Open"));
        fields.Should().HaveCount(3);

        var found = await FileFieldResolver.ReadAsync(
            files, new HashSet<Guid> { mine, theirs, open }, FakeFileStore.User(owner), Ct);
        var resolved = FileFieldResolver.Resolved(data, fields, found);

        resolved.Should().NotBeNull();
        resolved!.Keys.Should().BeEquivalentTo(new[] { "Mine", "Open" });
        resolved["Mine"].FileName.Should().Be("mine.png");
        resolved["Mine"].Url.Should().BeNull("a private file has no address anyone can fetch");
        resolved["Open"].Url.Should().Be($"/api/public/files/{open}");

        files.CallerBatches.Should().Equal(3);
        files.PublicBatches.Should().BeEmpty();
    }

    [Fact]
    public async Task With_no_file_store_an_authoring_read_answers_no_files_and_reads_nothing()
    {
        var entries = new List<(string ContentType, Dictionary<string, object> Data)>
        {
            ("gallery", new() { ["Cover"] = Guid.NewGuid().ToString() }),
            ("gallery", new() { ["Title"] = "b" }),
        };

        // The session is null: reaching it would throw, so this also shows it is not reached.
        var resolved = await barakoCMS.Features.Content.EntryFiles.ResolveAsync(
            entries, session: null!, new NoFileStore(), FakeFileStore.User(Guid.NewGuid()), Ct);

        resolved.Should().HaveCount(2);
        resolved.Should().OnlyContain(files => files == null);
    }

    /// <summary>A store written against the two members the seam first shipped with, plus the read for a caller.</summary>
    private sealed class SingleReadsOnly(FakeFileStore inner) : IFileStore
    {
        public Task<StoredFileInfo?> FindPublicAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.FindPublicAsync(id, cancellationToken);

        public Task<Stream?> OpenPublicAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.OpenPublicAsync(id, cancellationToken);

        public Task<StoredFileInfo?> FindAsync(Guid id, ClaimsPrincipal caller, CancellationToken cancellationToken = default) =>
            inner.FindAsync(id, caller, cancellationToken);
    }

    [Fact]
    public async Task A_store_that_has_only_the_single_reads_answers_many_through_them()
    {
        var inner = new FakeFileStore();
        var owner = Guid.NewGuid();
        var open = inner.Add(isPublic: true);
        var mine = inner.Add(isPublic: false, owner: owner);
        var absent = Guid.NewGuid();
        IFileStore store = new SingleReadsOnly(inner);

        var asked = new[] { open, mine, absent, open };

        var anonymous = await store.FindPublicManyAsync(asked, Ct);
        anonymous.Should().ContainSingle().Which.Key.Should().Be(open);

        var forOwner = await store.FindManyAsync(asked, FakeFileStore.User(owner), Ct);
        forOwner.Should().HaveCount(2);
        forOwner.Keys.Should().BeEquivalentTo(new[] { open, mine });

        inner.PublicBatches.Should().BeEmpty("the wrapped store's own batch members were never reached");
        inner.CallerBatches.Should().BeEmpty();
        inner.SingleReads.Should().HaveCount(6, "three distinct ids are asked once each, for each of the two calls");
    }
}
