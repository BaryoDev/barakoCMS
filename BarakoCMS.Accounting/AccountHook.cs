using barakoCMS.Core.Interfaces;
using Marten;

namespace BarakoCMS.Accounting;

/// <summary>
/// Chart-of-accounts rules the schema cannot express: the account class must be one of the five
/// classical types, codes are unique across the chart, and a parent code must actually resolve to
/// another account. Runs inside the generic content write pipeline, so accounts stay ordinary
/// content while still being impossible to create in a broken state.
/// </summary>
public sealed class AccountHook : IContentLifecycleHook
{
    public string ContentType => AccountingContentTypes.Account;

    public async Task<IReadOnlyList<string>> OnBeforeSaveAsync(
        ContentLifecycleContext context, CancellationToken ct)
    {
        var errors = new List<string>();
        var data = context.Data;

        var code = ContentData.AsString(ContentData.Get(data, "Code"));
        if (string.IsNullOrWhiteSpace(code))
            errors.Add("Account code is required.");

        if (string.IsNullOrWhiteSpace(ContentData.AsString(ContentData.Get(data, "Name"))))
            errors.Add("Account name is required.");

        var type = ContentData.AsString(ContentData.Get(data, "Type"));
        if (!AccountingContentTypes.AccountTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
            errors.Add($"Account type must be one of: {string.Join(", ", AccountingContentTypes.AccountTypes)}.");

        // Default IsActive rather than letting a missing flag read as inactive later.
        if (!ContentData.Has(data, "IsActive"))
            ContentData.Set(data, "IsActive", true);

        var parentCode = ContentData.AsString(ContentData.Get(data, "ParentCode"));
        var needsChartLookup = !string.IsNullOrWhiteSpace(code) || !string.IsNullOrWhiteSpace(parentCode);

        if (needsChartLookup)
        {
            var chart = await context.Session.Query<barakoCMS.Models.Content>()
                .Where(c => c.ContentType == AccountingContentTypes.Account)
                .ToListAsync(ct);

            var existingCode = context.Existing is null
                ? null
                : ContentData.AsString(ContentData.Get(context.Existing, "Code"));

            var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in chart)
            {
                var c = ContentData.AsString(ContentData.Get(entry.Data, "Code"));
                if (!string.IsNullOrWhiteSpace(c)) codes.Add(c!);
            }

            // On update the account keeps its own code; only a clash with a *different* account counts.
            if (!string.IsNullOrWhiteSpace(code)
                && codes.Contains(code!)
                && !string.Equals(code, existingCode, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Account code '{code}' is already in use.");
            }

            if (!string.IsNullOrWhiteSpace(parentCode))
            {
                if (string.Equals(parentCode, code, StringComparison.OrdinalIgnoreCase))
                    errors.Add("An account cannot be its own parent.");
                else if (!codes.Contains(parentCode!))
                    errors.Add($"Parent account '{parentCode}' does not exist.");
            }
        }

        return errors;
    }
}
