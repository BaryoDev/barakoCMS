using System.Net;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using barakoCMS.Infrastructure.Caching;
using barakoCMS.Models;
using BarakoCMS.Tests.Features.Site;

namespace BarakoCMS.Tests.Features.Public;

/// <summary>
/// The configurable cache tag bounds (#561): the defaults are unchanged, a raised bound tags every
/// entry of a list, and a value out of range is clamped with a warning that names the key.
/// </summary>
[Collection("Integration1")]
public class DeliveryCacheTagBoundsTests(IntegrationTestFixture fixture)
{
    private const int Entries = 40;

    private readonly ShareLinkTestHost _host = new(fixture);

    private static CancellationToken Ct => ShareLinkTestHost.Ct;

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    private static string[] Tags(HttpResponseMessage response) =>
        response.Headers.TryGetValues(DeliveryCache.SurrogateKeyHeader, out var values)
            ? values.SelectMany(v => v.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray()
            : [];

    private async Task<(string Tenant, string Type, List<Guid> Ids)> ListOfFortyAsync()
    {
        var tenant = await _host.TenantAsync();
        var type = $"bnd{Guid.NewGuid():N}"[..12];
        var ids = new List<Guid>();
        var store = fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenant);
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            DisplayName = type,
            IsPubliclyDeliverable = true,
            Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" }],
        });
        for (var i = 0; i < Entries; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            session.Store(new Content
            {
                Id = id,
                ContentType = type,
                Status = ContentStatus.Published,
                Sensitivity = SensitivityLevel.Public,
                Data = new() { ["Title"] = $"entry-{i}" },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        await session.SaveChangesAsync(Ct);
        return (tenant, type, ids);
    }

    private static string Url(string type) => $"/api/public/{type}?pageSize={Entries}";

    [Fact]
    public void With_nothing_set_the_bounds_are_the_defaults_and_nothing_is_warned()
    {
        var bounds = DeliveryTagBounds.Read(Config(), out var warnings);

        bounds.Should().Be(new DeliveryTagBounds(32, 1024));
        bounds.Should().Be(DeliveryTagBounds.Default);
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void A_value_inside_the_range_is_used_as_set()
    {
        var bounds = DeliveryTagBounds.Read(Config(
            (DeliveryTagBounds.MaxTagsKey, "200"), (DeliveryTagBounds.MaxHeaderLengthKey, "8192")), out var warnings);

        bounds.Should().Be(new DeliveryTagBounds(200, 8192));
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void A_value_above_the_ceiling_is_clamped_to_it_and_the_warning_names_the_key()
    {
        var bounds = DeliveryTagBounds.Read(Config(
            (DeliveryTagBounds.MaxTagsKey, "5000"), (DeliveryTagBounds.MaxHeaderLengthKey, "99999")), out var warnings);

        bounds.MaxTags.Should().Be(DeliveryTagBounds.CeilingTags).And.Be(1000);
        bounds.MaxHeaderLength.Should().Be(DeliveryTagBounds.CeilingHeaderLength).And.Be(16384);
        warnings.Should().HaveCount(2);
        warnings.Should().ContainSingle(w => w.Contains("Delivery:CacheTags:MaxTags") && w.Contains("5000") && w.Contains("1000"));
        warnings.Should().ContainSingle(w => w.Contains("Delivery:CacheTags:MaxHeaderLength") && w.Contains("99999") && w.Contains("16384"));
    }

    [Fact]
    public void A_value_below_the_floor_is_clamped_to_it_and_the_warning_names_the_key()
    {
        var bounds = DeliveryTagBounds.Read(Config(
            (DeliveryTagBounds.MaxTagsKey, "0"), (DeliveryTagBounds.MaxHeaderLengthKey, "100")), out var warnings);

        bounds.MaxTags.Should().Be(1, "the tenant tag has to fit");
        bounds.MaxHeaderLength.Should().Be(DeliveryCache.MaxTagHeaderLength);
        warnings.Should().ContainSingle(w => w.Contains("Delivery:CacheTags:MaxTags") && w.Contains("0"));
        warnings.Should().ContainSingle(w => w.Contains("Delivery:CacheTags:MaxHeaderLength") && w.Contains("100"));
    }

    [Fact]
    public void A_value_that_is_not_a_number_takes_the_default_and_the_warning_names_the_key()
    {
        var bounds = DeliveryTagBounds.Read(Config((DeliveryTagBounds.MaxTagsKey, "lots")), out var warnings);

        bounds.Should().Be(DeliveryTagBounds.Default);
        warnings.Should().ContainSingle().Which.Should().Contain("Delivery:CacheTags:MaxTags");
    }

    [Fact]
    public async Task The_startup_check_logs_one_warning_per_clamped_key()
    {
        var logger = new CapturingLogger<DeliveryTagBoundsCheck>();
        var check = new DeliveryTagBoundsCheck(Config(
            (DeliveryTagBounds.MaxTagsKey, "5000"), (DeliveryTagBounds.MaxHeaderLengthKey, "2048")), logger);

        await check.StartAsync(Ct);

        logger.Warnings.Should().ContainSingle().Which.Should().Contain("Delivery:CacheTags:MaxTags");
    }

    [Fact]
    public void The_two_argument_overload_is_the_default_bounds()
    {
        var scopes = Enumerable.Range(0, 100).Select(i => CacheScope.Named($"k{i}")).Prepend(CacheScope.Type("post")).ToList();

        var old = DeliveryCache.TagHeaderValue("acme", scopes);
        var withDefaults = DeliveryCache.TagHeaderValue("acme", scopes, DeliveryTagBounds.Default);

        old.Should().Be(withDefaults);
        old.Value.Split(' ').Should().HaveCount(DeliveryCache.MaxTags);
        old.Dropped.Should().Be(102 - DeliveryCache.MaxTags);
    }

    [Fact]
    public void Raised_bounds_keep_every_tag_and_a_lower_bound_keeps_fewer()
    {
        var scopes = Enumerable.Range(0, 100).Select(_ => CacheScope.Entry(Guid.NewGuid())).Prepend(CacheScope.Type("post")).ToList();

        var all = DeliveryCache.TagHeaderValue("acme", scopes, new DeliveryTagBounds(200, 16384));
        var one = DeliveryCache.TagHeaderValue("acme", scopes, new DeliveryTagBounds(1, 1024));

        all.Dropped.Should().Be(0);
        all.Value.Split(' ').Should().HaveCount(102);
        one.Value.Should().Be("t:acme");
        one.Dropped.Should().Be(101);
    }

    [Fact]
    public async Task With_nothing_set_a_list_of_forty_sends_a_bounded_set_and_the_dropped_count_but_keeps_the_type_tag()
    {
        var (tenant, type, _) = await ListOfFortyAsync();

        var response = await _host.AnonymousIn(tenant).GetAsync(Url(type), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var tags = Tags(response);
        tags.Length.Should().BeLessThan(Entries, "the bound is hit before every entry is tagged");
        tags.Length.Should().BeLessThanOrEqualTo(DeliveryCache.MaxTags);
        tags.Should().Contain($"t:{tenant}:type:{type}", "the type tag still reaches the list when entry tags fall off");
        var dropped = int.Parse(response.Headers.GetValues(DeliveryCache.TagsDroppedHeader).Single());
        (dropped + tags.Length).Should().Be(1 + 1 + Entries, "the tenant tag, the type tag and every entry were asked for");
    }

    [Fact]
    public async Task With_the_bounds_raised_a_list_of_forty_tags_every_entry_and_drops_nothing()
    {
        var (tenant, type, ids) = await ListOfFortyAsync();
        var raised = fixture.WithWebHostBuilder(b =>
        {
            b.UseSetting(DeliveryTagBounds.MaxTagsKey, "200");
            b.UseSetting(DeliveryTagBounds.MaxHeaderLengthKey, "8192");
        });
        var client = raised.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ShareLinkTestHost.NextIp());

        var response = await client.GetAsync(Url(type), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var tags = Tags(response);
        tags.Should().Contain(ids.Select(id => $"t:{tenant}:entry:{id}"));
        tags.Should().Contain($"t:{tenant}:type:{type}");
        response.Headers.Contains(DeliveryCache.TagsDroppedHeader).Should().BeFalse();
        response.Headers.GetValues(DeliveryCache.CacheTagHeader).Should().Equal(response.Headers.GetValues(DeliveryCache.SurrogateKeyHeader));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
