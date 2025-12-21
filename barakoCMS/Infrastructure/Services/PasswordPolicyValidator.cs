using System.Text.RegularExpressions;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Validates passwords against security policy:
/// - Minimum 12 characters
/// - At least 1 uppercase letter
/// - At least 1 lowercase letter
/// - At least 1 digit
/// - At least 1 special character
/// </summary>
public class PasswordPolicyValidator : IPasswordPolicyValidator
{
    private static readonly Regex HasNumber = new(@"[0-9]+", RegexOptions.Compiled);
    private static readonly Regex HasUpperChar = new(@"[A-Z]+", RegexOptions.Compiled);
    private static readonly Regex HasLowerChar = new(@"[a-z]+", RegexOptions.Compiled);
    private static readonly Regex HasSymbols = new(@"[!@#$%^&*()_+=\[{\]};:<>|./?,-]", RegexOptions.Compiled);

    private const int MinLength = 12;

    public (bool IsValid, string? ErrorMessage) Validate(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return (false, "Password is required.");
        }

        if (password.Length < MinLength)
        {
            return (false, $"Password must be at least {MinLength} characters long.");
        }

        if (!HasUpperChar.IsMatch(password))
        {
            return (false, "Password must contain at least one uppercase letter (A-Z).");
        }

        if (!HasLowerChar.IsMatch(password))
        {
            return (false, "Password must contain at least one lowercase letter (a-z).");
        }

        if (!HasNumber.IsMatch(password))
        {
            return (false, "Password must contain at least one digit (0-9).");
        }

        if (!HasSymbols.IsMatch(password))
        {
            return (false, "Password must contain at least one special character (!@#$%^&*()_+=[]{};<>|./?,-)."); 
        }

        return (true, null);
    }
}
