namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Service for validating passwords against security policy.
/// </summary>
public interface IPasswordPolicyValidator
{
    /// <summary>
    /// Validates a password against the configured security policy.
    /// </summary>
    /// <param name="password">The password to validate</param>
    /// <returns>Tuple with validation result and error message if invalid</returns>
    (bool IsValid, string? ErrorMessage) Validate(string password);
}
