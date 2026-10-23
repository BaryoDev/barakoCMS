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
            .Must(name => name is null || !name.EnumerateRunes().Any(IsHidden))
            .WithMessage("Display name must not contain control or invisible formatting characters.");
    }

    // Format characters (bidi overrides, zero-width joiners, tag characters) and the fillers that draw
    // as blank let a name render as someone else's, or as nothing. Runes, not chars, so a character
    // outside the basic plane is judged by its own category; a lone surrogate enumerates as U+FFFD.
    private static readonly HashSet<int> BlankFillers = [0x115F, 0x1160, 0x2800, 0x3164, 0xFFA0];

    private static bool IsHidden(System.Text.Rune rune) =>
        System.Text.Rune.IsControl(rune)
        || rune == System.Text.Rune.ReplacementChar
        || BlankFillers.Contains(rune.Value)
        || System.Text.Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.Format
            or System.Globalization.UnicodeCategory.LineSeparator
            or System.Globalization.UnicodeCategory.ParagraphSeparator
            or System.Globalization.UnicodeCategory.PrivateUse
            or System.Globalization.UnicodeCategory.OtherNotAssigned;
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
