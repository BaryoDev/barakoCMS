using System.Net;
using System.Text.Json;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Caching;
using barakoCMS.Infrastructure.Multitenancy;
using BarakoCMS.CdnPurge;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The CdnPurge action and the Cloudflare client, driven against a fake handler so the claims are
/// about the bytes sent: which tags, in which tenant, and that the token goes nowhere but the header.
/// </summary>
public class CdnPurgeTests
{
    private const string Token = "cf-token-ThisIsNotARealToken-0123456789";

    private sealed class FakeCloudflare(HttpStatusCode status = HttpStatusCode.OK, string body = """{"success":true}""")
        : HttpMessageHandler
    {
        public List<(Uri? Uri, string? Auth, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.RequestUri, request.Headers.Authorization?.ToString(),
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }

    private static CloudflarePurger Purger(FakeCloudflare handler, ILogger<CloudflarePurger>? logger = null) =>
        new(new HttpClient(handler),
            Options.Create(new CdnPurgeOptions { ZoneId = "zone1", ApiToken = Token }),
            logger ?? new CapturingLogger<CloudflarePurger>());

    private static CdnPurgeAction Action(string tenant, ICdnPurger purger) =>
        new(purger, new TenantContext { Slug = tenant });

    private static barakoCMS.Models.Content Entry(Guid id, string type = "post") =>
        new() { Id = id, ContentType = type };

    private static string[] SentTags(FakeCloudflare handler) =>
        handler.Calls.SelectMany(c => JsonDocument.Parse(c.Body).RootElement.GetProperty("tags")
            .EnumerateArray().Select(t => t.GetString()!)).ToArray();

    [Fact]
    public async Task Publishing_an_entry_purges_its_entry_type_and_sitemap_tags_in_its_tenant()
    {
        var id = Guid.NewGuid();
        var handler = new FakeCloudflare();

        var result = await Action("acme", Purger(handler)).RunAsync([], Entry(id), CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        handler.Calls.Should().HaveCount(1);
        handler.Calls[0].Uri!.ToString().Should().Be("https://api.cloudflare.com/client/v4/zones/zone1/purge_cache");
        SentTags(handler).Should().BeEquivalentTo(
            $"t:acme:entry:{id:D}", "t:acme:type:post", "t:acme:sitemap");
    }

    [Fact]
    public async Task The_purge_names_the_same_tags_a_delivery_read_emits()
    {
        var id = Guid.NewGuid();
        var (emitted, _) = DeliveryCache.TagHeaderValue("acme", [CacheScope.Entry(id), CacheScope.Type("post")]);
        var handler = new FakeCloudflare();

        await Action("acme", Purger(handler)).RunAsync([], Entry(id), CancellationToken.None);

        var emittedTags = emitted.Split(' ');
        emittedTags.Should().HaveCount(3);
        SentTags(handler).Should().Contain(emittedTags.Where(t => t != "t:acme"));
    }

    [Fact]
    public async Task A_tag_never_lacks_the_tenant_prefix_so_tenant_a_cannot_purge_tenant_b()
    {
        var handler = new FakeCloudflare();

        await Action("tenant-a", Purger(handler)).RunAsync([], Entry(Guid.NewGuid(), "t:tenant-b:type:post"), CancellationToken.None);

        var tags = SentTags(handler);
        tags.Should().NotBeEmpty();
        tags.Should().OnlyContain(t => t.StartsWith("t:tenant-a:", StringComparison.Ordinal));
        tags.Should().NotContain(t => t.StartsWith("t:tenant-b", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_bare_tenant_tag_is_not_purged_because_it_would_empty_the_tenant()
    {
        var handler = new FakeCloudflare();

        await Action("acme", Purger(handler)).RunAsync([], Entry(Guid.NewGuid()), CancellationToken.None);

        var tags = SentTags(handler);
        tags.Should().NotBeEmpty();
        tags.Should().NotContain("t:acme");
    }

    [Fact]
    public async Task A_rejected_purge_fails_the_action_with_the_status_and_reason()
    {
        var handler = new FakeCloudflare(HttpStatusCode.Forbidden,
            """{"success":false,"errors":[{"code":10000,"message":"Authentication error"}]}""");

        var result = await Action("acme", Purger(handler)).RunAsync([], Entry(Guid.NewGuid()), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.ResponseStatus.Should().Be(403);
        result.Error.Should().Contain("403").And.Contain("Authentication error");
        result.Retryable.Should().BeFalse();
    }

    [Fact]
    public async Task A_server_error_from_the_cdn_fails_the_action_and_can_be_retried()
    {
        var handler = new FakeCloudflare(HttpStatusCode.BadGateway, "bad gateway");

        var result = await Action("acme", Purger(handler)).RunAsync([], Entry(Guid.NewGuid()), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeTrue();
    }

    [Fact]
    public async Task A_module_with_no_zone_or_token_fails_the_action_without_calling_the_cdn()
    {
        var handler = new FakeCloudflare();
        var purger = new CloudflarePurger(new HttpClient(handler), Options.Create(new CdnPurgeOptions()),
            new CapturingLogger<CloudflarePurger>());

        var result = await Action("acme", purger).RunAsync([], Entry(Guid.NewGuid()), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        handler.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task The_token_is_only_in_the_bearer_header_and_not_in_the_body_the_error_or_the_logs()
    {
        var logger = new CapturingLogger<CloudflarePurger>();
        var handler = new FakeCloudflare(HttpStatusCode.Unauthorized,
            $$"""{"success":false,"errors":[{"message":"bad token {{Token}}"}]}""");

        var result = await Action("acme", Purger(handler, logger)).RunAsync([], Entry(Guid.NewGuid()), CancellationToken.None);

        handler.Calls.Should().HaveCount(1);
        handler.Calls[0].Auth.Should().Be("Bearer " + Token);
        handler.Calls[0].Body.Should().NotContain(Token);
        result.Error.Should().NotBeNull().And.NotContain(Token);
        logger.Lines.Should().NotBeEmpty();
        logger.Lines.Should().NotContain(l => l.Contains(Token));
    }

    [Fact]
    public async Task More_than_thirty_tags_go_out_in_batches_the_api_accepts()
    {
        var handler = new FakeCloudflare();
        var tags = Enumerable.Range(0, 65).Select(i => $"t:acme:entry:{i}").ToArray();

        var result = await Purger(handler).PurgeTagsAsync(tags, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        handler.Calls.Should().HaveCount(3);
        SentTags(handler).Should().BeEquivalentTo(tags);
    }
}
