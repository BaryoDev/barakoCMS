namespace barakoCMS.Core.Expressions;

/// <summary>
/// A refusal or a note: the rule broken, the character position in the source, and a message that
/// names neither an entry value nor the text of a string literal.
/// </summary>
internal sealed record ExpressionError(string Rule, int Position, string Message)
{
    public override string ToString() => $"{Rule} at {Position}: {Message}";
}

internal static class Rules
{
    public const string Empty = "empty";
    public const string SourceTooLong = "source-too-long";
    public const string NodeLimit = "node-limit";
    public const string DepthLimit = "depth-limit";
    public const string TextTooLong = "text-too-long";
    public const string StepBudget = "step-budget";
    public const string UnexpectedCharacter = "unexpected-character";
    public const string BadNumber = "bad-number";
    public const string Exponent = "exponent";
    public const string BadString = "bad-string";
    public const string BadDate = "bad-date";
    public const string Syntax = "syntax";
    public const string MemberAccess = "member-access";
    public const string UnknownName = "unknown-name";
    public const string AmbiguousName = "ambiguous-name";
    public const string UnknownFunction = "unknown-function";
    public const string Arity = "arity";
    public const string TypeMismatch = "type-mismatch";
    public const string ArgumentRange = "argument-range";
    public const string FormatPattern = "format-pattern";
    public const string ClockFunction = "clock-function";
    public const string Overflow = "overflow";
    public const string DivisionByZero = "division-by-zero";
    public const string ValueType = "value-type";
}
