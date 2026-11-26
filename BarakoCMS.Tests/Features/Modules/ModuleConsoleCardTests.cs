using System.Text.Json;
using barakoCMS.Modules;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests.Features.Modules;

/// <summary>
/// The rules the catalogue applies to <see cref="IBarakoModule.ConsoleCards"/>. A broken card is
/// dropped and a warning is written, and the module itself still lists.
/// </summary>
public class ModuleConsoleCardTests
{
    private sealed class CardModule(params ConsoleCard[]? cards) : IBarakoModule
    {
        public string Name => "Reports";
        public IReadOnlyList<ConsoleCard> ConsoleCards { get; } = cards!;
    }

    private static (IReadOnlyList<ConsoleCard> Cards, List<string> Warnings) Run(params ConsoleCard[]? cards)
    {
        var warnings = new List<string>();
        var module = new CardModule(cards);
        var entry = ModuleCatalogue.Of([module], [module], warnings.Add).Entries.Single();
        return (entry.Cards, warnings);
    }

    private static ConsoleCard Card(string id = "recent", string title = "Recent", string route = "/api/reports/summary")
        => new(id, title, route);

    [Fact]
    public void A_module_that_declares_nothing_has_no_cards()
    {
        var entry = ModuleCatalogue.Of([new CardModule()], [new CardModule()]).Entries.Single();

        entry.Cards.Should().BeEmpty();
    }

    [Fact]
    public void A_valid_card_is_kept_with_its_title_trimmed()
    {
        var (cards, warnings) = Run(Card(title: "  Recent reports  "));

        cards.Should().ContainSingle().Which.Should().Be(new ConsoleCard("recent", "Recent reports", "/api/reports/summary"));
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void A_null_list_is_treated_as_none()
    {
        var (cards, warnings) = Run(null);

        cards.Should().BeEmpty();
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void A_null_entry_is_skipped_and_the_others_kept()
    {
        var (cards, _) = Run(null!, Card());

        cards.Should().ContainSingle();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Recent")]
    [InlineData("-recent")]
    [InlineData("re cent")]
    [InlineData("re_cent")]
    [InlineData("recent\n")]
    [InlineData("a1234567890123456789012345678901234567890")]
    public void An_id_outside_the_pattern_drops_the_card(string id)
    {
        var (cards, warnings) = Run(Card(id: id));

        cards.Should().BeEmpty();
        warnings.Should().ContainSingle().Which.Should().Contain("Reports");
    }

    [Fact]
    public void An_id_of_forty_characters_is_kept()
    {
        Run(Card(id: new string('a', 40))).Cards.Should().ContainSingle();
    }

    [Fact]
    public void A_second_card_with_the_same_id_is_dropped()
    {
        var (cards, warnings) = Run(Card(title: "First"), Card(title: "Second"));

        cards.Should().ContainSingle().Which.Title.Should().Be("First");
        warnings.Should().ContainSingle().Which.Should().Contain("'recent'");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bad\u0007title")]
    [InlineData("Line\nbreak")]
    public void A_title_that_is_empty_or_holds_control_characters_drops_the_card(string title)
    {
        Run(Card(title: title)).Cards.Should().BeEmpty();
    }

    [Fact]
    public void A_title_of_sixty_characters_is_kept_and_sixty_one_is_not()
    {
        Run(Card(title: new string('t', 60))).Cards.Should().ContainSingle();
        Run(Card(title: new string('t', 61))).Cards.Should().BeEmpty();
    }

    [Fact]
    public void A_refresh_label_is_kept_trimmed_and_listed()
    {
        var (cards, warnings) = Run(new ConsoleCard("recent", "Recent", "/api/reports/summary", "  Refresh  "));

        cards.Should().ContainSingle().Which.RefreshLabel.Should().Be("Refresh");
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void A_null_refresh_label_keeps_the_card_with_no_button()
    {
        var (cards, warnings) = Run(Card());

        cards.Should().ContainSingle().Which.RefreshLabel.Should().BeNull();
        warnings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bad\u0007label")]
    [InlineData("Zero\u200Bwidth")]
    [InlineData("abcdefghijklmnopqrstuvwxy")]
    public void A_refresh_label_that_breaks_the_rule_drops_the_card_with_a_warning(string label)
    {
        var (cards, warnings) = Run(new ConsoleCard("recent", "Recent", "/api/reports/summary", label));

        cards.Should().BeEmpty();
        warnings.Should().ContainSingle().Which.Should().Contain("Reports").And.Contain("'recent'").And.Contain("refresh label");
    }

    [Fact]
    public void A_refresh_label_is_trimmed_before_its_length_is_checked()
    {
        var label = new string('r', 24);

        var (cards, warnings) = Run(new ConsoleCard("recent", "Recent", "/api/reports/summary", "  " + label + "  "));

        cards.Should().ContainSingle().Which.RefreshLabel.Should().Be(label);
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void A_refresh_label_of_twenty_four_characters_is_kept()
    {
        Run(new ConsoleCard("recent", "Recent", "/api/reports/summary", new string('r', 24))).Cards.Should().ContainSingle();
    }

    [Theory]
    [InlineData("/api/reports")]
    [InlineData("/api/reports/summary")]
    [InlineData("/api/my-reports/by_day/today")]
    public void A_route_under_api_is_kept(string route)
    {
        Run(Card(route: route)).Cards.Should().ContainSingle();
    }

    [Theory]
    [InlineData("/api/reports/../admin")]
    [InlineData("/api/reports/..")]
    [InlineData("/api//reports")]
    [InlineData("/api/reports//summary")]
    [InlineData("/api/reports?x=1")]
    [InlineData("/api/reports#top")]
    [InlineData("http://example.com/api/reports")]
    [InlineData("HTTP://example.com/api/reports")]
    [InlineData("//example.com/api/reports")]
    [InlineData("/api/reports\\summary")]
    [InlineData("\\api\\reports")]
    [InlineData("api/reports")]
    [InlineData("/API/reports")]
    [InlineData("/api/")]
    [InlineData("/api/reports/")]
    [InlineData("/api/reports\n")]
    [InlineData("/other/reports")]
    [InlineData("")]
    public void A_route_that_breaks_the_pattern_drops_the_card(string route)
    {
        var (cards, warnings) = Run(Card(route: route));

        cards.Should().BeEmpty();
        warnings.Should().ContainSingle();
    }

    [Fact]
    public void A_route_of_two_hundred_characters_is_kept_and_two_hundred_and_one_is_not()
    {
        var ok = "/api/" + new string('a', 195);
        var tooLong = "/api/" + new string('a', 196);

        ok.Length.Should().Be(200);
        Run(Card(route: ok)).Cards.Should().ContainSingle();
        Run(Card(route: tooLong)).Cards.Should().BeEmpty();
    }

    [Fact]
    public void Only_the_first_four_valid_cards_are_kept()
    {
        var (cards, warnings) = Run(
            Card("c1"), Card("c2"), Card("c3"), Card("c4"), Card("c5"), Card("c6"));

        cards.Select(c => c.Id).Should().Equal("c1", "c2", "c3", "c4");
        warnings.Should().HaveCount(2);
    }

    [Fact]
    public void A_broken_card_does_not_use_up_one_of_the_four()
    {
        var (cards, _) = Run(Card("c1"), Card("BAD"), Card("c2"), Card("c3"), Card("c4"));

        cards.Select(c => c.Id).Should().Equal("c1", "c2", "c3", "c4");
    }

    [Fact]
    public void A_warning_does_not_carry_control_characters_from_the_card()
    {
        var (_, warnings) = Run(Card(id: "Bad\u001b[31mid\nforged line", title: "Bad\ntitle", route: "/x\n"));

        warnings.Should().ContainSingle();
        warnings[0].Any(char.IsControl).Should().BeFalse();
        warnings[0].Should().NotContain("/x");
    }

    private sealed class ThrowingModule(bool onGet) : IBarakoModule
    {
        public string Name => "Reports";

        public IReadOnlyList<ConsoleCard> ConsoleCards => onGet ? throw new InvalidOperationException("boom") : Throws();

        private static IReadOnlyList<ConsoleCard> Throws() => new ThrowingList();

        private sealed class ThrowingList : IReadOnlyList<ConsoleCard>
        {
            public ConsoleCard this[int index] => throw new InvalidOperationException("boom");

            public int Count => 1;

            public IEnumerator<ConsoleCard> GetEnumerator()
            {
                yield return new ConsoleCard("ok", "Ok", "/api/reports");
                throw new InvalidOperationException("boom");
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_module_whose_cards_throw_still_lists_with_no_cards_and_one_warning(bool onGet)
    {
        var warnings = new List<string>();
        var module = new ThrowingModule(onGet);

        var entry = ModuleCatalogue.Of([module], [module], warnings.Add).Entries.Single();

        entry.Cards.Should().BeEmpty();
        warnings.Should().ContainSingle().Which.Should().Contain("Reports");
    }

    [Fact]
    public void A_disabled_module_with_a_bad_card_gives_no_warning_and_no_cards()
    {
        var warnings = new List<string>();
        var module = new CardModule(Card(route: "http://example.com"), Card("good"));

        var entry = ModuleCatalogue.Of([module], [], warnings.Add).Entries.Single();

        entry.Enabled.Should().BeFalse();
        entry.Cards.Should().BeEmpty();
        warnings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Evil\u202Etitle")]
    [InlineData("Zero\u200Bwidth")]
    [InlineData("Line\u2028sep")]
    [InlineData("Para\u2029sep")]
    public void A_title_with_a_format_or_separator_character_drops_the_card(string title)
    {
        Run(Card(title: title)).Cards.Should().BeEmpty();
    }

    [Fact]
    public void A_route_under_api_auth_is_dropped_and_a_look_alike_is_kept()
    {
        Run(Card(route: "/api/auth/methods")).Cards.Should().BeEmpty();
        Run(Card(route: "/api/auth")).Cards.Should().BeEmpty();
        Run(Card(route: "/api/authors/top")).Cards.Should().ContainSingle();
    }

    [Fact]
    public void A_title_padded_with_spaces_is_kept_trimmed_at_sixty_characters()
    {
        var (cards, _) = Run(Card(title: "  " + new string('t', 60) + "  "));

        cards.Should().ContainSingle().Which.Title.Should().Be(new string('t', 60));
    }

    [Fact]
    public void A_very_long_id_gives_a_short_warning()
    {
        var (_, warnings) = Run(Card(id: new string('a', 10000)));

        warnings.Should().ContainSingle().Which.Length.Should().BeLessThan(250);
    }

    [Fact]
    public void A_module_name_with_a_newline_gives_a_warning_without_one()
    {
        var module = new NamedModule("A\nB", Card(route: "nope"));
        var warnings = new List<string>();

        ModuleCatalogue.Of([module], [module], warnings.Add);

        warnings.Should().ContainSingle();
        warnings[0].Should().NotContain("\n");
        warnings[0].Should().Contain("A?B");
    }

    private sealed class NamedModule(string name, params ConsoleCard[] cards) : IBarakoModule
    {
        public string Name => name;
        public IReadOnlyList<ConsoleCard> ConsoleCards { get; } = cards;
    }

    [Fact]
    public void A_module_with_a_broken_card_still_lists()
    {
        var module = new CardModule(Card(route: "http://example.com"));

        var entry = ModuleCatalogue.Of([module], [module]).Entries.Single();

        entry.Name.Should().Be("Reports");
        entry.Enabled.Should().BeTrue();
        entry.Cards.Should().BeEmpty();
    }

    [Fact]
    public void Console_card_content_parts_default_to_null_and_keep_old_construction()
    {
        // Modules answer with the web defaults, which write nulls unless a member opts out.
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var rows = new[] { new ConsoleCardRow("Open", "3") };

        var plain = new ConsoleCardContent("ok", "Fine", rows, "note", DateTimeOffset.UnixEpoch);

        plain.Stats.Should().BeNull();
        plain.Series.Should().BeNull();
        plain.Bars.Should().BeNull();
        plain.Checks.Should().BeNull();
        plain.RowsLabel.Should().BeNull();
        plain.Question.Should().BeNull();
        using var old = JsonDocument.Parse(JsonSerializer.Serialize(plain, web));
        old.RootElement.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("state", "text", "rows", "note", "updatedAt");

        var withStats = plain with { Stats = [new ConsoleCardStat("Orders", 12, 10, "count", 40, "up")] };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(withStats, web));
        json.RootElement.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("state", "text", "rows", "note", "updatedAt", "stats");
        json.RootElement.GetProperty("stats")[0].EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("label", "value", "previous", "format", "total", "goodWhen");
    }
}
