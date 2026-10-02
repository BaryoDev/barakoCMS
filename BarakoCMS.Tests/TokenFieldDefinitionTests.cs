using FluentAssertions;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// What a token field may declare, checked when the type is saved: its length, that it is never
/// Public, and that nothing a caller would supply is asked of it.
/// </summary>
public class TokenFieldDefinitionTests
{
    private static readonly ContentTypeValidatorService Validator = new();

    private static FieldDefinition Token(Action<FieldDefinition>? change = null)
    {
        var field = new FieldDefinition
        {
            Name = "ClaimToken",
            DisplayName = "Claim token",
            Type = "token",
            Sensitivity = SensitivityLevel.Hidden,
        };
        change?.Invoke(field);
        return field;
    }

    private static (bool IsValid, List<string> Errors) Save(FieldDefinition field) =>
        Validator.Validate("ticket", "Ticket", [field]);

    [Theory]
    [InlineData(SensitivityLevel.Hidden, null)]
    [InlineData(SensitivityLevel.Sensitive, null)]
    [InlineData(SensitivityLevel.Hidden, 16)]
    [InlineData(SensitivityLevel.Hidden, 128)]
    public void A_token_field_that_is_not_public_is_accepted_with_or_without_a_length(SensitivityLevel level, int? length)
    {
        var (isValid, errors) = Save(Token(f => { f.Sensitivity = level; f.TokenLength = length; }));

        isValid.Should().BeTrue(string.Join("; ", errors));
    }

    [Fact]
    public void A_public_token_field_is_refused()
    {
        var (isValid, errors) = Save(Token(f => f.Sensitivity = SensitivityLevel.Public));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("ClaimToken").And.Contain("cannot be Public");
    }

    [Theory]
    [InlineData(15)]
    [InlineData(129)]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_token_length_outside_16_to_128_is_refused(int length)
    {
        var (isValid, errors) = Save(Token(f => f.TokenLength = length));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("ClaimToken").And.Contain("16 to 128");
    }

    [Theory]
    [InlineData("string")]
    [InlineData("uuid")]
    [InlineData("int")]
    public void A_token_length_on_a_field_that_is_not_a_token_is_refused(string type)
    {
        var (isValid, errors) = Save(new FieldDefinition { Name = "Code", DisplayName = "Code", Type = type, TokenLength = 32 });

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Code").And.Contain("not token");
    }

    [Fact]
    public void A_required_token_field_is_refused()
    {
        var (isValid, errors) = Save(Token(f => f.IsRequired = true));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("ClaimToken").And.Contain("cannot be required");
    }

    [Fact]
    public void A_token_field_with_a_default_value_is_refused()
    {
        var (isValid, errors) = Save(Token(f => f.DefaultValue = "0123456789abcdef"));

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("ClaimToken").And.Contain("no default value");
        errors[0].Should().NotContain("0123456789abcdef");
    }

    [Fact]
    public void Every_validation_rule_is_refused_on_a_token_field()
    {
        FieldRules.Names.Should().NotBeEmpty();

        foreach (var rule in FieldRules.Names)
        {
            var (isValid, errors) = Save(Token(f => f.ValidationRules = new Dictionary<string, object> { [rule] = 1L }));

            isValid.Should().BeFalse("rule '{0}' has nothing to check on a value the server generates", rule);
            errors.Should().HaveCount(1);
            errors[0].Should().Contain("ClaimToken").And.Contain(", which applies to ");

            FieldRules.AppliesTo(rule, "token").Should().BeFalse(
                "the describe document lists the rules a type takes from this, and a token takes none");
        }
    }

    [Fact]
    public void A_transition_cannot_take_a_token_field()
    {
        var fields = new List<FieldDefinition>
        {
            new() { Name = "Name", DisplayName = "Name", Type = "string" },
            Token(),
        };

        var lifecycle = new LifecycleDefinition
        {
            States = ["Issued", "Claimed"],
            InitialState = "Issued",
            Transitions =
            [
                new StateTransition { Name = "Claim", From = "Issued", To = "Claimed", OptionalFields = ["ClaimToken"] },
            ],
        };

        var (isValid, errors) = Validator.ValidateLifecycle(lifecycle, fields);

        isValid.Should().BeFalse();
        errors.Should().HaveCount(1);
        errors[0].Should().Contain("Claim").And.Contain("is a token");

        lifecycle.Transitions[0].OptionalFields = ["Name"];
        Validator.ValidateLifecycle(lifecycle, fields).IsValid.Should().BeTrue("a transition still takes a field a caller writes");
    }

    [Fact]
    public void The_type_default_raises_a_public_token_to_hidden_and_changes_nothing_else()
    {
        var left = Token(f => f.Sensitivity = SensitivityLevel.Public);
        var sensitive = Token(f => f.Sensitivity = SensitivityLevel.Sensitive);
        var text = new FieldDefinition { Name = "Name", Type = "string" };

        FieldTypeRegistry.ApplyTypeDefaults(new FieldDefinition?[] { left, sensitive, text, null });

        left.Sensitivity.Should().Be(SensitivityLevel.Hidden);
        sensitive.Sensitivity.Should().Be(SensitivityLevel.Sensitive);
        text.Sensitivity.Should().Be(SensitivityLevel.Public);
    }

    [Fact]
    public void The_registry_knows_the_type_and_that_the_server_generates_it()
    {
        FieldTypeRegistry.IsKnownType("token").Should().BeTrue();
        FieldTypeRegistry.IsServerGenerated(Token()).Should().BeTrue();
        FieldTypeRegistry.IsServerGenerated(Token(f => f.Type = "Token")).Should().BeTrue("a type name is matched ignoring case");
        FieldTypeRegistry.IsServerGenerated(new FieldDefinition { Name = "Name", Type = "string" }).Should().BeFalse();
    }
}
