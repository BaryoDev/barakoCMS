using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>The text form of a saved query: every clause round-trips, and hostile text is refused with a sentence.</summary>
public class QueryTextTests
{
    [Theory]
    [InlineData("from student limit 100")]
    [InlineData("from student where Number = @data.StudentNo select Email, BirthDate, Name limit 1")]
    [InlineData("from invoiceLine where Invoice = @id order by createdAt select Product.Name, Qty, Price, Total limit 200")]
    [InlineData("from student where Name = Ana select Email limit 10")]
    [InlineData("from student where Name = \"O\\\"Brien, Jr\" select Email limit 10")]
    [InlineData("from student where Path = \"C:\\\\temp\" select Email limit 10")]
    [InlineData("from student where Note = \"\" select Email limit 10")]
    [InlineData("from student where Note = \"{{data.Secret}}\" select Email limit 10")]
    [InlineData("from student where A = 1 and B != 2 and C < 3 and D <= 4 and E > 5 and F >= 6 and G contains x select A limit 5")]
    [InlineData("from student where Amount = 19.99 and Big = 12345678901234567 and Neg = -5 select Amount limit 5")]
    [InlineData("from student where Invoice = @id and Born >= @createdAt and Name = @data.Name and Email = @actions.0.Email select Email limit 5")]
    [InlineData("from student where Name = \"a b\" and Kind = \"and\" select Email limit 5")]
    [InlineData("from student where \"From\" = x select \"To\" limit 5")]
    [InlineData("from student where status != Archived select id, status, createdAt, updatedAt limit 5")]
    [InlineData("from invoice in createdAt last30Days select Total limit 10")]
    [InlineData("from invoice in createdAt 2026-01-01 to 2026-01-31 select Total limit 10")]
    [InlineData("from invoice in createdAt 2026-01-01 to select Total limit 10")]
    [InlineData("from invoice in createdAt to 2026-01-31 select Total limit 10")]
    [InlineData("from invoice order by Total desc select Total limit 10")]
    [InlineData("from invoice order by updatedAt select id limit 10")]
    [InlineData("from invoice where Status != Archived in createdAt thisMonth group by Status, createdAt month sum Total as Revenue, count as Orders order by Status")]
    [InlineData("from invoice group by Status avg Total as Mean, min Total as Smallest, max Total as Largest")]
    [InlineData("from enrollment sum Student.Credits as Credits")]
    public void Text_to_definition_to_text_round_trips(string text)
    {
        var parsed = QueryText.Parse(text);

        parsed.Ok.Should().BeTrue($"{text}: {parsed.Error}");
        QueryText.Print(parsed.Definition!).Should().Be(text);
    }

    [Fact]
    public void A_definition_printed_and_parsed_again_is_the_same_definition()
    {
        var definition = new QueryDefinition
        {
            ContentType = "invoiceLine",
            Filters =
            [
                new QueryFilter { Field = "Invoice", Op = "eq", Param = "id" },
                new QueryFilter { Field = "Note", Op = "contains", Value = "50% off_\\" },
                new QueryFilter { Field = "Qty", Op = "gte", Value = "2" },
            ],
            SortField = "createdAt",
            Descending = true,
            Fields = ["Product.Name", "Qty", "id"],
            Limit = 250,
        };

        var again = QueryText.Parse(QueryText.Print(definition));

        again.Ok.Should().BeTrue(again.Error);
        again.Definition.Should().BeEquivalentTo(definition, o => o
            .Excluding(d => d.CreatedAt).Excluding(d => d.UpdatedAt));
    }

    [Fact]
    public void A_literal_that_starts_with_an_at_sign_stays_a_literal()
    {
        var definition = new QueryDefinition
        {
            ContentType = "student",
            Filters = [new QueryFilter { Field = "Handle", Op = "eq", Value = "@data.Secret" }],
            Fields = ["Handle"],
        };

        var again = QueryText.Parse(QueryText.Print(definition)).Definition!;

        again.Filters[0].Param.Should().BeNull();
        again.Filters[0].Value.Should().Be("@data.Secret");
    }

    [Fact]
    public void Keywords_are_read_in_any_case_and_printed_in_lower_case()
    {
        var parsed = QueryText.Parse("FROM student WHERE Name CONTAINS Ana ORDER BY Name DESC SELECT Email LIMIT 3");

        parsed.Ok.Should().BeTrue(parsed.Error);
        QueryText.Print(parsed.Definition!).Should().Be("from student where Name contains Ana order by Name desc select Email limit 3");
    }

    [Fact]
    public void A_parameter_is_kept_as_a_name_and_never_as_a_value()
    {
        var parsed = QueryText.Parse("from student where Number = @data.StudentNo select Email limit 1").Definition!;

        parsed.Filters.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new QueryFilter { Field = "Number", Op = "eq", Value = "", Param = "data.StudentNo" });
    }

    public static TheoryData<string> Hostile()
    {
        var eleven = "from student where " + string.Join(" and ", Enumerable.Range(1, 11).Select(i => $"F{i} = 1")) + " select Email";
        var manyTerms = "from student select " + string.Join(", ", Enumerable.Range(1, 30).Select(i => $"F{i}"));
        return new TheoryData<string>
        {
            "",
            "   \t\n",
            "select Email from student",
            "from",
            "from student where",
            "from student where Name",
            "from student where Name =",
            "from student where Name = 'abc",
            "from student where Name = \"abc",
            "from student where Name = \u201Cabc\u201D",
            "from student where Name = \u2018abc\u2019",
            "from student where Name = abc; drop table mt_doc_content",
            "from student where Name.First = 1",
            "from student where Na me = 1",
            "from student select a.b.c",
            "from student.x",
            "from select",
            "from student where select = 1",
            "from student where Name = 1 or Name = 2",
            "from student where Name like 'a%'",
            "from student where Name = @data",
            "from student where Name = @data.A.B",
            "from student where Name = @actions.x.y",
            "from student where Name = @",
            "from student order by",
            "from student limit 0",
            "from student limit 1001",
            "from student limit abc",
            "from student limit 1 limit 2",
            "from student where Name = 1 where Name = 2",
            eleven,
            "from student where Name = '" + new string('x', 2001) + "'",
            manyTerms,
            "from student select Name,",
            "from student where Name = 1 /* c */",
            "from student where Name = '\\n'",
            "from student where Name = --",
            "from student where Name = (select 1)",
            "from student where Name = 1 union select 1",
            "from student select *",
            "from student where Name = 'a' 'b'",
            "from \u00E9tudiant",
            "from student group by Name",
            "from student sum Total",
            "from student where Name = \0",
            "from student where Name == 1",
            "from student where Name <> 1",
            "from student where \"Na me\" = 1",
            "from student where \"Name; drop\" = 1",
            "from student order by Name.First",
            "from student in createdAt someday",
            "from student in createdAt 2026-13-45 to 2026-01-01",
        };
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public void Hostile_text_is_refused_with_a_message(string text)
    {
        var parsed = QueryText.Parse(text);

        parsed.Ok.Should().BeFalse($"'{(text.Length > 60 ? text[..60] : text)}' should be refused");
        parsed.Definition.Should().BeNull();
        parsed.Error.Should().NotBeNullOrWhiteSpace();
        parsed.Error!.Length.Should().BeLessThan(400, "a message never repeats a long piece of what was written");
    }

    [Fact]
    public void The_eleventh_filter_is_named_in_the_refusal()
    {
        var text = "from student where " + string.Join(" and ", Enumerable.Range(1, 11).Select(i => $"F{i} = 1"));

        QueryText.Parse(text).Error.Should().Contain("10");
    }

    [Fact]
    public void Ten_filters_are_accepted()
    {
        var text = "from student where " + string.Join(" and ", Enumerable.Range(1, 10).Select(i => $"F{i} = 1")) + " select Email";

        QueryText.Parse(text).Ok.Should().BeTrue();
    }

    [Fact]
    public void Text_over_the_cap_is_refused_before_it_is_read()
    {
        var parsed = QueryText.Parse(new string(' ', QueryText.MaxLength + 1) + "from student");

        parsed.Error.Should().Contain(QueryText.MaxLength.ToString());
    }

    [Fact]
    public void A_long_value_in_a_string_is_kept_whole()
    {
        var value = new string('x', 1500);

        var parsed = QueryText.Parse($"from student where Note = \"{value}\" select Email");

        parsed.Definition!.Filters[0].Value.Should().Be(value);
    }

    [Fact]
    public void A_hostile_value_in_a_string_stays_a_value()
    {
        const string value = "' or 1=1 --";

        var parsed = QueryText.Parse("from student where Name = \"' or 1=1 --\" select Email");

        parsed.Definition!.Filters[0].Value.Should().Be(value);
        parsed.Definition.Filters[0].Param.Should().BeNull();
    }

    [Fact]
    public void Random_text_never_throws()
    {
        var random = new Random(1421);
        const string alphabet = "fromwheselctandbyli \t\"'\\@.,=<>!-_:+;()*/012345azAZ\u201C\u00E9{}%";
        for (var i = 0; i < 3000; i++)
        {
            var text = new string(Enumerable.Range(0, random.Next(1, 120)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());

            var act = () => QueryText.Parse(text);

            act.Should().NotThrow(text);
        }
    }

    [Theory]
    [InlineData("id", true)]
    [InlineData("createdAt", true)]
    [InlineData("data.Email", true)]
    [InlineData("data.Email_2", true)]
    [InlineData("actions.0.Email", true)]
    [InlineData("actions.12.Product_Name", true)]
    [InlineData("Id", false)]
    [InlineData("data", false)]
    [InlineData("data.", false)]
    [InlineData("data.A.B", false)]
    [InlineData("data.a b", false)]
    [InlineData("actions.x.Email", false)]
    [InlineData("actions.0", false)]
    [InlineData("actions.00000.Email", false)]
    [InlineData("secret", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_named_parameters_are_parameters(string? name, bool expected) =>
        QueryText.IsParameter(name).Should().Be(expected);
}
