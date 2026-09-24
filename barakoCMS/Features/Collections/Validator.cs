using barakoCMS.Infrastructure.Sync;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.Collections;

/// <summary>
/// The shape rules for a saved sync: everything that can be decided without the database.
/// </summary>
/// <remarks>
/// What a mapping names has to be checked against the content type and the request definition, which
/// needs a session, so those live in <see cref="CollectionSyncRules"/> and run in the endpoint. Both
/// halves run on every save. Catching a bad mapping here rather than on the first sweep is the point:
/// the alternative is a sync that is configured, looks fine, and writes nothing at three in the
/// morning.
/// </remarks>
internal sealed class SaveCollectionSyncValidator : Validator<SaveCollectionSyncRequest>
{
    public SaveCollectionSyncValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);

        RuleFor(x => x.Slug)
            .NotEmpty()
            .Matches("^[a-z0-9][a-z0-9-]{0,62}$")
            .WithMessage("Slug must be lowercase letters, digits and hyphens, starting with a letter or digit.");

        RuleFor(x => x.ContentType).NotEmpty().MaximumLength(200);

        RuleFor(x => x.Source)
            .Must(s => Enum.TryParse<SyncSource>(s, ignoreCase: true, out _))
            .WithMessage($"Source must be one of: {string.Join(", ", Enum.GetNames<SyncSource>())}.");

        RuleFor(x => x.EntryStatus)
            .Must(s => Enum.TryParse<ContentStatus>(s, ignoreCase: true, out _))
            .WithMessage($"EntryStatus must be one of: {string.Join(", ", Enum.GetNames<ContentStatus>())}.");

        RuleFor(x => x.IntervalMinutes)
            .GreaterThanOrEqualTo(CollectionSync.MinIntervalMinutes)
            .WithMessage($"IntervalMinutes must be at least {CollectionSync.MinIntervalMinutes}.");

        RuleFor(x => x.MaxEntries)
            .InclusiveBetween(1, CollectionSync.MaxEntriesCeiling);

        RuleFor(x => x.FieldMap)
            .Must((req, m) => m.Count + (req.FieldRules?.Count ?? 0) > 0)
            .WithMessage("FieldMap must name at least one content field and the source path it reads.");

        RuleFor(x => x.FieldMap)
            .Must(m => m.Values.All(v => !string.IsNullOrWhiteSpace(v)))
            .WithMessage("Every entry in FieldMap needs a source path.");

        RuleFor(x => x.KeyField)
            .NotEmpty()
            .Must((req, key) => Mapped(req).Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)))
            .WithMessage("KeyField must be one of the fields FieldMap or FieldRules names, since it is read from the same item.");

        // Every item would get the same key from a constant, and a ratio or a true/false collides
        // just as surely, so the second item would overwrite the first on every run.
        RuleFor(x => x.KeyField)
            .Must((req, key) => !(req.FieldRules ?? new()).Any(r =>
                string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase)
                && r.Value is { } rule
                && (rule.Const is not null || rule.Ratio is not null || rule.Contains is not null)))
            .WithMessage("KeyField cannot be a const, ratio or contains rule, since items would share the key.");

        RuleFor(x => x.FloorFields)
            .Must((req, floors) => floors.All(f =>
                Mapped(req).Any(k => string.Equals(k, f, StringComparison.OrdinalIgnoreCase))))
            .WithMessage("Every FloorField must be one of the fields FieldMap or FieldRules names.");

        RuleFor(x => x).Custom((req, context) =>
        {
            foreach (var (field, rule) in req.FieldRules ?? new())
            {
                if (req.FieldMap.Keys.Any(k => string.Equals(k, field, StringComparison.OrdinalIgnoreCase)))
                {
                    context.AddFailure(nameof(req.FieldRules), $"'{field}' is in both fieldMap and fieldRules; name it in one.");
                }

                if (SyncRules.ShapeProblem(field, rule) is { } problem)
                {
                    context.AddFailure(nameof(req.FieldRules), problem);
                }
            }

            if ((req.Exclude ?? []).Any(e => SyncRules.ShapeProblem(e) is not null))
            {
                context.AddFailure(nameof(req.Exclude), SyncRules.ShapeProblem(null)!);
            }
        });

        // An early refusal rather than the guard. The address that gets dialled is checked when the
        // socket opens, which is the only check a DNS answer that changes afterwards cannot evade.
        RuleFor(x => x.FeedUrl)
            .Must(url => Uri.TryCreate(url, UriKind.Absolute, out var uri)
                      && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .When(x => string.Equals(x.Source, nameof(SyncSource.Feed), StringComparison.OrdinalIgnoreCase))
            .WithMessage("A Feed sync needs FeedUrl to be an absolute http or https URL.");

        RuleFor(x => x.RequestSlug)
            .NotEmpty()
            .When(x => !string.Equals(x.Source, nameof(SyncSource.Feed), StringComparison.OrdinalIgnoreCase))
            .WithMessage("A Request sync needs RequestSlug to name a request definition.");
    }

    private static IEnumerable<string> Mapped(SaveCollectionSyncRequest req) =>
        req.FieldMap.Keys.Concat((req.FieldRules ?? new()).Keys);
}
