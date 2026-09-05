using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using FluentValidation;
using Marten;

namespace BarakoCMS.Files.Features.Update;

public class Request
{
    public Guid Id { get; set; }

    /// <summary>Left alone when omitted or null. An empty string clears it.</summary>
    public string? Alt { get; set; }

    /// <summary>Left alone when omitted or null. An empty string clears it.</summary>
    public string? Caption { get; set; }
}

public class Validator : Validator<Request>
{
    public const int MaxAlt = 500;
    public const int MaxCaption = 2000;

    public Validator()
    {
        RuleFor(x => x.Alt).MaximumLength(MaxAlt);
        RuleFor(x => x.Caption).MaximumLength(MaxCaption);
    }
}

/// <summary>
/// PATCH /api/files/{id}. Sets the alt text and caption. Nothing else about a file is editable:
/// the name, type and public flag are decided at upload and a frontend may have cached the answer.
/// </summary>
public class Endpoint : Endpoint<Request, FileMetadata>
{
    private readonly IDocumentSession _session;

    public Endpoint(IDocumentSession session) => _session = session;

    public override void Configure()
    {
        Patch("/api/files/{id}");
        Definition.RequireCapability(FileCapabilities.UploadFiles, FileCapabilities.LegacyRoles);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var file = await _session.LoadAsync<StoredFile>(req.Id, ct);
        if (file is null || file.ParentFileId is not null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (req.Alt is not null) file.Alt = Clean(req.Alt);
        if (req.Caption is not null) file.Caption = Clean(req.Caption);

        _session.Store(file);
        await _session.SaveChangesAsync(ct);

        await Send.OkAsync(FileMetadata.From(file), ct);
    }

    private static string? Clean(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
