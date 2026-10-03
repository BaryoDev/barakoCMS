using System.Net;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using Xunit;
using Probe = BarakoCMS.Tests.TokenFieldProbe;

namespace BarakoCMS.Tests.Features.ContentApi;

/// <summary>
/// A token under permission field sets: a Read set that names it does not make it searchable, a
/// sent token is dropped before the write sets are judged, so it is never the reason for a 403,
/// and a blind write cannot set it.
/// </summary>
/// <remarks>
/// Every caller holds <c>view_hidden</c>, so field sensitivity is not what keeps the token.
/// </remarks>
[Collection("Sequential")]
public class TokenFieldSetTests
{
    private const string Forged = "forged0token0forged0token0forged";

    private readonly IntegrationTestFixture _factory;

    public TokenFieldSetTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> CallerAsync(PermissionRule? read, PermissionRule update, string type)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"TokenSet_{Guid.NewGuid():n}",
            SystemCapabilities = [SystemCapabilities.ViewHidden],
            Permissions =
            [
                new ContentTypePermission
                {
                    ContentTypeSlug = type,
                    Read = read ?? new PermissionRule { Enabled = false },
                    Create = new PermissionRule { Enabled = false },
                    Update = update,
                    Delete = new PermissionRule { Enabled = false },
                },
            ],
        };
        session.Store(role);

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"user_{Guid.NewGuid()}",
            Email = $"{Guid.NewGuid()}@example.com",
            RoleIds = [role.Id],
        };
        session.Store(user);
        await session.SaveChangesAsync(Ct);

        return _factory.CreateToken([role.Name], user.Id.ToString());
    }

    private async Task<List<Guid>> SearchAsync(string token, string type, string term)
    {
        var response = await Probe.ClientFor(_factory, token)
            .GetAsync($"/api/contents?contentType={type}&search={term}&pageSize=100", Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetGuid())
            .ToList();
    }

    [Fact]
    public async Task A_read_set_naming_the_token_does_not_make_it_searchable()
    {
        var type = await Probe.StoreTypeAsync(_factory, "toksetsearch", false, Probe.Text("Name"), Probe.Token());
        var id = await Probe.CreateAsync(_factory, await Probe.AdminAsync(_factory), type, new() { ["Name"] = "Ana" });
        var token = Probe.TokenOf(await Probe.StoredAsync(_factory, id));

        var caller = await CallerAsync(
            new PermissionRule { Enabled = true, ReadableFields = ["Name", "ClaimToken"] },
            new PermissionRule { Enabled = false },
            type);

        var byName = await SearchAsync(caller, type, "Ana");
        byName.Should().HaveCount(1, "the search runs over the shown fields and finds the entry by its name");
        byName.Should().Equal(id);

        (await SearchAsync(caller, type, token)).Should().BeEmpty();
        (await SearchAsync(caller, type, token[..6])).Should().BeEmpty();
    }

    [Fact]
    public async Task A_sent_token_outside_the_writable_set_is_dropped_and_not_refused()
    {
        var type = await Probe.StoreTypeAsync(_factory, "toksetwrite", false, Probe.Text("Name"), Probe.Token());
        var id = await Probe.CreateAsync(_factory, await Probe.AdminAsync(_factory), type, new() { ["Name"] = "Ana" });
        var token = Probe.TokenOf(await Probe.StoredAsync(_factory, id));

        var caller = await CallerAsync(
            new PermissionRule { Enabled = true, ReadableFields = ["Name", "ClaimToken"] },
            new PermissionRule { Enabled = true, WritableFields = ["Name"] },
            type);

        var updated = await Probe.UpdateAsync(_factory, caller, id, new() { ["Name"] = "Ana Cruz", ["ClaimToken"] = Forged });

        updated.StatusCode.Should().Be(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync(Ct));
        var stored = await Probe.StoredAsync(_factory, id);
        stored.Data["Name"].ToString().Should().Be("Ana Cruz");
        Probe.TokenOf(stored).Should().Be(token);
    }

    [Fact]
    public async Task A_blind_write_on_an_entry_the_caller_may_not_read_cannot_set_the_token()
    {
        var type = await Probe.StoreTypeAsync(_factory, "toksetblind", false, Probe.Text("Name"), Probe.Token());
        var id = await Probe.CreateAsync(_factory, await Probe.AdminAsync(_factory), type, new() { ["Name"] = "Ana" });
        var token = Probe.TokenOf(await Probe.StoredAsync(_factory, id));

        var caller = await CallerAsync(
            read: null,
            new PermissionRule { Enabled = true, WritableFields = ["Name", "ClaimToken"] },
            type);

        var updated = await Probe.UpdateAsync(_factory, caller, id, new() { ["Name"] = "Ana Cruz", ["ClaimToken"] = Forged });

        updated.StatusCode.Should().Be(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync(Ct));
        var stored = await Probe.StoredAsync(_factory, id);
        stored.Data["Name"].ToString().Should().Be("Ana Cruz", "the blind write itself went through");
        Probe.TokenOf(stored).Should().Be(token);
    }
}
