using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using FluentValidation;
using Marten;

namespace BarakoCMS.Diagnostics.Features.Resolve;

public class ResolveRequest
{
    public Guid Id { get; set; }
    /// <summary>True to mark resolved, false to reopen. Defaults to true.</summary>
    public bool Resolved { get; set; } = true;

    /// <summary>Optional pull request or ticket, as a link or a number. Ignored when reopening.</summary>
    public string? Reference { get; set; }

    /// <summary>Optional remarks on what was done. Ignored when reopening.</summary>
    public string? Note { get; set; }
}

public sealed class ResolveValidator : Validator<ResolveRequest>
{
    public const int MaxReferenceLength = 500;
    public const int MaxNoteLength = 2000;

    public ResolveValidator()
    {
        RuleFor(x => x.Reference).MaximumLength(MaxReferenceLength);
        RuleFor(x => x.Note).MaximumLength(MaxNoteLength);
    }
}

/// <summary>POST /api/client-errors/{id}/resolve: mark an error done, or reopen it.</summary>
public class Endpoint : Endpoint<ResolveRequest>
{
    private readonly IDocumentSession _session;
    public Endpoint(IDocumentSession session) => _session = session;

    public override void Configure()
    {
        Post("/api/client-errors/{id}/resolve");
        Definition.RequireCapability(
            DiagnosticsCapabilities.ManageClientErrors, DiagnosticsCapabilities.LegacyRoles);
    }

    public override async Task HandleAsync(ResolveRequest req, CancellationToken ct)
    {
        var error = await _session.LoadAsync<ClientError>(req.Id, ct);
        if (error is null) { await Send.NotFoundAsync(ct); return; }

        error.Resolved = req.Resolved;
        error.ResolvedAt = req.Resolved ? DateTime.UtcNow : null;

        // Reopening clears the resolution as well as the flag. A reference left behind on an open
        // error reads as "this was fixed by that", which is the claim reopening withdraws.
        error.ResolvedBy = req.Resolved ? User.FindFirst("Username")?.Value : null;
        error.ResolutionReference = req.Resolved ? Blank(req.Reference) : null;
        error.ResolutionNote = req.Resolved ? Blank(req.Note) : null;

        _session.Store(error);
        await _session.SaveChangesAsync(ct);
        await Send.OkAsync(ct);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
