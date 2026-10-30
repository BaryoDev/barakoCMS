using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FastEndpoints;
using FluentValidation;
using Marten;
using Microsoft.AspNetCore.Http;

namespace BarakoCMS.ExternalAuth;

/// <summary>
/// One answered Facebook data deletion request. The id is the confirmation code Facebook was given.
/// Global, like the link it removed. It holds no Facebook user id.
/// </summary>
public sealed class FacebookDeletionRequest
{
    public string Id { get; set; } = string.Empty;

    public string Status { get; set; } = FacebookDeletionStatus.Completed;

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
}

public static class FacebookDeletionStatus
{
    public const string Completed = "completed";
}

/// <summary>Checks the <c>signed_request</c> Facebook posts to the data deletion callback.</summary>
internal static class FacebookSignedRequest
{
    public const int MaxLength = 4096;
    private const int MaxUserIdLength = 128;

    /// <summary>
    /// The Facebook user id in a request signed with <paramref name="secret"/>, or null when the
    /// request is empty, oversized, malformed, not HMAC-SHA256, or not signed with the secret.
    /// </summary>
    public static string? UserIdOf(string? signedRequest, string? secret)
    {
        if (string.IsNullOrEmpty(signedRequest) || signedRequest.Length > MaxLength || string.IsNullOrEmpty(secret))
        {
            return null;
        }

        var parts = signedRequest.Split('.');
        if (parts.Length != 2 || !TryDecode(parts[0], out var signature) || !TryDecode(parts[1], out var payload))
        {
            return null;
        }

        // The signature covers the payload as sent, still encoded.
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes(parts[1]));
        if (!CryptographicOperations.FixedTimeEquals(expected, signature))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("algorithm", out var algorithm) || algorithm.ValueKind != JsonValueKind.String
                || !string.Equals(algorithm.GetString(), "HMAC-SHA256", StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("user_id", out var user) || user.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var id = user.GetString();
            return string.IsNullOrWhiteSpace(id) || id.Length > MaxUserIdLength ? null : id;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Base64url without padding, strictly: any other character is a refusal.</summary>
    private static bool TryDecode(string value, out byte[] bytes)
    {
        bytes = [];
        if (value.Length == 0 || value.Length % 4 == 1)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

internal sealed class FacebookDeletionRequestModel
{
    [BindFrom("signed_request")]
    public string SignedRequest { get; set; } = string.Empty;
}

internal sealed class FacebookDeletionResponse
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("confirmation_code")]
    public string ConfirmationCode { get; set; } = string.Empty;
}

internal sealed class FacebookDeletionValidator : Validator<FacebookDeletionRequestModel>
{
    public FacebookDeletionValidator()
    {
        RuleFor(x => x.SignedRequest).NotEmpty().MaximumLength(FacebookSignedRequest.MaxLength);
    }
}

/// <summary>
/// POST /api/auth/facebook/data-deletion. Facebook posts a form with <c>signed_request</c> when a
/// person removes the app. A valid one removes that person's Facebook link and nothing else, and is
/// answered with a confirmation code and the status URL, as Facebook requires.
/// </summary>
internal sealed class FacebookDataDeletionEndpoint(IDocumentSession session, IConfiguration config)
    : Endpoint<FacebookDeletionRequestModel, FacebookDeletionResponse>
{
    public override void Configure()
    {
        Post("/api/auth/facebook/data-deletion");
        AllowAnonymous();
        AllowFormData(urlEncoded: true);
    }

    public override async Task HandleAsync(FacebookDeletionRequestModel req, CancellationToken ct)
    {
        var secret = config["Facebook:AppSecret"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var facebookUserId = FacebookSignedRequest.UserIdOf(req.SignedRequest, secret);
        if (facebookUserId is null)
        {
            ThrowError("The signed request is not valid.");
            return;
        }

        var code = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        session.Delete<ExternalIdentity>(ExternalIdentity.KeyOf(ExternalProviders.Facebook, facebookUserId));
        session.Store(new FacebookDeletionRequest { Id = code });
        await session.SaveChangesAsync(ct);

        var baseUrl = ExternalAuthSupport.BaseUrl(config, HttpContext);
        await Send.OkAsync(new FacebookDeletionResponse
        {
            Url = $"{baseUrl}/api/auth/facebook/data-deletion/status/{code}",
            ConfirmationCode = code,
        }, ct);
    }
}

internal sealed class FacebookDeletionStatusResponse
{
    public string ConfirmationCode { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public DateTime RequestedAt { get; set; }
}

/// <summary>GET /api/auth/facebook/data-deletion/status/{code}. What became of one deletion request.</summary>
internal sealed class FacebookDataDeletionStatusEndpoint(IQuerySession session)
    : EndpointWithoutRequest<FacebookDeletionStatusResponse>
{
    public override void Configure()
    {
        Get("/api/auth/facebook/data-deletion/status/{code}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var code = Route<string>("code") ?? "";
        var found = code.Length is > 0 and <= 64 ? await session.LoadAsync<FacebookDeletionRequest>(code, ct) : null;
        if (found is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(new FacebookDeletionStatusResponse
        {
            ConfirmationCode = found.Id,
            Status = found.Status,
            RequestedAt = found.RequestedAt,
        }, ct);
    }
}
