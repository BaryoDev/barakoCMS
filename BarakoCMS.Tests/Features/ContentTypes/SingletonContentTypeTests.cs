using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// A singleton content type holds one entry, which is how a site's own values (address, phone,
/// opening hours, footer text) become content instead of a fixed column somewhere.
/// </summary>
/// <remarks>
/// The update case is the one that decides whether the flag is usable at all. A cap written as "this
/// type already has an entry, refuse" also refuses the edit of the only entry, which leaves a type
/// whose whole purpose is to be edited and cannot be. So the cap is on creating, and the test for it
/// is here rather than left to be discovered by whoever tries to change a phone number.
///
/// What the cap is, and therefore what these tests claim, is a read before a write: an entry that is
/// in the database refuses the next create, including two creates sent back to back. Two that commit
/// at the same instant are not covered, the same way the content type name check is the friendly half
/// of a rule the unique index underneath it enforces. There is no index to lean on here, because
/// whether a type is a singleton lives in another document.
/// </remarks>
[Collection("Sequential")]
public class SingletonContentTypeTests
{
    private readonly IntegrationTestFixture _fixture;

    public SingletonContentTypeTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<HttpClient> AdminAsync()
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin", "Admin"));
        return client;
    }

    /// <summary>
    /// Created through the API rather than stored directly, so the request field that carries the
    /// flag is part of what these tests cover: a flag nothing can set is not a feature.
    /// </summary>
    private static async Task<string> TypeAsync(HttpClient client, bool singleton)
    {
        var asked = $"settings{Guid.NewGuid():n}"[..16];

        var res = await client.PostAsJsonAsync("/api/content-types", new
        {
            name = asked,
            displayName = $"Site Settings {asked}",
            isSingleton = singleton,
            fields = new[] { new { name = "Phone", type = "string" } },
        }, Ct);

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode,
            await res.Content.ReadAsStringAsync(Ct));

        using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));

        // The stored name, not the one asked for: it is normalised on the way in and every lookup
        // afterwards uses the stored spelling.
        return body.RootElement.GetProperty("name").GetString()!;
    }

    private static Task<HttpResponseMessage> CreateEntryAsync(HttpClient client, string type, string phone) =>
        client.PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["Phone"] = phone },
        }, Ct);

    private async Task<List<Content>> EntriesAsync(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.Query<Content>().Where(c => c.ContentType == type).ToListAsync(Ct)).ToList();
    }

    /// <summary>
    /// Entries of the type however the caller spelled it. <see cref="EntriesAsync"/> matches the
    /// stored name exactly, so it counts none of the rows a mis-cased create would leave behind, and
    /// counting none of them is what a test of the case hole must not do.
    /// </summary>
    private async Task<List<Content>> EntriesInAnyCaseAsync(string type)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var lowered = type.ToLower();
        return (await session.Query<Content>()
            .Where(c => c.ContentType.ToLower() == lowered)
            .ToListAsync(Ct)).ToList();
    }

    /// <summary>Walks the paged list, because that is the only route a console reads a type from.</summary>
    private static async Task<JsonElement?> DescribedAsync(HttpClient client, string name)
    {
        for (var page = 1; page <= 50; page++)
        {
            var res = await client.GetAsync($"/api/content-types?page={page}&pageSize=100", Ct);
            res.IsSuccessStatusCode.Should().BeTrue("got {0}", res.StatusCode);

            using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));
            var items = body.RootElement.GetProperty("items");

            foreach (var item in items.EnumerateArray())
            {
                if (item.GetProperty("name").GetString() == name)
                {
                    return item.Clone();
                }
            }

            if (items.GetArrayLength() < 100)
            {
                return null;
            }
        }

        return null;
    }

    [Fact]
    public async Task The_first_entry_of_a_singleton_type_is_accepted()
    {
        var client = await AdminAsync();
        var type = await TypeAsync(client, singleton: true);

        var res = await CreateEntryAsync(client, type, "+63 2 8123 4567");

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode,
            await res.Content.ReadAsStringAsync(Ct));

        var entries = await EntriesAsync(type);
        entries.Should().HaveCount(1, "the cap is one entry, not none");
    }

    [Fact]
    public async Task A_second_entry_of_a_singleton_type_is_refused()
    {
        var client = await AdminAsync();
        var type = await TypeAsync(client, singleton: true);

        (await CreateEntryAsync(client, type, "+63 2 8123 4567")).IsSuccessStatusCode
            .Should().BeTrue("the first entry has to land, or the second proves nothing");

        var second = await CreateEntryAsync(client, type, "+63 2 8999 0000");

        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await second.Content.ReadAsStringAsync(Ct);
        body.Should().Contain("single entry",
            "the refusal has to say why, or an editor reads it as the save being broken");
        body.Should().Contain("Site Settings",
            "and name the type, because a console shows this message next to the form");

        var entries = await EntriesAsync(type);
        entries.Should().HaveCount(1);
        entries[0].Data["Phone"].ToString().Should().Be("+63 2 8123 4567",
            "the refused request must not have overwritten the entry that was already there either");
    }

    /// <summary>
    /// The same refusal when the type name is shouted. A cap that only matches the stored spelling is
    /// walked past by holding down shift, and the entry that gets through is a real second row.
    /// </summary>
    /// <remarks>
    /// This is not hypothetical politeness about casing. Content type names are normalised to a
    /// lowercase slug on the way in, but <c>POST /api/contents</c> stores <c>contentType</c> as the
    /// caller typed it, and the schema lookup in the validator matches exactly: a shouted name used
    /// to find no schema at all, return valid, and land the row.
    /// </remarks>
    [Fact]
    public async Task A_second_entry_of_a_singleton_type_is_refused_when_the_name_is_cased_differently()
    {
        var client = await AdminAsync();
        var type = await TypeAsync(client, singleton: true);

        (await CreateEntryAsync(client, type, "+63 2 8123 4567")).IsSuccessStatusCode
            .Should().BeTrue("the first entry has to land, or the second proves nothing");

        var shouted = type.ToUpperInvariant();
        shouted.Should().NotBe(type, "the test needs a spelling that differs from the stored one");

        var second = await CreateEntryAsync(client, shouted, "+63 2 8999 0000");

        second.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "got {0}: {1}", second.StatusCode, await second.Content.ReadAsStringAsync(Ct));

        var entries = await EntriesInAnyCaseAsync(type);
        entries.Should().HaveCount(1, "a row stored under the shouted name is still a second entry");
        entries[0].ContentType.Should().Be(type);
    }

    /// <summary>
    /// The control. A cap that refused the second entry of every type would pass the test above, and
    /// would also break every content type in every existing deployment.
    /// </summary>
    [Fact]
    public async Task A_second_entry_of_a_type_that_is_not_a_singleton_is_accepted()
    {
        var client = await AdminAsync();
        var type = await TypeAsync(client, singleton: false);

        (await CreateEntryAsync(client, type, "first")).IsSuccessStatusCode.Should().BeTrue();
        var second = await CreateEntryAsync(client, type, "second");

        second.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", second.StatusCode,
            await second.Content.ReadAsStringAsync(Ct));

        (await EntriesAsync(type)).Should().HaveCount(2);
    }

    /// <summary>
    /// The entry a singleton type exists for can still be edited.
    /// </summary>
    /// <remarks>
    /// A count check that does not know a create from an update refuses this, and a flag whose one
    /// entry is read only is worse than no flag: the type looks editable in the console and every
    /// save fails.
    /// </remarks>
    [Fact]
    public async Task The_only_entry_of_a_singleton_type_can_still_be_updated()
    {
        var client = await AdminAsync();
        var type = await TypeAsync(client, singleton: true);

        var created = await CreateEntryAsync(client, type, "+63 2 8123 4567");
        created.IsSuccessStatusCode.Should().BeTrue();

        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        var id = createdBody.RootElement.GetProperty("id").GetGuid();

        var updated = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            data = new Dictionary<string, object> { ["Phone"] = "+63 2 8555 1111" },
        }, Ct);

        updated.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", updated.StatusCode,
            await updated.Content.ReadAsStringAsync(Ct));

        var entries = await EntriesAsync(type);
        entries.Should().HaveCount(1, "an update is an edit of the one entry, not a second one");
        entries[0].Data["Phone"].ToString().Should().Be("+63 2 8555 1111",
            "a 200 on a write that changed nothing would pass the count assertion above");
    }

    /// <summary>
    /// The other update path. A rollback rewrites the one entry, so it is not a second entry either.
    /// </summary>
    /// <remarks>
    /// Here because the suites that cover the rollback endpoint all use ordinary types, and a
    /// rollback of an ordinary type passes whether or not the endpoint tells the validator which
    /// entry is being changed. Removing <c>existing: content</c> from RollbackEndpoint leaves those
    /// green and makes this one fail with a 400, which is the whole reason it exists.
    /// </remarks>
    [Fact]
    public async Task The_only_entry_of_a_singleton_type_can_still_be_rolled_back()
    {
        var client = await AdminAsync();
        var type = await TypeAsync(client, singleton: true);

        var created = await CreateEntryAsync(client, type, "+63 2 8123 4567");
        created.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", created.StatusCode,
            await created.Content.ReadAsStringAsync(Ct));

        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        var id = createdBody.RootElement.GetProperty("id").GetGuid();

        var updated = await client.PutAsJsonAsync($"/api/contents/{id}", new
        {
            data = new Dictionary<string, object> { ["Phone"] = "+63 2 8555 1111" },
        }, Ct);
        updated.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", updated.StatusCode,
            await updated.Content.ReadAsStringAsync(Ct));

        var history = await client.GetAsync($"/api/contents/{id}/history", Ct);
        history.IsSuccessStatusCode.Should().BeTrue("got {0}", history.StatusCode);

        using var historyBody = JsonDocument.Parse(await history.Content.ReadAsStringAsync(Ct));
        var firstVersions = historyBody.RootElement.GetProperty("items").EnumerateArray()
            .Where(v => v.GetProperty("data").GetProperty("Phone").GetString() == "+63 2 8123 4567")
            .Select(v => v.GetProperty("versionId").GetGuid())
            .ToList();

        firstVersions.Should().HaveCount(1, "the version being restored has to be in the history");

        var rolledBack = await client.PostAsJsonAsync(
            $"/api/contents/{id}/rollback/{firstVersions[0]}", new { }, Ct);

        rolledBack.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", rolledBack.StatusCode,
            await rolledBack.Content.ReadAsStringAsync(Ct));

        var entries = await EntriesAsync(type);
        entries.Should().HaveCount(1, "a rollback rewrites the entry, it does not add one");
        entries[0].Data["Phone"].ToString().Should().Be("+63 2 8123 4567",
            "a 200 on a rollback that restored nothing would pass the count assertion above");
    }

    /// <summary>
    /// Archiving the one entry does not free the slot. Decided this way rather than the other.
    /// </summary>
    /// <remarks>
    /// The cap counts entries of every status on purpose. A console or a site reading the first item
    /// of the list cannot tell an archived row from a live one, so letting an archive free the slot
    /// puts two rows under the type and brings back exactly the ambiguity the flag removes. The way
    /// out is erasing the entry, which needs SuperAdmin and the erase_content capability, and that
    /// cost is the point: a singleton's one entry is the site's own values, not a draft.
    /// </remarks>
    [Fact]
    public async Task Archiving_the_only_entry_of_a_singleton_type_does_not_free_the_slot()
    {
        var client = await AdminAsync();
        var type = await TypeAsync(client, singleton: true);

        var created = await CreateEntryAsync(client, type, "+63 2 8123 4567");
        created.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", created.StatusCode,
            await created.Content.ReadAsStringAsync(Ct));

        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        var id = createdBody.RootElement.GetProperty("id").GetGuid();

        var archived = await client.PutAsJsonAsync(
            $"/api/contents/{id}/status", new { id, newStatus = "Archived" }, Ct);
        archived.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", archived.StatusCode,
            await archived.Content.ReadAsStringAsync(Ct));

        var second = await CreateEntryAsync(client, type, "+63 2 8999 0000");

        second.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "got {0}: {1}", second.StatusCode, await second.Content.ReadAsStringAsync(Ct));

        var entries = await EntriesAsync(type);
        entries.Should().HaveCount(1);
        entries[0].Status.Should().Be(ContentStatus.Archived,
            "the archived entry is the one still holding the slot, so the status change did happen");
    }

    /// <summary>
    /// The two-argument ValidateAsync is obsolete and still has to be there, and still has to mean
    /// create.
    /// </summary>
    /// <remarks>
    /// BarakoCMS.Import 4.0.0 is on nuget.org and its IL names that member. A host that upgrades the
    /// core package while keeping that module resolves it at runtime with nothing at compile time to
    /// warn them, so dropping it is a MissingMethodException on POST /api/import/content. Called
    /// through reflection rather than with a cast, because the compiler would happily bind a
    /// two-argument call to the three-argument overload's default and this test would then prove
    /// nothing about the member that ships.
    /// </remarks>
    [Fact]
    public async Task The_obsolete_two_argument_validate_is_still_on_the_interface_and_still_caps()
    {
        var client = await AdminAsync();
        var type = await TypeAsync(client, singleton: true);

        (await CreateEntryAsync(client, type, "+63 2 8123 4567")).IsSuccessStatusCode
            .Should().BeTrue("the cap needs an entry to refuse the next one against");

        var method = typeof(barakoCMS.Infrastructure.Services.IContentValidatorService).GetMethod(
            "ValidateAsync", [typeof(string), typeof(Dictionary<string, object>)]);

        method.Should().NotBeNull(
            "BarakoCMS.Import 4.0.0 calls ValidateAsync(string, Dictionary<string, object>) and is "
            + "already published, so the member cannot leave until a major that drops support for it");
        method!.ReturnType.Should().Be(typeof(Task<(bool IsValid, List<string> Errors)>),
            "a changed return type is the same binary break as a removed method");

        using var scope = _fixture.Services.CreateScope();
        var validator = scope.ServiceProvider
            .GetRequiredService<barakoCMS.Infrastructure.Services.IContentValidatorService>();

        var call = (Task<(bool IsValid, List<string> Errors)>)method.Invoke(
            validator, [type, new Dictionary<string, object> { ["Phone"] = "+63 2 8999 0000" }])!;

        var (isValid, errors) = await call;

        isValid.Should().BeFalse("the old overload always meant create, and a create is capped");
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("single entry");
    }

    /// <summary>
    /// The flag is readable back, because the console decides between a list and one edit screen
    /// from it and has nowhere else to get it.
    /// </summary>
    [Fact]
    public async Task A_type_reports_whether_it_is_a_singleton()
    {
        var client = await AdminAsync();
        var singleton = await TypeAsync(client, singleton: true);
        var ordinary = await TypeAsync(client, singleton: false);

        var describedSingleton = await DescribedAsync(client, singleton);
        var describedOrdinary = await DescribedAsync(client, ordinary);

        describedSingleton.Should().NotBeNull("the type was just created through this same API");
        describedOrdinary.Should().NotBeNull();

        describedSingleton!.Value.GetProperty("isSingleton").GetBoolean().Should().BeTrue();
        describedOrdinary!.Value.GetProperty("isSingleton").GetBoolean().Should().BeFalse(
            "the default has to read back as false, not as absent");
    }
}
