using FastEndpoints;
using FluentValidation;
using Marten;
using Marten.Patching;
using barakoCMS.Models;

namespace barakoCMS.Features.Me;

internal class UpdateProfileRequest
{
    public const int DisplayNameMaxLength = 100;

    /// <summary>The new display name. Null or blank clears it, and <c>GET /api/me</c> reports the username again.</summary>
    public string? DisplayName { get; set; }
}

internal class UpdateProfileResponse
{
    public string DisplayName { get; set; } = string.Empty;
}

internal class UpdateProfileValidator : Validator<UpdateProfileRequest>
{
    public UpdateProfileValidator()
    {
        RuleFor(x => x.DisplayName)
            .MaximumLength(UpdateProfileRequest.DisplayNameMaxLength)
            .Must(name => name is null || !name.Any(IsHidden))
            .WithMessage("Display name must not contain control or invisible formatting characters.");
    }

    // Format characters (bidi overrides, zero-width joiners) let a name render as someone else's.
    private static bool IsHidden(char c) =>
        char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format;
}

/// <summary>
/// PUT /api/me/profile, the signed-in user sets their own display name. It sits under the global
/// <c>/api/me</c> identity prefix like the password change, so it is not tenant-scoped.
/// </summary>
internal class UpdateProfileEndpoint(IDocumentSession session) : Endpoint<UpdateProfileRequest, UpdateProfileResponse>
{
    public override void Configure()
    {
        Put("/api/me/profile"); // authenticated by default
        Description(b => b
            .Produces<UpdateProfileResponse>(200)
            .Produces(400)
            .Produces(401));
    }

    public override async Task HandleAsync(UpdateProfileRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId)
            || await session.LoadAsync<User>(userId, ct) is not { } user)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var displayName = string.IsNullOrWhiteSpace(req.DisplayName)
            ? null
            : req.DisplayName.Trim().Normalize(System.Text.NormalizationForm.FormC);

        // Patch the one field, so a lockout or role change saved meanwhile is not overwritten.
        session.Patch<User>(userId).Set(u => u.DisplayName, displayName);
        await session.SaveChangesAsync(ct);

        await Send.OkAsync(new UpdateProfileResponse
        {
            DisplayName = displayName ?? user.Username,
        }, ct);
    }
}
