using System.Net.Http.Headers;
using System.Text;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// What of a connector send may be written down, without a database or a provider in the way.
/// </summary>
public class ConnectorDeliveryRedactionTests
{
    private const string Marker = ConnectorDeliveryRedaction.Marker;

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Proxy-Authorization")]
    [InlineData("X-Api-Key")]
    [InlineData("api_key")]
    [InlineData("X-Auth-Token")]
    [InlineData("X-Client-Secret")]
    [InlineData("X-Access-Key")]
    [InlineData("Cookie")]
    [InlineData("X-Hub-Signature-256")]
    [InlineData("X-Functions-Key")]
    [InlineData("X-Master-Key")]
    [InlineData("Ocp-Apim-Subscription-Key")]
    [InlineData("key")]
    public void A_header_whose_name_reads_as_a_credential_is_one(string name) =>
        ConnectorDeliveryRedaction.IsCredentialHeader(name).Should().BeTrue();

    [Theory]
    [InlineData("Content-Type")]
    [InlineData("Accept")]
    [InlineData("Idempotency-Key")]
    [InlineData("X-Trace")]
    [InlineData("User-Agent")]
    [InlineData("X-Idempotency-Key")]
    [InlineData("X-Monkey")]
    public void An_ordinary_header_is_not(string name) =>
        ConnectorDeliveryRedaction.IsCredentialHeader(name).Should().BeFalse();

    [Fact]
    public void A_header_the_request_carries_and_the_composer_did_not_write_loses_its_value_whatever_its_name()
    {
        var composed = new ComposedRequest(
            "POST", "https://api.example/things", new() { ["X-Trace"] = "trace-1", ["X-Region"] = "eu" }, "{}", "application/json");

        using var request = Request(composed);
        request.Headers.TryAddWithoutValidation("X-Partner-Ref", "attached-by-the-sender");
        request.Headers.TryAddWithoutValidation("X-Region", "added-to-a-composed-header");

        var secrets = new HashSet<string>();
        var headers = ConnectorDeliveryRedaction.Headers(request, composed, secrets);

        headers.Should().ContainKeys("X-Trace", "X-Partner-Ref", "X-Region", "Content-Type");
        headers["X-Trace"].Should().Be("trace-1");
        headers["X-Partner-Ref"].Should().Be(Marker);
        headers["X-Region"].Should().Be(Marker, "a second value under a composed name was not composed");
        secrets.Should().Contain("attached-by-the-sender");
        secrets.Should().Contain("added-to-a-composed-header");
        secrets.Should().NotContain("eu", "the composed half of that header is an ordinary word, not something to cut out of an answer");
    }

    [Fact]
    public void A_composed_header_that_holds_a_credential_under_an_ordinary_name_loses_its_value()
    {
        var composed = new ComposedRequest(
            "GET", "https://api.example/things",
            new() { ["X-Note"] = "sent with tok-12345 attached", ["X-Trace"] = "trace-1" }, null, null);

        using var request = Request(composed);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "tok-12345");

        var secrets = new HashSet<string>();
        var headers = ConnectorDeliveryRedaction.Headers(request, composed, secrets);

        headers.Should().ContainKeys("Authorization", "X-Note", "X-Trace");
        headers["Authorization"].Should().Be(Marker);
        headers["X-Note"].Should().Be(Marker, "its value quotes the token");
        headers["X-Trace"].Should().Be("trace-1");
        secrets.Should().Contain("tok-12345", "the token alone is looked for, not only with its scheme");
        secrets.Should().Contain("Bearer tok-12345");
    }

    /// <summary>
    /// Red with the validating enumerator: it hands a comma separated value back as two and a
    /// quality factor back respaced, so neither matched what was composed, both were redacted, and
    /// their parts were then cut out of the stored answer.
    /// </summary>
    [Fact]
    public void An_ordinary_header_with_several_values_or_a_quality_factor_is_kept_and_adds_no_secret()
    {
        var composed = new ComposedRequest(
            "GET", "https://api.example/things",
            new()
            {
                ["Accept"] = "application/json, text/plain",
                ["Accept-Language"] = "en;q=0.9,fr;q=0.8",
                ["X-Trace"] = "trace-1",
            },
            null, null);

        using var request = Request(composed);

        var secrets = new HashSet<string>();
        var headers = ConnectorDeliveryRedaction.Headers(request, composed, secrets);

        headers.Should().ContainKeys("Accept", "Accept-Language", "X-Trace");
        headers["Accept"].Should().Be("application/json, text/plain");
        headers["Accept-Language"].Should().Be("en;q=0.9,fr;q=0.8");
        secrets.Should().BeEmpty("nothing here is a credential, so nothing may be cut out of the answer");
    }

    [Theory]
    [InlineData("X-Functions-Key")]
    [InlineData("X-Master-Key")]
    [InlineData("Ocp-Apim-Subscription-Key")]
    public void A_composed_key_header_loses_its_value_and_the_value_is_looked_for(string name)
    {
        var composed = new ComposedRequest(
            "GET", "https://api.example/things", new() { [name] = "written-key-123", ["Idempotency-Key"] = "run-0" }, null, null);

        using var request = Request(composed);

        var secrets = new HashSet<string>();
        var headers = ConnectorDeliveryRedaction.Headers(request, composed, secrets);

        headers.Should().ContainKeys(name, "Idempotency-Key");
        headers[name].Should().Be(Marker);
        headers["Idempotency-Key"].Should().Be("run-0", "it is what a receiver joins on, and it is not a credential");
        secrets.Should().Contain("written-key-123");
        secrets.Should().NotContain("run-0");
    }

    [Fact]
    public void Credential_named_query_parameters_and_the_user_info_of_the_url_are_looked_for()
    {
        var composed = new ComposedRequest(
            "GET", "https://svc:p%40ss@api.example/things?api_key=a%2Bb&key=g-1&page=2&idempotency_key=i-1",
            new(), null, null);

        var secrets = ConnectorDeliveryRedaction.UrlSecrets(composed);

        secrets.Should().HaveCount(7);
        secrets.Should().BeEquivalentTo("a%2Bb", "a+b", "g-1", "svc:p%40ss", "svc:p@ss", "p%40ss", "p@ss");
    }

    [Fact]
    public void Both_halves_of_a_basic_pair_are_looked_for()
    {
        var composed = new ComposedRequest("GET", "https://api.example/things", new(), null, null);
        var pair = Convert.ToBase64String(Encoding.UTF8.GetBytes("svc-user:the-password"));

        using var request = Request(composed);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", pair);

        var secrets = new HashSet<string>();
        ConnectorDeliveryRedaction.Headers(request, composed, secrets);

        secrets.Should().Contain(pair);
        secrets.Should().Contain("the-password");
        secrets.Should().Contain("svc-user:the-password");
    }

    [Fact]
    public void Credential_named_fields_of_a_json_body_are_looked_for_and_the_others_are_not()
    {
        var composed = new ComposedRequest(
            "POST", "https://api.example/things", new(),
            "{\"title\":\"hello\",\"password\":\"p-1\",\"nested\":{\"api_key\":\"k-2\",\"note\":\"plain\"},"
            + "\"tokens\":[\"t-3\"],\"secret\":\"true\"}",
            "application/json");

        var secrets = ConnectorDeliveryRedaction.BodySecrets(composed);

        secrets.Should().HaveCount(3, "a field named for a credential gives its value whatever its length, and a bare literal is not one");
        secrets.Should().BeEquivalentTo("p-1", "k-2", "t-3");
    }

    /// <summary>
    /// Red without the floor: every string under <c>credentials</c> became a secret, so "basic"
    /// was cut out of the stored answer wherever it appeared.
    /// </summary>
    [Fact]
    public void A_value_under_a_credential_named_object_needs_some_length_to_be_looked_for()
    {
        var composed = new ComposedRequest(
            "POST", "https://api.example/things", new(),
            "{\"credentials\":{\"type\":\"basic\",\"user\":\"service-user\",\"parts\":[\"ab\",\"long-part-1\"],"
            + "\"password\":\"pw\"}}",
            "application/json");

        var secrets = ConnectorDeliveryRedaction.BodySecrets(composed);

        secrets.Should().HaveCount(3);
        secrets.Should().BeEquivalentTo("service-user", "long-part-1", "pw");
    }

    [Fact]
    public void Credential_named_fields_of_a_form_body_are_looked_for_decoded_and_as_sent()
    {
        var composed = new ComposedRequest(
            "POST", "https://api.example/things", new(),
            "grant_type=password&client_secret=a%2Bb+c&title=hello", "application/x-www-form-urlencoded");

        var secrets = ConnectorDeliveryRedaction.BodySecrets(composed);

        secrets.Should().HaveCount(2);
        secrets.Should().BeEquivalentTo("a%2Bb+c", "a+b c");
    }

    [Fact]
    public void A_scrub_replaces_every_copy_and_the_longer_secret_first()
    {
        var text = "sent Bearer tok-12345, then tok-12345 again, and other things";

        var scrubbed = ConnectorDeliveryRedaction.Scrub(text, ["tok-12345", "Bearer tok-12345"], text.Length);

        scrubbed.Should().Be($"sent {Marker}, then {Marker} again, and other things");
    }

    [Fact]
    public async Task A_body_is_cut_at_the_limit_after_the_secrets_in_it_are_replaced()
    {
        const string secret = "tok-0123456789abcdef";
        var lead = new string('x', WebhookDelivery.ResponseBodyLimit - 5);
        using var content = new StringContent(lead + secret + new string('y', 500), Encoding.UTF8, "text/plain");

        var body = await ConnectorDeliveryRedaction.ReadBodyAsync(content, [secret], TestContext.Current.CancellationToken);

        body.Should().NotBeNull();
        body.Should().StartWith(lead);
        body!.Length.Should().BeLessThanOrEqualTo(WebhookDelivery.ResponseBodyLimit);
        body.Should().NotContain("tok-0", "the five characters before the cut are the start of the secret");
        body.Should().NotContain("y");
    }

    [Fact]
    public async Task A_body_with_no_secret_in_it_is_cut_at_the_limit_as_a_webhook_rows_is()
    {
        using var content = new StringContent(new string('z', WebhookDelivery.ResponseBodyLimit * 2), Encoding.UTF8, "text/plain");

        var body = await ConnectorDeliveryRedaction.ReadBodyAsync(content, [], TestContext.Current.CancellationToken);

        body.Should().HaveLength(WebhookDelivery.ResponseBodyLimit);
    }

    [Fact]
    public async Task An_empty_body_is_no_body()
    {
        using var content = new ByteArrayContent([]);

        var body = await ConnectorDeliveryRedaction.ReadBodyAsync(content, ["anything"], TestContext.Current.CancellationToken);

        body.Should().BeNull();
    }

    private static HttpRequestMessage Request(ComposedRequest composed)
    {
        var request = new HttpRequestMessage(new HttpMethod(composed.Method), composed.Url);

        foreach (var (name, value) in composed.Headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (composed.Body is not null)
        {
            request.Content = new StringContent(composed.Body, Encoding.UTF8, composed.BodyContentType ?? "application/json");
        }

        return request;
    }
}
