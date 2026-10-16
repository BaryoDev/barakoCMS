using BarakoCMS.Push;
using Xunit;

namespace BarakoCMS.Tests.Push;

public class PushNotificationTests
{
    [Fact]
    public void Oversize_payload_is_rejected()
    {
        var n = new PushNotification("T", new string('x', 4000));
        Assert.Throws<ArgumentException>(() => n.ToJson());
    }

    [Fact]
    public void Payload_is_json_with_title_body_and_url()
    {
        var json = new PushNotification("T", "B", "/me").ToJson();
        var doc = System.Text.Json.JsonDocument.Parse(json).RootElement;
        Assert.Equal("T", doc.GetProperty("title").GetString());
        Assert.Equal("B", doc.GetProperty("body").GetString());
        Assert.Equal("/me", doc.GetProperty("url").GetString());
    }

    [Theory]
    [InlineData("/my-club/me", "/my-club/me")]
    [InlineData("/", "/")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("https://evil.example/x", "/")]
    [InlineData("//evil.example/x", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("me", "/")]
    [InlineData("/\\evil.com", "/")]
    [InlineData("/\t/evil.com", "/")]
    [InlineData("/%5Cevil.com", "/")]
    [InlineData("/%5cevil.com", "/")]
    [InlineData("/x\ny", "/")]
    [InlineData("/x\u007fy", "/")]
    [InlineData("/me/dues?x=1", "/me/dues?x=1")]
    public void SafeUrl_keeps_only_same_origin_paths(string? input, string expected) =>
        Assert.Equal(expected, PushNotification.SafeUrl(input));
}
