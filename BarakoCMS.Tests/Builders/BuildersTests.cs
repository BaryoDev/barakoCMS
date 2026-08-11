using Xunit;
using FluentAssertions;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Builders;

/// <summary>
/// Tests for the builders themselves.
///
/// A builder is fixture code, and a wrong one is worse than a wrong test: it makes other tests pass
/// for reasons nobody stated. The defaults are the part worth pinning — a user who cannot actually
/// sign in, or content that is Published when the test assumed Draft, would turn real assertions into
/// decoration.
/// </summary>
public class BuildersTests
{
    [Fact]
    public void Content_defaults_to_an_unpublished_public_entry()
    {
        var c = new ContentBuilder().Build();

        // Draft is the safe default: a test that forgets to say Published cannot accidentally assert
        // against something the delivery API would serve.
        c.Status.Should().Be(ContentStatus.Draft);
        c.Sensitivity.Should().Be(SensitivityLevel.Public);
        c.Id.Should().NotBeEmpty();
    }

    [Fact]
    public void Content_reads_as_the_sentence_the_test_is_making()
    {
        var c = new ContentBuilder()
            .OfType("post")
            .WithTitleAndSlug("Hello World")
            .Published()
            .Sensitive()
            .Build();

        c.ContentType.Should().Be("post");
        c.Status.Should().Be(ContentStatus.Published);
        c.Sensitivity.Should().Be(SensitivityLevel.Sensitive);
        c.Data["Title"].Should().Be("Hello World");
        c.Data["Slug"].Should().Be("hello-world");
    }

    [Fact]
    public void Content_ids_are_unique_so_two_builds_are_two_entries()
    {
        new ContentBuilder().Build().Id.Should().NotBe(new ContentBuilder().Build().Id);
    }

    [Fact]
    public void ContentType_generates_a_unique_name_when_none_is_given()
    {
        var a = new ContentTypeBuilder().Build();
        var b = new ContentTypeBuilder().Build();

        // Tests share one database, so two types with the same name would collide across files.
        a.Name.Should().NotBe(b.Name);
        a.DisplayName.Should().Be(a.Name);
    }

    [Fact]
    public void ContentType_carries_its_fields_and_their_sensitivity()
    {
        var t = new ContentTypeBuilder().Named("post").WithTitleAndSlug().WithSensitiveField().Build();

        t.Fields.Should().HaveCount(3);
        t.Fields.Single(f => f.Name == "Slug").Type.Should().Be("slug");
        t.Fields.Single(f => f.Name == "Secret").Sensitivity.Should().Be(SensitivityLevel.Sensitive);
        t.Fields.Single(f => f.Name == "Title").Sensitivity.Should().Be(SensitivityLevel.Public);
    }

    [Fact]
    public void User_can_always_sign_in_with_the_default_password()
    {
        var u = new UserBuilder().Build();

        // Several hand-written fixtures seeded a user with no hash at all; a later sign-in then failed
        // in a way that read as broken authentication rather than an invalid fixture.
        u.PasswordHash.Should().NotBeNullOrEmpty();
        BCrypt.Net.BCrypt.Verify(UserBuilder.DefaultPassword, u.PasswordHash).Should().BeTrue();
        u.Email.Should().Contain("@");
    }

    [Fact]
    public void User_honours_an_explicit_password()
    {
        var u = new UserBuilder().WithPassword("Sup3rSecret!Long").Build();

        BCrypt.Net.BCrypt.Verify("Sup3rSecret!Long", u.PasswordHash).Should().BeTrue();
        BCrypt.Net.BCrypt.Verify(UserBuilder.DefaultPassword, u.PasswordHash).Should().BeFalse();
    }

    [Fact]
    public void User_can_be_built_already_locked_out()
    {
        var u = new UserBuilder().LockedOut().Build();

        u.LockoutUntil.Should().NotBeNull();
        u.LockoutUntil!.Value.Should().BeAfter(DateTime.UtcNow);
        u.FailedLoginAttempts.Should().Be(5);
    }

    [Fact]
    public void JournalEntry_balances_when_debits_equal_credits()
    {
        var e = new JournalEntryBuilder().Debit("1000", 1500.50m).Credit("4000", 1500.50m);

        e.Imbalance().Should().Be(0m);
        e.Build()["Lines"].Should().BeOfType<object[]>().Which.Should().HaveCount(2);
    }

    [Fact]
    public void JournalEntry_reports_the_imbalance_it_was_given()
    {
        // The posting rules must reject this; the builder's job is to make the intent obvious.
        new JournalEntryBuilder().Debit("1000", 100m).Credit("4000", 99m)
            .Imbalance().Should().Be(1m);
    }

    [Fact]
    public void JournalEntry_keeps_fractions_exact()
    {
        // Three tenths summed as double is 0.30000000000000004. As decimal it is 0.3, and a ledger
        // that cannot say that has no business holding money.
        var e = new JournalEntryBuilder()
            .Debit("1000", 0.1m).Debit("1000", 0.2m)
            .Credit("4000", 0.3m);

        e.Imbalance().Should().Be(0m);
    }

    [Fact]
    public void JournalEntry_defaults_are_complete_enough_to_post()
    {
        var data = new JournalEntryBuilder().Debit("1000", 1m).Credit("4000", 1m).Build();

        data.Should().ContainKeys("EntryNumber", "Date", "Lines");
        data["EntryNumber"].ToString().Should().NotBeNullOrWhiteSpace();
        // yyyy-MM-dd, the format the date field expects.
        DateTime.TryParse(data["Date"].ToString(), out _).Should().BeTrue();
    }
}
