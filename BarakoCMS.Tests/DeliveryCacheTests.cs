using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;
using barakoCMS.Infrastructure.Caching;

namespace BarakoCMS.Tests;

/// <summary>The tag bounds, the tag escaping and the validator comparison behind every delivery read.</summary>
public class DeliveryCacheTests
{
    [Fact]
    public void Tags_past_the_bound_are_dropped_after_the_tenant_and_type_and_counted()
    {
        var scopes = Enumerable.Range(0, 100).Select(_ => CacheScope.Entry(Guid.NewGuid())).Prepend(CacheScope.Type("post"));

        var (value, dropped) = DeliveryCache.TagHeaderValue("acme", scopes);

        var tags = value.Split(' ');
        tags.Should().NotBeEmpty();
        tags.Length.Should().BeLessThanOrEqualTo(DeliveryCache.MaxTags);
        Encoding.UTF8.GetByteCount(value).Should().BeLessThanOrEqualTo(DeliveryCache.MaxTagHeaderLength);
        tags[0].Should().Be("t:acme");
        tags[1].Should().Be("t:acme:type:post");
        dropped.Should().Be(102 - tags.Length, "one tenant tag, one type tag and 100 entries were asked for");
        dropped.Should().BePositive();
    }

    [Fact]
    public void A_response_past_the_bound_says_how_many_tags_it_dropped()
    {
        var http = new DefaultHttpContext();

        DeliveryCache.Shared(http, DeliveryCacheClass.Short,
            Enumerable.Range(0, 50).Select(_ => CacheScope.Entry(Guid.NewGuid())));

        http.Response.Headers[DeliveryCache.TagsDroppedHeader].ToString().Should().NotBeEmpty();
        http.Response.Headers[DeliveryCache.SurrogateKeyHeader].ToString()
            .Should().Be(http.Response.Headers[DeliveryCache.CacheTagHeader].ToString());
    }

    [Fact]
    public void A_type_name_cannot_pose_as_another_tag_or_split_the_header()
    {
        DeliveryCache.Tag("acme", CacheScope.Type("a:entry:x y\r\nSet-Cookie"))
            .Should().Be("t:acme:type:a%3Aentry%3Ax%20y%0D%0ASet-Cookie");
        DeliveryCache.TenantTag("b c%").Should().Be("t:b%20c%25");
    }

    [Fact]
    public void The_etag_is_weak_and_differs_by_tenant_for_the_same_body()
    {
        var body = "{\"items\":[]}"u8.ToArray();

        var a = DeliveryCache.WeakETag("tenant-a", body);
        var b = DeliveryCache.WeakETag("tenant-b", body);

        a.Should().StartWith("W/\"");
        a.Should().NotBe(b);
        DeliveryCache.WeakETag("tenant-a", body).Should().Be(a);
    }

    [Theory]
    [InlineData("W/\"abc\"", true)]
    [InlineData("\"abc\"", true)]
    [InlineData("\"other\", W/\"abc\"", true)]
    [InlineData("*", true)]
    [InlineData("W/\"other\"", false)]
    public void If_none_match_uses_weak_comparison(string presented, bool notModified)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.IfNoneMatch = presented;

        DeliveryCache.IsNotModified(http.Request, "W/\"abc\"", null).Should().Be(notModified);
    }

    [Fact]
    public void If_modified_since_counts_only_without_if_none_match()
    {
        var modified = new DateTimeOffset(2026, 10, 1, 12, 0, 0, 500, TimeSpan.Zero);
        var http = new DefaultHttpContext();
        http.Request.Headers.IfModifiedSince = "Thu, 01 Oct 2026 12:00:00 GMT";

        DeliveryCache.IsNotModified(http.Request, "W/\"abc\"", modified).Should().BeTrue("Last-Modified is sent to the second");
        DeliveryCache.IsNotModified(http.Request, "W/\"abc\"", modified.AddSeconds(1)).Should().BeFalse();

        http.Request.Headers.IfNoneMatch = "W/\"other\"";
        DeliveryCache.IsNotModified(http.Request, "W/\"abc\"", modified).Should().BeFalse("If-None-Match decides when both are sent");
    }

    [Fact]
    public void A_no_store_response_keeps_no_tag_or_validator_set_before_it()
    {
        var http = new DefaultHttpContext();
        DeliveryCache.Shared(http, DeliveryCacheClass.Short, [CacheScope.Type("post")], DateTimeOffset.UtcNow);
        http.Response.Headers.ETag = "W/\"abc\"";

        DeliveryCache.NoStore(http);

        http.Response.Headers.CacheControl.ToString().Should().Be("no-store");
        http.Response.Headers[DeliveryCache.ClassHeader].ToString().Should().Be("no-store");
        http.Response.Headers.ContainsKey(DeliveryCache.SurrogateKeyHeader).Should().BeFalse();
        http.Response.Headers.ContainsKey(DeliveryCache.CacheTagHeader).Should().BeFalse();
        http.Response.Headers.ContainsKey("ETag").Should().BeFalse();
        http.Response.Headers.ContainsKey("Last-Modified").Should().BeFalse();
        DeliveryCache.IsShared(http.Response).Should().BeFalse();
    }
}
