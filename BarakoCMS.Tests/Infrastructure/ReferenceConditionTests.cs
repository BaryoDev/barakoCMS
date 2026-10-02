using System.Text.Json;
using FluentAssertions;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Infrastructure;

/// <summary>
/// A condition key that follows a reference, as the evaluator and the predicate compiler read it
/// with no database: neither looks the key up in the row's own data.
/// </summary>
/// <remarks>
/// An entry write keeps keys its type does not declare, so a row can carry a key spelled
/// <c>Class.InstructorUser</c>. Before this a rule naming that key matched it, in memory and in
/// SQL, which let whoever wrote the row decide who read it.
/// </remarks>
public class ReferenceConditionTests
{
    private static readonly User Caller = new()
    {
        Id = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444"),
        Username = "caller",
        Email = "caller@example.com",
    };

    private static Dictionary<string, object> Path(string key = "Class.InstructorUser") => new()
    {
        [key] = new Dictionary<string, object> { ["_eq"] = "$CURRENT_USER" },
    };

    private static Content Forged(string key = "Class.InstructorUser") => new()
    {
        Id = Guid.NewGuid(),
        ContentType = "enrollment",
        Data = new Dictionary<string, object>
        {
            ["Class"] = Guid.NewGuid().ToString(),
            [key] = Caller.Id.ToString(),
        },
    };

    [Fact]
    public void The_evaluator_denies_a_path_even_when_the_row_holds_a_key_of_that_name()
    {
        var evaluator = new ConditionEvaluator();

        evaluator.Evaluate(Path(), Forged(), Caller, callerProfile: null).Should().BeFalse(
            "the row's own data is not where a reference is read from");
        evaluator.Evaluate(Path(), Forged(), Caller).Should().BeFalse(
            "the older overload that takes the document answers a permission too");
    }

    [Fact]
    public void A_path_read_back_from_storage_is_denied_the_same_way()
    {
        var evaluator = new ConditionEvaluator();
        var stored = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(Path()))!;

        stored["Class.InstructorUser"].Should().BeOfType<JsonElement>("this is the shape a role has after a round trip");
        evaluator.Evaluate(stored, Forged(), Caller, callerProfile: null).Should().BeFalse();
    }

    [Fact]
    public void A_field_rule_keeps_reading_a_dotted_key_as_the_text_it_is()
    {
        // FieldRules evaluates requiredWhen through the overload that takes the data bag. A stored
        // rule there is not a permission and does not change meaning.
        var evaluator = new ConditionEvaluator();
        var forged = Forged();

        evaluator.Evaluate(Path(), forged.Data, Caller).Should().BeTrue();
    }

    [Fact]
    public void The_compiler_declines_a_path_it_was_given_no_predicate_for()
    {
        var rule = new PermissionRule { Enabled = true, Conditions = Path() };

        PermissionPredicateCompiler.Compile([rule], Caller.Id).Compiled.Should().BeFalse(
            "compiling the key as a field of the row would select a row that forged it");
    }

    [Fact]
    public void The_compiler_puts_the_predicate_it_was_given_in_place_of_the_path()
    {
        var conditions = Path();
        conditions["Student"] = new Dictionary<string, object> { ["_eq"] = "ana" };
        var rule = new PermissionRule { Enabled = true, Conditions = conditions };

        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var clause = ReferenceConditions.In("Class", ids);

        var predicate = PermissionPredicateCompiler.Compile(
            [rule],
            Caller.Id,
            callerProfile: null,
            new Dictionary<PermissionRule, IReadOnlyDictionary<string, ReadPredicate>>
            {
                [rule] = new Dictionary<string, ReadPredicate> { ["Class.InstructorUser"] = clause },
            });

        predicate.Compiled.Should().BeTrue();
        predicate.Sql.Should().Contain(clause.Sql!);
        predicate.Sql!.Count(ch => ch == '?').Should().Be(predicate.Parameters.Length);

        // The clause's own parameters, in its order: the field, then the ids as text.
        predicate.Parameters.Should().HaveCount(6);
        predicate.Parameters.Take(3).Should().Equal("Class", ids[0].ToString(), ids[1].ToString());
        predicate.Parameters.Skip(3).Should().Equal("Student", "Student", "ana");
    }

    [Fact]
    public void The_compiler_declines_a_predicate_whose_placeholders_do_not_match_its_parameters()
    {
        var rule = new PermissionRule { Enabled = true, Conditions = Path() };

        var predicate = PermissionPredicateCompiler.Compile(
            [rule],
            Caller.Id,
            callerProfile: null,
            new Dictionary<PermissionRule, IReadOnlyDictionary<string, ReadPredicate>>
            {
                [rule] = new Dictionary<string, ReadPredicate>
                {
                    ["Class.InstructorUser"] = new("lower(d.data -> 'Data' ->> ?) IN (?, ?)", ["Class", "one"]),
                },
            });

        predicate.Compiled.Should().BeFalse("a miscounted parameter shifts every one after it");
    }

    [Fact]
    public void No_ids_is_no_rows_and_not_an_empty_list_in_sql()
    {
        ReferenceConditions.In("Class", []).Sql.Should().Be("FALSE");
    }

    [Theory]
    [InlineData("Class.InstructorUser", "Class", "InstructorUser")]
    [InlineData("class.instructor_user", "class", "instructor_user")]
    [InlineData("A.B", "A", "B")]
    public void A_path_is_two_names_around_one_dot(string key, string reference, string field)
    {
        ReferenceConditions.TrySplit(key, out var first, out var second).Should().BeTrue();

        first.Should().Be(reference);
        second.Should().Be(field);
    }

    [Theory]
    [InlineData("Class.Teacher.Email")]
    [InlineData("Class.")]
    [InlineData(".InstructorUser")]
    [InlineData("Class..InstructorUser")]
    [InlineData("Class.$createdBy")]
    [InlineData("Class.Instructor User")]
    [InlineData("Class.1st")]
    [InlineData("Cl-ass.InstructorUser")]
    [InlineData("Class.<script>")]
    public void Anything_else_holding_a_dot_is_a_path_that_cannot_be_split(string key)
    {
        ReferenceConditions.IsPath(key).Should().BeTrue("it holds a dot, so it is never a field of the row");
        ReferenceConditions.TrySplit(key, out _, out _).Should().BeFalse();

        new ConditionEvaluator().Evaluate(Path(key), Forged(key), Caller, callerProfile: null).Should().BeFalse();
    }

    [Theory]
    [InlineData("Class")]
    [InlineData("$createdBy")]
    [InlineData("$created.by")]
    public void A_key_with_no_dot_or_a_document_property_is_not_a_path(string key)
    {
        ReferenceConditions.IsPath(key).Should().BeFalse();
    }

    [Fact]
    public void A_name_longer_than_the_limit_is_not_split()
    {
        var longest = new string('a', ReferenceConditions.MaxNameLength);

        ReferenceConditions.TrySplit($"{longest}.{longest}", out _, out _).Should().BeTrue();
        ReferenceConditions.TrySplit($"{longest}a.Field", out _, out _).Should().BeFalse();
        ReferenceConditions.TrySplit($"Class.{longest}a", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void A_reference_id_is_read_in_its_hyphenated_form_in_either_case_and_in_no_other()
    {
        var id = Guid.NewGuid();

        foreach (var held in new object[]
                 {
                     id.ToString(),
                     id.ToString().ToUpperInvariant(),
                     JsonDocument.Parse($"\"{id}\"").RootElement,
                 })
        {
            ReferenceConditions.TryReadId(held, out var read).Should().BeTrue("{0} is the hyphenated form", held);
            read.Should().Be(id);
        }

        foreach (var held in new object?[]
                 {
                     id.ToString("N"),
                     id.ToString("B"),
                     // Spellings the id parser takes in place of leading digits. The list compares
                     // the stored text with the id written plainly, so these would open and not list.
                     "0x345678-1234-1234-1234-123456789abc",
                     "0X345678-1234-1234-1234-123456789abc",
                     "+2345678-1234-1234-1234-123456789abc",
                     "12345678-0x34-1234-1234-123456789abc",
                     $"{id}\n",
                     $" {id}",
                     $"{id} ",
                     "not an id",
                     string.Empty,
                     42L,
                     id,
                     null,
                     JsonDocument.Parse("42").RootElement,
                     JsonDocument.Parse($"[\"{id}\"]").RootElement,
                     JsonDocument.Parse("null").RootElement,
                 })
        {
            ReferenceConditions.TryReadId(held, out _).Should().BeFalse("{0} is not text in the hyphenated form", held);
        }
    }

    private static Npgsql.NpgsqlCommand Command(string text, params Npgsql.NpgsqlParameter[] parameters)
    {
        var command = new Npgsql.NpgsqlCommand(text);
        command.Parameters.AddRange(parameters);
        return command;
    }

    [Fact]
    public void A_query_becomes_a_subquery_with_its_parameters_in_the_order_its_text_names_them()
    {
        // The two ways a command names a parameter: by position and by name.
        foreach (var command in new[]
                 {
                     Command(
                         "select d.id from public.mt_doc_content as d where d.tenant_id = $1 and d.data ->> 'ContentType' = $2;",
                         new Npgsql.NpgsqlParameter { Value = "tenant-a" },
                         new Npgsql.NpgsqlParameter { Value = "class" }),
                     Command(
                         "select d.id from public.mt_doc_content as d where d.tenant_id = :p0 and d.data ->> 'ContentType' = :p1",
                         new Npgsql.NpgsqlParameter("p0", "tenant-a"),
                         new Npgsql.NpgsqlParameter("p1", "class")),
                 })
        {
            var predicate = ReferenceConditions.InSubquery("Class", command);

            predicate.Should().NotBeNull();
            predicate!.Sql.Should().EndWith(
                "IN (select d.id from public.mt_doc_content as d where d.tenant_id = ? and d.data ->> 'ContentType' = ?)");
            predicate.Sql!.Count(ch => ch == '?').Should().Be(predicate.Parameters.Length);

            // The reference field twice, for the spelling check and the cast, then the query's own.
            predicate.Parameters.Should().HaveCount(4);
            predicate.Parameters.Should().Equal("Class", "Class", "tenant-a", "class");
        }
    }

    [Fact]
    public void A_parameter_named_twice_is_bound_twice_and_a_cast_is_not_a_parameter()
    {
        var predicate = ReferenceConditions.InSubquery("Class", Command(
            "select d.id from t as d where d.a = $2 and d.b::text = $1 and d.c = $2",
            new Npgsql.NpgsqlParameter { Value = "one" },
            new Npgsql.NpgsqlParameter { Value = "two" }));

        predicate.Should().NotBeNull();
        predicate!.Sql.Should().EndWith("IN (select d.id from t as d where d.a = ? and d.b::text = ? and d.c = ?)");
        predicate.Parameters.Should().HaveCount(5);
        predicate.Parameters.Should().Equal("Class", "Class", "two", "one", "two");
    }

    [Fact]
    public void A_query_that_cannot_be_carried_over_with_certainty_gives_no_subquery()
    {
        var one = new Func<Npgsql.NpgsqlParameter>(() => new Npgsql.NpgsqlParameter { Value = "one" });

        // A parameter the text never names, one the text names and the command does not hold, a
        // question mark of the text's own, a parameter with no value, and a text that is no select.
        foreach (var command in new[]
                 {
                     Command("select d.id from t as d where d.a = $1", one(), one()),
                     Command("select d.id from t as d where d.a = $1 and d.b = $2", one()),
                     Command("select d.id from t as d where d.a ? $1", one()),
                     Command("select d.id from t as d where d.a = $1", new Npgsql.NpgsqlParameter { Value = DBNull.Value }),
                     Command("delete from t where a = $1", one()),
                 })
        {
            ReferenceConditions.InSubquery("Class", command).Should().BeNull("{0} is not certain", command.CommandText);
        }
    }

    [Fact]
    public void A_refusal_past_the_bound_names_the_condition_and_the_bound()
    {
        var refusal = new ReferenceConditionBoundException("Class.InstructorUser");

        refusal.Message.Should().Contain("'Class.InstructorUser'");
        refusal.Message.Should().Contain($"more than {ReferenceConditions.MaxEntriesPerCondition} entries");
    }

    [Fact]
    public void A_comparison_is_text_or_a_list_of_text_holding_at_least_one()
    {
        static Dictionary<string, object> With(string op, object expected) => new() { [op] = expected };

        foreach (var operators in new object[]
                 {
                     With("_eq", "$CURRENT_USER"),
                     With("_ne", "north"),
                     With("_eq", "$CURRENT_USER.branch"),
                     With("_in", new List<object> { "north", "south" }),
                     With("_nin", new List<object> { "north" }),
                     JsonDocument.Parse("{\"_eq\":\"north\",\"_nin\":[\"south\"]}").RootElement,
                 })
        {
            ReferenceConditions.ComparesText(operators).Should().BeTrue("{0} compares text", JsonSerializer.Serialize(operators));
        }

        foreach (var operators in new object?[]
                 {
                     With("_eq", 42L),
                     With("_ne", true),
                     With("_in", new List<object>()),
                     With("_in", new List<object> { "north", 42L }),
                     With("_nin", "north"),
                     With("_gt", "north"),
                     "north",
                     null,
                 })
        {
            ReferenceConditions.ComparesText(operators).Should().BeFalse("{0} does not", JsonSerializer.Serialize(operators));
        }
    }

    [Fact]
    public void A_reference_type_is_looked_up_as_written_and_then_as_a_type_name_is_stored()
    {
        ReferenceConditions.TargetNames("class").Should().Equal("class");
        ReferenceConditions.TargetNames("Blog Post").Should().Equal("Blog Post", "blog-post");
    }

    [Fact]
    public void A_field_is_followed_only_when_the_type_declares_it_as_a_reference()
    {
        var definition = new ContentTypeDefinition
        {
            Name = "enrollment",
            Fields =
            [
                new FieldDefinition { Name = "Class", Type = "reference", ReferenceType = "class" },
                new FieldDefinition { Name = "Student", Type = "string" },
                new FieldDefinition { Name = "Untyped", Type = "reference" },
            ],
        };

        ReferenceConditions.ReferenceField(definition, "Class").Should().NotBeNull();
        ReferenceConditions.ReferenceField(definition, "class").Should().BeNull("a data key is matched in its own case");
        ReferenceConditions.ReferenceField(definition, "Student").Should().BeNull();
        ReferenceConditions.ReferenceField(definition, "Untyped").Should().BeNull();
        ReferenceConditions.ReferenceField(definition, "Missing").Should().BeNull();
        ReferenceConditions.ReferenceField(null, "Class").Should().BeNull();
    }

    [Fact]
    public void A_referenced_field_is_compared_only_when_it_is_declared_and_public()
    {
        var definition = new ContentTypeDefinition
        {
            Name = "class",
            Fields =
            [
                new FieldDefinition { Name = "InstructorUser", Type = "string" },
                new FieldDefinition { Name = "Salary", Type = "decimal", Sensitivity = SensitivityLevel.Sensitive },
                new FieldDefinition { Name = "Notes", Type = "string", Sensitivity = SensitivityLevel.Hidden },
            ],
        };

        ReferenceConditions.IsComparable(definition, "InstructorUser").Should().BeTrue();
        ReferenceConditions.IsComparable(definition, "instructoruser").Should().BeFalse();
        ReferenceConditions.IsComparable(definition, "Salary").Should().BeFalse();
        ReferenceConditions.IsComparable(definition, "Notes").Should().BeFalse();
        ReferenceConditions.IsComparable(definition, "Missing").Should().BeFalse();
        ReferenceConditions.IsComparable(null, "InstructorUser").Should().BeFalse();
    }
}
