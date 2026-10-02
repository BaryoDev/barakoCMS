using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Models;
using BarakoCMS.Forms;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Forms;

/// <summary>
/// A form named under <c>Modules:Forms:PerForm</c> has its own submit limit. Every other form stays
/// on the shared one, five per client IP per ten minutes (#888).
/// </summary>
/// <remarks>
/// A submission with no data reaches validation and is 400, so a 400 here means the limiter let the
/// request through and a 429 means it did not.
/// </remarks>
[Collection("Sequential")]
public class FormRateLimitTests
{
    private readonly IntegrationTestFixture _factory;

    public FormRateLimitTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public void With_nothing_configured_the_shared_limit_is_five_in_ten_minutes_and_no_form_has_its_own()
    {
        var options = new FormsOptions();

        options.PermitLimit.Should().Be(5);
        options.WindowSeconds.Should().Be(600);
        options.PerForm.Should().BeEmpty();
    }

    [Fact]
    public async Task A_form_with_its_own_limit_is_counted_against_that_limit_and_not_the_shared_one()
    {
        var (host, busy, _) = await LimitedHostAsync();
        var other = await CreateFormAsync();
        var visitor = Visitor(host);

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++)
            codes.Add(await SubmitAsync(visitor, busy));

        codes.Should().HaveCount(8);
        codes.Take(7).Should().OnlyContain(c => c == HttpStatusCode.BadRequest,
            "seven is past the shared limit of five, so these were counted against the form's own limit");
        codes[7].Should().Be(HttpStatusCode.TooManyRequests);

        (await SubmitAsync(visitor, other)).Should().Be(HttpStatusCode.BadRequest,
            "the eight submissions above spent nothing from the shared bucket the other form is on");
        (await SubmitAsync(Visitor(host), busy)).Should().Be(HttpStatusCode.BadRequest,
            "another address has its own bucket for the same form");
    }

    [Fact]
    public async Task Another_spelling_of_the_slug_lands_in_the_same_bucket()
    {
        var (host, _, form) = await LimitedHostAsync();
        var visitor = Visitor(host);

        (await SubmitAsync(visitor, form)).Should().Be(HttpStatusCode.BadRequest);
        (await SubmitAsync(visitor, form.ToUpperInvariant())).Should().Be(HttpStatusCode.NotFound,
            "no form has that spelling, and the request was still counted");
        (await SubmitAsync(visitor, form)).Should().Be(HttpStatusCode.TooManyRequests,
            "the upper case request spent this form's bucket, it did not open one of its own");
    }

    [Fact]
    public async Task A_form_without_its_own_limit_stays_on_the_shared_one_on_a_host_that_configures_another()
    {
        var (host, _, _) = await LimitedHostAsync();
        var plain = await CreateFormAsync();
        var visitor = Visitor(host);

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
            codes.Add(await SubmitAsync(visitor, plain));

        codes.Should().HaveCount(6);
        codes.Take(5).Should().OnlyContain(c => c == HttpStatusCode.BadRequest);
        codes[5].Should().Be(HttpStatusCode.TooManyRequests, "the shared limit is still five");
    }

    private static (WebApplicationFactory<Program> Host, string Busy, string Tight)? _limited;

    /// <summary>
    /// One host for the class, since every host a test builds stays alive for the rest of the run.
    /// Two forms have their own limit on it: seven a minute, and two in the default window. The
    /// slug has to be known before the host is built, because the limit is configuration.
    /// </summary>
    private async Task<(WebApplicationFactory<Program> Host, string Busy, string Tight)> LimitedHostAsync()
    {
        if (_limited is { } ready)
        {
            return ready;
        }

        var busy = await CreateFormAsync();
        var tight = await CreateFormAsync();
        var host = _factory.WithSettings(new Dictionary<string, string?>
        {
            [$"Modules:Forms:PerForm:{busy}:PermitLimit"] = "7",
            [$"Modules:Forms:PerForm:{busy}:WindowSeconds"] = "60",
            [$"Modules:Forms:PerForm:{tight}:PermitLimit"] = "2",
        });

        _limited = (host, busy, tight);
        return (host, busy, tight);
    }

    private static async Task<HttpStatusCode> SubmitAsync(HttpClient client, string slug)
    {
        using var response = await client.PostAsJsonAsync(
            $"/api/public/forms/{slug}", new { data = new { } }, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static HttpClient Visitor(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        var bytes = Guid.NewGuid().ToByteArray();
        var ip = $"2001:db8:888::{bytes[0]:x2}{bytes[1]:x2}:{bytes[2]:x2}{bytes[3]:x2}:{bytes[4]:x2}{bytes[5]:x2}";
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ip);
        return client;
    }

    /// <summary>A content type with one required field, marked as a form. Returns its slug.</summary>
    private async Task<string> CreateFormAsync()
    {
        var name = $"form-{Guid.NewGuid():N}";

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = name,
                DisplayName = "Registration",
                Fields =
                [
                    new FieldDefinition { Name = "name", DisplayName = "Name", Type = "string", IsRequired = true },
                ],
            });
            await session.SaveChangesAsync();
        }

        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("Admin"));
        var enabled = await admin.PutAsJsonAsync($"/api/forms/{name}", new { enabled = true }, TestContext.Current.CancellationToken);
        enabled.StatusCode.Should().Be(HttpStatusCode.OK, await enabled.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        return name;
    }
}
