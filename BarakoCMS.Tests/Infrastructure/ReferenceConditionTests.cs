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
