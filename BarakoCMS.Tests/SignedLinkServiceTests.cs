using Xunit;
using FluentAssertions;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Links;
using barakoCMS.Infrastructure.Preview;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests;

[Collection("Integration2")]
public class SignedLinkServiceTests
{
    private readonly IntegrationTestFixture _factory;

    public SignedLinkServiceTests(IntegrationTestFixture factory) => _factory = factory;

    private sealed class MovableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private IConfiguration Config => _factory.Services.GetRequiredService<IConfiguration>();

    private (SignedLinkService Service, IServiceScope Scope) Make(MovableClock? clock = null)
    {
        var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        return (new SignedLinkService(session, Config, clock ?? new MovableClock()), scope);
    }

    [Fact]
    public async Task A_link_validates_and_returns_its_subject()
    {
        var (svc, scope) = Make();
        using var _ = scope;
        var link = svc.Issue("share", "entry-42", TimeSpan.FromMinutes(5));

        var result = await svc.ValidateAsync("share", link.Token);

        result.Succeeded.Should().BeTrue();
        result.Subject.Should().Be("entry-42");
        link.Token.Should().NotContain(".", "a signed link must never be mistaken for a JWT");
    }

    [Fact]
    public async Task An_expired_link_is_refused()
    {
        var clock = new MovableClock();
        var (svc, scope) = Make(clock);
        using var _ = scope;
        var link = svc.Issue("share", "entry-42", TimeSpan.FromMinutes(5));
        clock.Now = clock.Now.AddMinutes(6);

        var result = await svc.ValidateAsync("share", link.Token);

        result.Failure.Should().Be(SignedLinkFailure.Expired);
        result.Subject.Should().BeNull();
    }

    [Fact]
    public async Task A_tampered_link_is_refused()
    {
        var (svc, scope) = Make();
        using var _ = scope;
        var good = svc.Issue("share", "entry-1", TimeSpan.FromMinutes(5));
        var other = svc.Issue("share", "entry-2", TimeSpan.FromMinutes(5));
        var swappedPayload = other.Token.Split('~')[0] + "~" + good.Token.Split('~')[1];
        var flippedSignature = good.Token[..^2] + (good.Token[^2] == 'A' ? "B" : "A") + good.Token[^1];

        (await svc.ValidateAsync("share", swappedPayload)).Failure.Should().Be(SignedLinkFailure.Invalid);
        (await svc.ValidateAsync("share", flippedSignature)).Failure.Should().Be(SignedLinkFailure.Invalid);
        (await svc.ValidateAsync("share", "garbage")).Failure.Should().Be(SignedLinkFailure.Invalid);
    }

    [Fact]
    public async Task A_link_for_one_purpose_is_refused_for_another()
    {
        var (svc, scope) = Make();
        using var _ = scope;
        var link = svc.Issue("share", "entry-42", TimeSpan.FromMinutes(5));

        var wrong = await svc.ValidateAsync("confirm", link.Token);
        var right = await svc.ValidateAsync("share", link.Token);

        wrong.Failure.Should().Be(SignedLinkFailure.Invalid);
        right.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task A_single_use_link_is_refused_the_second_time()
    {
        var (svc, scope) = Make();
        using var _ = scope;
        var link = svc.Issue("confirm", "user-7", TimeSpan.FromMinutes(5), singleUse: true);

        var first = await svc.ValidateAsync("confirm", link.Token);
        var second = await svc.ValidateAsync("confirm", link.Token);

        first.Succeeded.Should().BeTrue();
        second.Failure.Should().Be(SignedLinkFailure.AlreadyUsed);
    }

    [Fact]
    public async Task A_link_that_is_not_single_use_validates_again()
    {
        var (svc, scope) = Make();
        using var _ = scope;
        var link = svc.Issue("share", "entry-42", TimeSpan.FromMinutes(5));

        (await svc.ValidateAsync("share", link.Token)).Succeeded.Should().BeTrue();
        (await svc.ValidateAsync("share", link.Token)).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task A_revoked_link_is_refused()
    {
        var (svc, scope) = Make();
        using var _ = scope;
        var link = svc.Issue("share", "entry-42", TimeSpan.FromMinutes(5));
        (await svc.ValidateAsync("share", link.Token)).Succeeded.Should().BeTrue();

        await svc.RevokeAsync(link.TokenId);

        (await svc.ValidateAsync("share", link.Token)).Failure.Should().Be(SignedLinkFailure.Revoked);
    }

    [Fact]
    public async Task A_preview_token_in_the_old_JWT_format_still_validates()
    {
        var (svc, scope) = Make();
        using var _ = scope;
        var entryId = Guid.NewGuid();
        var jwt = PreviewToken.Create(Config, "default", "article", "wip", entryId).Token;
        jwt.Should().Contain(".");

        var id = await PreviewToken.ValidatedEntryIdAsync(svc, Config, jwt, "default", "article", "wip");
        var otherSlug = await PreviewToken.ValidatedEntryIdAsync(svc, Config, jwt, "default", "article", "other");

        id.Should().Be(entryId);
        otherSlug.Should().BeNull();
    }

    [Fact]
    public async Task A_preview_token_issued_as_a_signed_link_validates_only_for_its_entry()
    {
        var (svc, scope) = Make();
        using var _ = scope;
        var entryId = Guid.NewGuid();
        var (token, _) = PreviewToken.Issue(svc, "default", "article", "wip", entryId);

        var id = await PreviewToken.ValidatedEntryIdAsync(svc, Config, token, "default", "article", "wip");
        var otherSlug = await PreviewToken.ValidatedEntryIdAsync(svc, Config, token, "default", "article", "other");
        var otherTenant = await PreviewToken.ValidatedEntryIdAsync(svc, Config, token, "acme", "article", "wip");

        id.Should().Be(entryId);
        otherSlug.Should().BeNull();
        otherTenant.Should().BeNull();
    }
}
