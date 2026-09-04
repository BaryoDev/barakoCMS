using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Redirects;

internal sealed class ImportRedirectsRequest
{
    /// <summary>CSV, one rule per line: from,to[,permanent[,note]]. A header row is optional.</summary>
    public string Csv { get; set; } = string.Empty;

    /// <summary>Report what would happen without writing anything.</summary>
    public bool DryRun { get; set; }
}

internal sealed class RedirectImportReport
{
    public bool DryRun { get; init; }
    public int Created { get; init; }
    public int Updated { get; init; }

    /// <summary>Lines that were not imported, each with the line number and why.</summary>
    public List<string> Rejected { get; init; } = new();
}

/// <summary>
/// POST /api/redirects/import. A site migration brings hundreds of these at once.
/// </summary>
/// <remarks>
/// Every line is validated against the rules already stored AND against the lines above it in the
/// same file. Checking only against the database would let one upload introduce a loop that neither
/// line creates on its own, which is the loop nobody can find afterwards because no single rule
/// looks wrong.
///
/// A bad line is rejected and named; the rest still import. That is the opposite of the content
/// importer next door, which is all or nothing, and the reason is what the two are for: a content
/// bundle is one export that should arrive whole, and a redirect list is a spreadsheet somebody
/// typed, where the useful answer is "these four hundred worked and these three did not".
/// </remarks>
internal sealed class ImportRedirectsEndpoint : Endpoint<ImportRedirectsRequest, RedirectImportReport>
{
    private readonly IDocumentSession _session;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public ImportRedirectsEndpoint(
        IDocumentSession session, barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Post("/api/redirects/import");
        Definition.RequireCapability(SystemCapabilities.ManageRedirects, RedirectGate.LegacyRoles);
    }

    /// <summary>How many lines one upload may carry.</summary>
    /// <remarks>
    /// A cap rather than none, because this is an authenticated endpoint that loads every existing
    /// rule and then does work per line. Five thousand is far more than a site migration produces and
    /// small enough that the whole thing stays one transaction.
    /// </remarks>
    public const int MaxLines = 5000;

    public override async Task HandleAsync(ImportRedirectsRequest req, CancellationToken ct)
    {
        var lines = (req.Csv ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count == 0)
        {
            AddError("The upload had no lines in it.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        if (lines.Count > MaxLines)
        {
            AddError($"That is {lines.Count} lines. At most {MaxLines} can be imported at once.");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        var stored = await _session.Query<UrlRedirect>().ToListAsync(ct);
        var byPath = stored.ToDictionary(r => r.FromPath, StringComparer.Ordinal);

        // The map the loop check walks, seeded from the database and extended as lines are accepted,
        // so line 300 is checked against line 12 as well as against what was already there.
        var chain = stored.ToDictionary(r => r.FromPath, r => r.ToPath, StringComparer.Ordinal);

        var rejected = new List<string>();
        var created = 0;
        var updated = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            var number = i + 1;
            var parts = SplitCsv(lines[i]);

            if (parts.Count < 2)
            {
                rejected.Add($"Line {number}: needs at least a from and a to, separated by a comma.");
                continue;
            }

            // A header row, skipped rather than rejected. Spreadsheets add one and a report full of
            // "line 1 is invalid" trains people to ignore the report.
            if (number == 1 && parts[0].Trim().Equals("from", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var from = UrlRedirect.Normalize(parts[0]);
            var to = UrlRedirect.Normalize(parts[1]);

            var permanent = parts.Count > 2
                && (parts[2].Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
                 || parts[2].Trim() == "301");

            var note = parts.Count > 3 ? parts[3].Trim() : null;

            var without = new Dictionary<string, string>(chain, StringComparer.Ordinal);
            without.Remove(from);

            if (RedirectRules.Refuse(from, to, without) is { } refusal)
            {
                rejected.Add($"Line {number}: {refusal}");
                continue;
            }

            if (byPath.TryGetValue(from, out var existing))
            {
                existing.ToPath = to;
                existing.Permanent = permanent;
                existing.Note = note ?? existing.Note;
                existing.UpdatedAt = DateTime.UtcNow;

                _session.Store(existing);
                updated++;
            }
            else
            {
                var redirect = new UrlRedirect
                {
                    Id = Guid.NewGuid(),
                    FromPath = from,
                    ToPath = to,
                    Permanent = permanent,
                    Note = note,
                };

                _session.Store(redirect);
                byPath[from] = redirect;
                created++;
            }

            chain[from] = to;
        }

        // The save is the only thing a dry run skips, and staging into the session is deliberately
        // not guarded as well. Two guards for one decision means a mutation to either leaves the
        // other holding, so neither is ever the thing a test proves. Nothing staged reaches the
        // database without this call, so this call is the guard.
        if (!req.DryRun)
        {
            var actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var parsed) ? parsed : (Guid?)null;
            await AuditLog.RecordAsync(_session, _tenant.Slug, "redirect.imported", actorId,
                User.FindFirst("Username")?.Value,
                targetType: nameof(UrlRedirect),
                metadata: new Dictionary<string, object>
                {
                    ["created"] = created,
                    ["updated"] = updated,
                    ["rejected"] = rejected.Count,
                }, ct: ct);

            await _session.SaveChangesAsync(ct);
        }

        await Send.OkAsync(new RedirectImportReport
        {
            DryRun = req.DryRun,
            Created = created,
            Updated = updated,
            Rejected = rejected,
        }, ct);
    }

    /// <summary>
    /// Splits one CSV line, honouring double quotes.
    /// </summary>
    /// <remarks>
    /// Hand-rolled rather than a dependency, because the shape here is four fields of which only the
    /// note can contain a comma. A full CSV parser would be the right call the moment this needs
    /// embedded newlines, and it does not: a redirect is two paths and a flag.
    /// </remarks>
    private static List<string> SplitCsv(string line)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '"')
            {
                // A doubled quote inside a quoted field is one literal quote.
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (c == ',' && !quoted)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        parts.Add(current.ToString());
        return parts;
    }
}
