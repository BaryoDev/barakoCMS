using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Models;
using Xunit;
using Probe = BarakoCMS.Tests.TokenFieldProbe;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// A value a caller wrote under a name before a token field of that name existed never becomes the
/// token: adding the field is refused while entries hold one, and a stored value the server could
/// not have generated is replaced on the next save.
/// </summary>
/// <remarks>
/// Entry data can hold keys no field declares, which is how such a value gets there.
/// </remarks>
[Collection("Sequential")]
public class TokenFieldTakeoverTests
{
    private const string Chosen = "chosen-by-an-editor";

    private readonly IntegrationTestFixture _factory;

    public TokenFieldTakeoverTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Adding_a_token_field_under_a_name_entries_already_hold_is_refused_with_the_count()
    {
        var type = await Probe.StoreTypeAsync(_factory, "tokover", false, Probe.Text("Name"));
        var admin = await Probe.AdminAsync(_factory);
        var holding = await Probe.CreateAsync(_factory, admin, type, new() { ["Name"] = "Ana", ["ClaimToken"] = Chosen });
        await Probe.CreateAsync(_factory, admin, type, new() { ["Name"] = "Ben" });
        (await Probe.StoredAsync(_factory, holding)).Data.Should().ContainKey("ClaimToken",
            "the undeclared key was stored, or the refusal below is about nothing");

        var added = await Probe.ClientFor(_factory, admin).PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "ClaimToken",
            displayName = "Claim token",
            type = "token",
        }, Ct);

        added.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await added.Content.ReadAsStringAsync(Ct);
        body.Should().Contain("1 entry").And.Contain("ClaimToken").And.NotContain(Chosen);
        (await Probe.StoredTypeAsync(_factory, type)).Fields.Should().NotContain(f => f.Name == "ClaimToken");

        var other = await Probe.ClientFor(_factory, admin).PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "TicketToken",
            displayName = "Ticket token",
            type = "token",
        }, Ct);
        other.StatusCode.Should().Be(HttpStatusCode.OK, "a name no entry holds is free");
    }

    [Fact]
    public async Task Creating_a_type_with_a_token_field_over_entries_holding_that_name_is_refused()
    {
        var name = "tokorphan" + Guid.NewGuid().ToString("n")[..10];
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Content
            {
                Id = Guid.NewGuid(),
                ContentType = name,
                Data = new Dictionary<string, object> { ["Name"] = "Ana", ["ClaimToken"] = Chosen },
                CreatedAt = DateTime.UtcNow,
            });
            await session.SaveChangesAsync(Ct);
        }

        var created = await Probe.ClientFor(_factory, await Probe.AdminAsync(_factory)).PostAsJsonAsync("/api/content-types", new
        {
            name,
            displayName = "Ticket",
            fields = new object[]
            {
                new { name = "Name", displayName = "Name", type = "string" },
                new { name = "ClaimToken", displayName = "Claim token", type = "token" },
            },
        }, Ct);

        created.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await created.Content.ReadAsStringAsync(Ct)).Should().Contain("ClaimToken").And.NotContain(Chosen);
    }

    [Fact]
    public async Task A_stored_value_the_server_could_not_have_generated_is_replaced_on_the_next_save()
    {
        // The value is written while the type has no such field, and the field is then added past
        // the endpoints, which is the case the writer's own check is for.
        var type = await Probe.StoreTypeAsync(_factory, "tokbad", false, Probe.Text("Name"));
        var admin = await Probe.AdminAsync(_factory);
        var id = await Probe.CreateAsync(_factory, admin, type, new() { ["Name"] = "Ana", ["ClaimToken"] = Chosen });

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var definition = await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type, Ct);
            definition.Fields.Add(Probe.Token());
            session.Store(definition);
            await session.SaveChangesAsync(Ct);
        }

        var updated = await Probe.UpdateAsync(_factory, admin, id, new() { ["Name"] = "Ana Cruz" });
        updated.StatusCode.Should().Be(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync(Ct));

        var token = Probe.TokenOf(await Probe.StoredAsync(_factory, id));
        token.Should().NotBe(Chosen);
        Probe.ShouldBeAToken(token);
    }
}
