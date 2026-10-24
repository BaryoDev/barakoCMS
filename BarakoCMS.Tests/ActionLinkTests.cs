using System.Net;
using System.Net.Http.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A signed action link approves once, after a confirmation, and a GET alone changes nothing.
/// </summary>
/// <remarks>
/// Each refusal is paired with the same link working before the thing it lacked is taken away, so
/// a route that refused everything would not pass.
/// </remarks>
[Collection("Integration1")]
public class ActionLinkTests
{
    private readonly IntegrationTestFixture _factory;
    private static readonly object HostGate = new();
    private static WebApplicationFactory<Program>? _enabled;

    public ActionLinkTests(IntegrationTestFixture factory) => _factory = factory;

    private WebApplicationFactory<Program> Enabled()
    {
        lock (HostGate)
        {
            return _enabled ??= _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(s => s.AddSingleton<IStartupFilter, TestRemoteIpFilter>());
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Workflows:ActionLinks:Enabled"] = "true",
                        ["App:BaseUrl"] = "https://cms.example.com",
                    }));
            });
        }
    }

    private static int _ipCounter;

    private HttpClient EnabledClient()
    {
        var n = Interlocked.Increment(ref _ipCounter);
        var client = Enabled().CreateClient();
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"198.51.{100 + n / 250}.{1 + n % 250}");
        return client;
    }

    private static LifecycleDefinition Review() => new()
    {
        States = ["Draft", "Submitted", "Approved", "Rejected"],
        InitialState = "Draft",
        Transitions =
        [
            new StateTransition { Name = "Submit", From = "Draft", To = "Submitted" },
            new StateTransition { Name = "Approve", From = "Submitted", To = "Approved" },
            new StateTransition { Name = "Reject", From = "Submitted", To = "Rejected", RequiredFields = ["RejectionReason"] },
        ],
    };

    private static ContentTypePermission Reviewer(string type) => new()
    {
        ContentTypeSlug = type,
        Read = new PermissionRule { Enabled = true },
        Transitions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Approve"] = new PermissionRule { Enabled = true },
            ["Reject"] = new PermissionRule { Enabled = true },
        },
    };

    private sealed record Setup(string Type, Guid EntryId, User Reviewer, Role Role);

    /// <summary>A type with a lifecycle, an entry in Submitted, and a reviewer who may approve and reject it.</summary>
    private async Task<Setup> SubmittedEntryAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var type = "alk" + Guid.NewGuid().ToString("n")[..8];
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = "Claim",
            Fields =
            [
                new() { Name = "Title", DisplayName = "Title", Type = "string" },
                new() { Name = "RejectionReason", DisplayName = "Rejection reason", Type = "string" },
            ],
            Lifecycle = Review(),
        });

        var role = new Role { Id = Guid.NewGuid(), Name = $"Role_{Guid.NewGuid():n}", Permissions = [Reviewer(type)] };
        var reviewer = new User
        {
            Id = Guid.NewGuid(),
            Username = $"alk_{Guid.NewGuid():n}",
            Email = $"alk_{Guid.NewGuid():n}@example.com",
            RoleIds = [role.Id],
        };
        var entry = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Data = new Dictionary<string, object> { ["Title"] = "a claim" },
            CreatedBy = Guid.NewGuid(),
            LifecycleState = "Submitted",
        };
        session.Store(role);
        session.Store(reviewer);
        session.Store(entry);
        session.Events.StartStream<Content>(
            entry.Id,
            new ContentCreated(
                entry.Id, type, entry.Data, ContentStatus.Draft, entry.CreatedBy, "a claim",
                SensitivityLevel.Public, DateTime.UtcNow),
            new ContentTransitioned(entry.Id, "Submit", "Draft", "Submitted", entry.CreatedBy, DateTime.UtcNow));
        await session.SaveChangesAsync();
        return new Setup(type, entry.Id, reviewer, role);
    }

    private string Token(Setup s, string transition, DateTimeOffset? now = null)
    {
        using var scope = Enabled().Services.CreateScope();
        var issuer = scope.ServiceProvider.GetRequiredService<ActionLinkTokenIssuer>();
        var token = issuer.Issue(s.EntryId, transition, s.Reviewer.Id, "Submitted", now ?? DateTimeOffset.UtcNow);
        token.Should().NotBeNullOrEmpty();
        return token!;
    }

    private string UseKey(string token)
    {
        using var scope = Enabled().Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ActionLinkTokenIssuer>()
            .Read(token, DateTimeOffset.UtcNow)!.Id.ToString("N");
    }

    private async Task<ActionLinkUse?> UseAsync(string token)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<ActionLinkUse>(UseKey(token));
    }

    private async Task<Content> EntryAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Content>(id))!;
    }

    private static string Url(string token) => $"/api/public/action-links/{token}";

    [Fact]
    public async Task A_link_approves_the_entry_once_after_it_is_confirmed_with_a_POST()
    {
        var s = await SubmittedEntryAsync();
        var client = EnabledClient();
        var token = Token(s, "Approve");

        var confirmed = await client.PostAsJsonAsync(Url(token), new { });

        confirmed.StatusCode.Should().Be(HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync());
        (await EntryAsync(s.EntryId)).LifecycleState.Should().Be("Approved");
    }

    [Fact]
    public async Task A_second_use_of_the_same_link_fails()
    {
        var s = await SubmittedEntryAsync();
        var client = EnabledClient();
        var token = Token(s, "Approve");

        (await client.PostAsJsonAsync(Url(token), new { })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync(Url(token), new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await UseAsync(token)).Should().NotBeNull("the first use stored its mark");
    }

    [Fact]
    public async Task A_spent_link_is_refused_even_while_the_entry_is_still_in_the_state()
    {
        var s = await SubmittedEntryAsync();
        var client = EnabledClient();
        var spent = Token(s, "Approve");
        var fresh = Token(s, "Approve");

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ActionLinkUse
            {
                Key = UseKey(spent),
                ConsumedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            });
            await session.SaveChangesAsync();
        }

        (await client.GetAsync(Url(fresh))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync(Url(spent))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_expired_link_fails()
    {
        var s = await SubmittedEntryAsync();
        var client = EnabledClient();
        var expired = Token(s, "Approve", DateTimeOffset.UtcNow.AddDays(-10));
        var fresh = Token(s, "Approve");

        (await client.PostAsJsonAsync(Url(expired), new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await EntryAsync(s.EntryId)).LifecycleState.Should().Be("Submitted");
        (await client.GetAsync(Url(fresh))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_GET_alone_changes_nothing_and_says_what_confirming_would_do()
    {
        var s = await SubmittedEntryAsync();
        var client = EnabledClient();
        var token = Token(s, "Approve");

        for (var i = 0; i < 2; i++)
        {
            var preview = await client.GetAsync(Url(token));
            preview.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await preview.Content.ReadAsStringAsync();
            body.Should().Contain("Approve").And.Contain("Approved");
        }

        (await EntryAsync(s.EntryId)).LifecycleState.Should().Be("Submitted");

        (await UseAsync(token)).Should().BeNull("a preview spends nothing");
    }

    [Fact]
    public async Task A_link_fails_once_the_user_loses_the_role()
    {
        var s = await SubmittedEntryAsync();
        var client = EnabledClient();
        var token = Token(s, "Approve");
        (await client.GetAsync(Url(token))).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var user = (await session.LoadAsync<User>(s.Reviewer.Id))!;
            user.RoleIds = [];
            session.Store(user);
            await session.SaveChangesAsync();
        }

        (await client.PostAsJsonAsync(Url(token), new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await EntryAsync(s.EntryId)).LifecycleState.Should().Be("Submitted");
    }

    [Fact]
    public async Task A_link_fails_once_the_entry_has_left_the_state()
    {
        var s = await SubmittedEntryAsync();
        var client = EnabledClient();
        var token = Token(s, "Approve");
        (await client.GetAsync(Url(token))).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var moved = await scope.ServiceProvider.GetRequiredService<IContentTransitioner>().TransitionAsync(
                (await session.LoadAsync<Content>(s.EntryId))!, "Reject",
                ContentTransitionActor.ForSystem("test-setup"),
                new ContentTransitionOptions
                {
                    SkipPermissionChecks = true,
                    Data = new Dictionary<string, object> { ["RejectionReason"] = "no" },
                });
            moved.Outcome.Should().Be(ContentTransitionOutcome.Transitioned, string.Join(" ", moved.Errors));
        }

        (await client.GetAsync(Url(token))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync(Url(token), new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_transition_that_requires_a_note_refuses_the_confirmation_without_one_and_leaves_the_link_usable()
    {
        var s = await SubmittedEntryAsync();
        var client = EnabledClient();
        var token = Token(s, "Reject");

        var preview = await client.GetAsync(Url(token));
        (await preview.Content.ReadAsStringAsync()).Should().Contain("RejectionReason");

        var without = await client.PostAsJsonAsync(Url(token), new { });
        without.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await EntryAsync(s.EntryId)).LifecycleState.Should().Be("Submitted");

        var with = await client.PostAsJsonAsync(Url(token), new { data = new { RejectionReason = "no receipt" } });
        with.StatusCode.Should().Be(HttpStatusCode.OK, await with.Content.ReadAsStringAsync());
        (await EntryAsync(s.EntryId)).LifecycleState.Should().Be("Rejected");
    }

    [Fact]
    public async Task Action_links_are_off_by_default()
    {
        var s = await SubmittedEntryAsync();
        var client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var issuer = scope.ServiceProvider.GetRequiredService<ActionLinkTokenIssuer>();
        issuer.Enabled.Should().BeFalse();
        issuer.Issue(s.EntryId, "Approve", s.Reviewer.Id, "Submitted", DateTimeOffset.UtcNow).Should().BeNull();

        var enabledToken = Token(s, "Approve");
        (await client.PostAsJsonAsync(Url(enabledToken), new { })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await EntryAsync(s.EntryId)).LifecycleState.Should().Be("Submitted");
    }

    [Fact]
    public async Task The_placeholder_resolves_to_a_link_for_the_recipient_only_when_enabled()
    {
        var s = await SubmittedEntryAsync();
        const string template = "{{links.action \"Approve\"}}";

        async Task<string> ResolveAsync(WebApplicationFactory<Program> host)
        {
            using var scope = host.Services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var extractor = scope.ServiceProvider.GetRequiredService<ITemplateVariableExtractor>();
            var entry = (await session.LoadAsync<Content>(s.EntryId))!;
            await extractor.PrepareAsync(entry, null, 0, [template]);
            await extractor.ForRecipientAsync(s.Reviewer.Email, entry);
            return extractor.ResolveVariables(template, entry);
        }

        var on = await ResolveAsync(Enabled());
        on.Should().StartWith("https://cms.example.com/api/public/action-links/");

        var token = on["https://cms.example.com/api/public/action-links/".Length..];
        var client = EnabledClient();
        (await client.GetAsync(Url(token))).StatusCode.Should().Be(HttpStatusCode.OK);

        var off = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://cms.example.com" })));
        (await ResolveAsync(off)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_later_action_without_a_recipient_gets_no_link_signed_for_the_earlier_one()
    {
        var s = await SubmittedEntryAsync();
        const string template = "{{links.action \"Approve\"}}";
        var parameters = new Dictionary<string, string> { ["To"] = s.Reviewer.Email, ["Body"] = template };
        var other = new Dictionary<string, string> { ["Url"] = template };

        using var scope = Enabled().Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var extractor = scope.ServiceProvider.GetRequiredService<ITemplateVariableExtractor>();
        var entry = (await session.LoadAsync<Content>(s.EntryId))!;

        var email = await barakoCMS.Features.Workflows.ActionParameters.ResolveAsync(
            extractor, "Email", parameters, entry, null, 0, CancellationToken.None);
        email["Body"].Should().StartWith("https://cms.example.com/api/public/action-links/");

        var webhook = await barakoCMS.Features.Workflows.ActionParameters.ResolveAsync(
            extractor, "Webhook", other, entry, null, 0, CancellationToken.None);
        webhook["Url"].Should().BeEmpty();
    }
}
