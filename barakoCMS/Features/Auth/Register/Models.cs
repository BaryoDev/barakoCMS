using FluentValidation;

namespace barakoCMS.Features.Auth.Register;

internal class Request
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

internal class RequestValidator : FastEndpoints.Validator<Request>
{
    public RequestValidator()
    {
        // The maximum matters more than the minimum. Username carries a unique btree index, and a
        // multi-megabyte value would be stored, indexed and string-compared on every sign-in; past
        // roughly 2.7KB postgres refuses the index entry outright and registration fails as a 500.
        RuleFor(x => x.Username).NotEmpty().MinimumLength(3).MaximumLength(64)
            .Must(NotContainNul).WithMessage("Username must not contain a NUL character.");
        // Postgres text cannot hold U+0000, so without this the value passes every other rule and
        // fails at the database. Only NUL is refused: other control characters store and have
        // always been accepted, and refusing them would reject a request that works today.
        RuleFor(x => x.Email).MaximumLength(254)
            .Must(NotContainNul).WithMessage("Email must not contain a NUL character.");
        // Password minimum length must match PasswordPolicyValidator (12 characters)
        RuleFor(x => x.Password).NotEmpty().MinimumLength(12)
            .WithMessage("Password must be at least 12 characters long.");
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
    }

    private static bool NotContainNul(string? value) => value is null || !value.Contains('\0');
}

internal class Response
{
    public string Message { get; set; } = string.Empty;
}

internal class VerifyRequestValidator : FastEndpoints.Validator<VerifyRequest>
{
    // A token this endpoint issued is 76 characters. The cap is here because the value is parsed and
    // BCrypt-verified, and neither should be handed a megabyte by an anonymous caller.
    public VerifyRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty().MaximumLength(256);
    }
}
