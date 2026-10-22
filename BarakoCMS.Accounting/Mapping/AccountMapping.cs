using Marten;

namespace BarakoCMS.Accounting;

/// <summary>One line of a mapping: the account, which side it posts to, and the amount text.</summary>
/// <remarks>
/// <c>Side</c> is <c>debit</c> or <c>credit</c>. <c>Amount</c> is <c>{{Amount}}</c> (the action's
/// Amount parameter), <c>{{Data.Field}}</c> (a decimal field of the entry being posted for) or a
/// decimal literal.
/// </remarks>
public sealed record MappingLine(string Account, string Side, string Amount);

/// <summary>The lines a business event posts, and whether a zero line is left out.</summary>
public sealed record MappingRule(List<MappingLine> Lines, bool SkipIfZero, Dictionary<string, string>? AccountByField);

/// <summary>A mapping as stored: one document per tenant and name.</summary>
public sealed class AccountMapping
{
    /// <summary>The mapping's name, which is also its document id.</summary>
    public string Name { get; set; } = string.Empty;

    public MappingRule Rule { get; set; } = new([], false, null);
}

/// <summary>Reads mappings by name, for the actions that post from them.</summary>
public sealed class AccountMappingStore(IDocumentSession session)
{
    public async Task<MappingRule?> GetAsync(string name, CancellationToken ct) =>
        (await session.LoadAsync<AccountMapping>(name, ct))?.Rule;
}
