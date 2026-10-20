using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Features.Public.Events;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using BarakoCMS.Tests.Builders;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Two hosts on one database stand in for two API instances. A write through one is read off a
/// subscription registered on the other, which is where the stream used to go quiet (#566).
/// </summary>
/// <remarks>
/// "Receives nothing" is asserted with a positive control, as in ContentEventStreamTests: the
/// change that must be silent is followed by one that must not be, and the next change off the
/// subscription has to be the control.
/// </remarks>
[Collection("Sequential")]
public class ContentEventBusTests
{
    private readonly IntegrationTestFixture _factory;

    public ContentEventBusTests(IntegrationTestFixture factory) => _factory = factory;

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(20);

    private async Task<(WebApplicationFactory<Program> Writer, WebApplicationFactory<Program> Reader)> TwoInstancesAsync()
    {
        var settings = new Dictionary<string, string?> { ["Delivery:Events:Enabled"] = "true" };
        var writer = _factory.WithSettings(settings);
        var reader = _factory.WithSettings(settings);

        foreach (var host in new[] { writer, reader })
        {
            _ = host.Services;
            await host.Services.GetRequiredService<ContentChangeBus>().Listening.WaitAsync(ReadTimeout);
        }

        return (writer, reader);
    }

    private static ContentChangeBroadcaster.Subscription Subscribe(WebApplicationFactory<Program> host, string tenant)
    {
        var subscription = host.Services.GetRequiredService<ContentChangeBroadcaster>()
            .TrySubscribe(tenant, [], "bus-test", 100, 0, out _);
        subscription.Should().NotBeNull();
        return subscription!;
    }

    private static async Task<ContentChange> NextAsync(ContentChangeBroadcaster.Subscription subscription) =>
        await subscription.Reader.ReadAsync().AsTask().WaitAsync(ReadTimeout);

    private static async Task SeedTypeAsync(WebApplicationFactory<Program> host, string type, string tenant)
    {
        using var scope = host.Services.CreateScopeForTenant(tenant);
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeBuilder()
            .Named(type)
            .PubliclyDeliverable()
            .WithTitleAndSlug()
            .WithSensitiveField()
            .Build());
        await session.SaveChangesAsync();
    }

    private static async Task<Content> WriteAsync(
        WebApplicationFactory<Program> host, string tenant, Func<IContentWriter, Task<Content>> act)
    {
        using var scope = host.Services.CreateScopeForTenant(tenant);
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var writer = scope.ServiceProvider.GetRequiredService<IContentWriter>();
        var content = await act(writer);
        await session.SaveChangesAsync();
        return content;
    }

    private static Task<Content> PublishNewAsync(WebApplicationFactory<Program> host, string tenant, string type, string slug) =>
        WriteAsync(host, tenant, w => w.CreateAsync(
            new ContentCreated(
                Guid.NewGuid(), type,
                new Dictionary<string, object> { ["Title"] = "Bus " + slug, ["Slug"] = slug, ["Secret"] = "topsecret" },
                ContentStatus.Published, Guid.NewGuid(), "Bus " + slug, SensitivityLevel.Public, DateTime.UtcNow),
            default));

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("n")[..8];

    [Fact]
    public async Task A_publish_on_one_instance_reaches_a_subscriber_on_another_with_masking_applied()
    {
        var (writer, reader) = await TwoInstancesAsync();
        var tenant = Unique("busa");
        var type = Unique("bus_");
        var slug = Unique("s-");
        await SeedTypeAsync(writer, type, tenant);
        using var subscription = Subscribe(reader, tenant);

        var written = await PublishNewAsync(writer, tenant, type, slug);

        var change = await NextAsync(subscription);
        change.EventName.Should().Be(ContentChangeEvents.Published);
        change.Id.Should().Be(written.Id);
        change.Slug.Should().Be(slug);
        var json = System.Text.Json.JsonSerializer.Serialize(change.Payload);
        json.Should().Contain("Bus " + slug);
        json.Should().NotContain("topsecret", "a Sensitive field is masked on the receiving instance too");
    }

    [Fact]
    public async Task An_unpublish_on_one_instance_reaches_a_subscriber_on_another_as_identity_only()
    {
        var (writer, reader) = await TwoInstancesAsync();
        var tenant = Unique("busu");
        var type = Unique("bus_");
        var slug = Unique("s-");
        await SeedTypeAsync(writer, type, tenant);
        var written = await PublishNewAsync(writer, tenant, type, slug);
        using var subscription = Subscribe(reader, tenant);

        await WriteAsync(writer, tenant, async w =>
        {
            await w.AppendAsync(written, new ContentStatusChanged(written.Id, ContentStatus.Archived, Guid.NewGuid(), DateTime.UtcNow), default);
            return written;
        });

        var change = await NextAsync(subscription);
        change.EventName.Should().Be(ContentChangeEvents.Unpublished);
        change.Id.Should().Be(written.Id);
        change.Payload.Should().BeOfType<UnpublishedPayload>();
    }

    [Fact]
    public async Task A_change_on_another_tenant_over_the_bus_never_reaches_a_subscriber()
    {
        var (writer, reader) = await TwoInstancesAsync();
        var tenantA = Unique("busta");
        var tenantB = Unique("bustb");
        var type = Unique("bus_");
        await SeedTypeAsync(writer, type, tenantA);
        await SeedTypeAsync(writer, type, tenantB);
        using var subscription = Subscribe(reader, tenantA);

        var inB = await PublishNewAsync(writer, tenantB, type, Unique("s-"));
        var control = await PublishNewAsync(writer, tenantA, type, Unique("s-"));

        var change = await NextAsync(subscription);
        change.Id.Should().Be(control.Id, "the first change is tenant A's own, so tenant B's never arrived");
        change.Id.Should().NotBe(inB.Id);
    }

    [Fact]
    public async Task A_write_is_not_delivered_twice_to_a_subscriber_on_the_writing_instance()
    {
        var (writer, _) = await TwoInstancesAsync();
        var tenant = Unique("busw");
        var type = Unique("bus_");
        await SeedTypeAsync(writer, type, tenant);
        using var subscription = Subscribe(writer, tenant);

        var first = await PublishNewAsync(writer, tenant, type, Unique("s-"));
        var second = await PublishNewAsync(writer, tenant, type, Unique("s-"));

        (await NextAsync(subscription)).Id.Should().Be(first.Id);
        (await NextAsync(subscription)).Id.Should().Be(second.Id, "the instance ignores its own notice, so the next change is the next write");
    }
}
