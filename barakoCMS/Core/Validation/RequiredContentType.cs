using Microsoft.Extensions.Configuration;

namespace barakoCMS.Core.Validation;

/// <summary>
/// The opt-in rule that a new entry needs a defined content type. Off by default, so a type with no
/// definition keeps accepting entries as it always has.
/// </summary>
/// <remarks>
/// Read on use rather than at start, like <c>Content:Concurrency:Require</c>. Creating only: an
/// entry that already exists is never refused for the type it was stored under.
/// </remarks>
public static class RequiredContentType
{
    public const string Key = "Content:RequireContentType";

    public static bool IsOn(IConfiguration? configuration) => configuration?.GetValue(Key, false) ?? false;

    public static string Refusal(string contentType) =>
        $"Content type '{contentType}' has no definition, and this deployment only accepts entries of a defined type. "
      + "Define the type first.";
}
