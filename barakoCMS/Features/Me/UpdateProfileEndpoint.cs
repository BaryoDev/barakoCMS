using FastEndpoints;
using FluentValidation;
using Marten;
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
            .Must(name => name is null || !name.Any(char.IsControl))
            .WithMessage("Display name must not contain control characters.");
    }
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

        user.DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? null : req.DisplayName.Trim();
        session.Store(user);
        await session.SaveChangesAsync(ct);

        await Send.OkAsync(new UpdateProfileResponse
        {
            DisplayName = user.DisplayName ?? user.Username,
        }, ct);
    }
}
