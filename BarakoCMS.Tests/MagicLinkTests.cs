using System.Net;
using System.Net.Http.Json;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Sign in with an emailed link. The "auth" rate limit is 5 requests per 15 minutes per client IP,
/// so each test class instance takes a private IP through TestRemoteIpFilter and stays under it.
/// </summary>
[Collection("Integration2")]
public class MagicLinkTests
{
    private const string RequestPath = "/api/auth/magic-link/request";
    private const string ConfirmPath = "/api/auth/magic-link/confirm";

    private readonly IntegrationTestFixture _factory;
    private readonly string _ip = $"198.51.100.{Random.Shared.Next(2, 250)}";
    private readonly WebApplicationFactory<Program> _host;
    private readonly HttpClient _client;

    public MagicLinkTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _host = factory
            .WithWebHostBuilder(b => b.ConfigureServices(s =>
                s.AddSingleton<IStartupFilter, TestRemoteIpFilter>()));
        _client = _host.CreateClient();
    }

    private async Task<string> SeedUserAsync()
    {
        var email = $"magic-{Guid.NewGuid():N}@example.com";
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        s.Store(new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            Username = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString()),
            CreatedAt = DateTime.UtcNow,
        });
        await s.SaveChangesAsync();
        return email;
    }

    private async Task<string> SeedLinkAsync(string email, TimeSpan? lifetime = null, string tenantSlug = Tenant.DefaultSlug)
    {
        var row = new MagicLinkToken
        {
            Email = email.ToLowerInvariant(),
            TenantSlug = tenantSlug,
            ExpiresAt = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(10)),
        };
        var (token, hash) = EmailVerificationToken.Create(row.Id);
        row.TokenHash = hash;
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        s.Store(row);
        await s.SaveChangesAsync();
        return token;
    }

    private async Task<MagicLinkToken?> LoadByEmailAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var s = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await s.Query<MagicLinkToken>().Where(t => t.Email == email.ToLowerInvariant()).ToListAsync())
            .OrderByDescending(t => t.CreatedAt).FirstOrDefault();
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (body is not null) req.Content = JsonContent.Create(body);
        req.Headers.Add(TestRemoteIpFilter.Header, _ip);
        return _client.SendAsync(req);
    }

    private Task<HttpResponseMessage> Confirm(string token) => Send(HttpMethod.Post, ConfirmPath, new { token });

    private Task<HttpResponseMessage> Request(string email) => Send(HttpMethod.Post, RequestPath, new { email });

    [Fact]
    public async Task A_link_signs_in_once_and_a_second_use_is_refused()
    {
        var email = await SeedUserAsync();
        var token = await SeedLinkAsync(email);

        var first = await Confirm(token);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await first.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        body.GetProperty("token").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("refreshToken").GetString().Should().NotBeNullOrEmpty();

        var second = await Confirm(token);
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Two_posts_racing_one_link_sign_in_at_most_once_and_neither_errors()
    {
        var email = await SeedUserAsync();
        var token = await SeedLinkAsync(email);

        var responses = await Task.WhenAll(Confirm(token), Confirm(token));

        responses.Should().HaveCount(2);
        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().BeLessThanOrEqualTo(1, "one link must not sign in twice");
        foreach (var r in responses)
            ((int)r.StatusCode).Should().BeLessThan(500, "a link another request already spent is a refusal, not a server error");
    }

    [Fact]
    public async Task An_expired_link_is_refused()
    {
        var email = await SeedUserAsync();
        var token = await SeedLinkAsync(email, lifetime: TimeSpan.FromMinutes(-1));

        var response = await Confirm(token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_GET_on_the_link_shows_a_confirm_step_and_neither_signs_in_nor_spends_it()
    {
        var email = await SeedUserAsync();
        var token = await SeedLinkAsync(email);

        var page = await Send(HttpMethod.Get, $"{ConfirmPath}?token={Uri.EscapeDataString(token)}");

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        page.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        (await page.Content.ReadAsStringAsync()).Should().NotContain("refreshToken");
        var row = await LoadByEmailAsync(email);
        row.Should().NotBeNull();
        row!.Consumed.Should().BeFalse("only the POST spends a link");

        var signedIn = await Confirm(token);
        signedIn.StatusCode.Should().Be(HttpStatusCode.OK, "the link still works after a GET");
    }

    [Fact]
    public async Task An_unknown_email_gets_the_same_answer_as_a_known_one_and_no_mail()
    {
        var known = await SeedUserAsync();
        var unknown = $"nobody-{Guid.NewGuid():N}@example.com";
        var mail = _host.Services.GetRequiredService<RecordingEmailService>();

        var a = await Request(known);
        var b = await Request(unknown);

        a.StatusCode.Should().Be(HttpStatusCode.OK);
        b.StatusCode.Should().Be(HttpStatusCode.OK);
        (await b.Content.ReadAsStringAsync()).Should().Be(await a.Content.ReadAsStringAsync());
        await RecordingEmailService.WaitForAsync(() => mail.LastVerificationTokenFor(known) is not null);
        await Task.Delay(300); // the queue is first in, first out, so an unknown address's mail would land by now
        mail.LastVerificationTokenFor(known).Should().NotBeNullOrEmpty("the known address was mailed a link");
        mail.Messages.Where(m => m.To == unknown.ToLowerInvariant()).Should().BeEmpty();
        (await LoadByEmailAsync(unknown)).Should().BeNull();
    }

    [Fact]
    public async Task A_requested_link_from_the_email_signs_in()
    {
        var email = await SeedUserAsync();
        var mail = _host.Services.GetRequiredService<RecordingEmailService>();

        (await Request(email)).StatusCode.Should().Be(HttpStatusCode.OK);
        await RecordingEmailService.WaitForAsync(() => mail.LastVerificationTokenFor(email) is not null);
        var token = mail.LastVerificationTokenFor(email);

        token.Should().NotBeNullOrEmpty();
        (await Confirm(token!)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_request_route_is_rate_limited_like_the_code_route()
    {
        var email = await SeedUserAsync();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 7; i++) statuses.Add((await Request(email)).StatusCode);

        statuses.Should().HaveCount(7);
        statuses.Should().Contain(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task A_link_requested_in_another_tenant_is_refused_here()
    {
        var email = await SeedUserAsync();
        var token = await SeedLinkAsync(email, tenantSlug: "some-other-tenant");

        var response = await Confirm(token);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await LoadByEmailAsync(email))!.Consumed.Should().BeFalse("a refused link is not spent");
    }
}
