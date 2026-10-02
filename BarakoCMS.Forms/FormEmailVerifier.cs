using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using FluentValidation.Results;
using Marten;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Forms;

/// <summary>
/// Sends the one-time code a form uses to verify an email field, and checks it on submit.
/// </summary>
/// <remarks>
/// The code travels with the submission and there is no separate check step. A check step would
/// hand back a token that has to be stored, bound and expired like the code, on one more anonymous
/// endpoint. Checking on submit lets the code be spent in the transaction that stores the entry, so
/// one code yields at most one entry.
///
/// Every read-then-write on a row here runs under a transaction-scoped advisory lock: one per
/// address, which both paths take, and one per form, which only a send takes and takes first. That
/// is what makes the attempt cap and the two stored limits hold against requests sent side by side.
/// Mail is sent after the commit, so no lock is held across the provider call.
/// </remarks>
internal sealed class FormEmailVerifier(
    IDocumentSession session,
    IEmailService email,
    TenantContext tenant,
    IOptions<FormsOptions> options,
    ILogger<FormEmailVerifier> logger)
{
    /// <summary>The submit request property a failure is reported against.</summary>
    public const string CodeField = "emailVerificationCode";

    public const string AuditAction = "form.email.verified";

    /// <summary>One sentence for a wrong, expired, spent, dead or missing code, so they cannot be told apart.</summary>
    public const string Refusal = "The code is wrong or has expired. Request a new one.";

    private const int CodeLength = 6;
    private const int MaxAddressLength = 254;
    private const int SweepBatch = 200;

    /// <summary>
    /// Compared against when there is no live code, so that refusal costs what a real comparison
    /// costs and the time taken does not say whether a code is outstanding for an address.
    /// </summary>
    private static readonly Lazy<string> NoCodeHash =
        new(() => BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N")));

    /// <summary>The one normalisation, used when a code is sent and when it is checked.</summary>
    public static string Normalise(string? address) => (address ?? string.Empty).Trim().ToLowerInvariant();

    public static bool IsAddress(string? address)
    {
        var normalised = Normalise(address);
        return normalised.Length is > 0 and <= MaxAddressLength
            && FieldTypeRegistry.IsValidValue("email", normalised);
    }

    public static string AddressId(string normalisedAddress) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalisedAddress)));

    /// <summary>The submittable email field called <paramref name="name"/>, or null.</summary>
    public static FieldDefinition? EmailField(ContentTypeDefinition definition, string name) =>
        FormFields.Submittable(definition).FirstOrDefault(f =>
            string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(f.Type, "email", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The field the form verifies, or null when it verifies none or names one the type no longer
    /// offers as a submittable email field.
    /// </summary>
    public static FieldDefinition? FieldToVerify(ContentTypeDefinition definition, PublicForm form) =>
        string.IsNullOrWhiteSpace(form.VerifyEmailField) ? null : EmailField(definition, form.VerifyEmailField);

    /// <summary>
    /// Stores a fresh code for <paramref name="address"/> on this form and emails it.
    /// </summary>
    /// <returns>
    /// False when the address or the form is past its limit, in which case nothing is stored or
    /// sent. True otherwise, whether or not the provider took the message: a failed send is logged
    /// and not reported, so the answer does not depend on the provider's view of an address.
    /// </returns>
    public async Task<bool> RequestCodeAsync(ContentTypeDefinition definition, string address, CancellationToken ct)
    {
        var settings = options.Value.EmailVerification;
        var window = TimeSpan.FromMinutes(Math.Max(1, settings.WindowMinutes));
        var lifetimeMinutes = Math.Max(1, settings.CodeLifetimeMinutes);
        var lifetime = TimeSpan.FromMinutes(lifetimeMinutes);

        var to = Normalise(address);
        var id = AddressId(to);

        // Before the locks, so the slow hash is not what other requests wait behind.
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        var hash = BCrypt.Net.BCrypt.HashPassword(code);

        await session.BeginTransactionAsync(ct);
        await LockAsync($"form:{definition.Name}", ct);
        await LockAsync($"address:{id}", ct);

        var now = DateTimeOffset.UtcNow;

        var budget = await session.LoadAsync<FormEmailBudget>(definition.Name, ct)
            ?? new FormEmailBudget { Id = definition.Name, WindowStartedAt = now };
        var row = await session.LoadAsync<FormEmailVerification>(id, ct)
            ?? new FormEmailVerification { Id = id, WindowStartedAt = now };

        if (now - budget.WindowStartedAt >= window)
        {
            budget.WindowStartedAt = now;
            budget.Sent = 0;
        }

        if (now - row.WindowStartedAt >= window)
        {
            row.WindowStartedAt = now;
            row.Sent = 0;
        }

        if (budget.Sent >= Math.Max(1, settings.CodesPerForm) || row.Sent >= Math.Max(1, settings.CodesPerAddress))
        {
            return false;
        }

        budget.Sent += 1;

        row.Form = definition.Name;
        row.CodeHash = hash;
        row.ExpiresAt = now + lifetime;
        row.Attempts = 0;
        row.Sent += 1;
        row.LastSentAt = now;

        session.Store(budget);
        session.Store(row);
        await StageSweepAsync(now - (window > lifetime ? window : lifetime), ct);
        await session.SaveChangesAsync(ct);

        var name = FormName(definition);
        var body =
            $"<p>Your verification code for {WebUtility.HtmlEncode(name)} is:</p>"
          + $"<p style=\"font-size:28px;font-weight:700;letter-spacing:4px\">{code}</p>"
          + $"<p>It expires in {lifetimeMinutes} minutes and works once. If you did not ask for it, ignore this email.</p>";

        try
        {
            await email.SendForTenantAsync(tenant.Slug, to, $"Your verification code for {name}", body, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The email verification code for form {Form} could not be sent.", definition.Name);
        }

        return true;
    }

    /// <summary>
    /// Decides whether a submission may be stored. True for a form that does not verify. For one
    /// that does, true only when <paramref name="code"/> is the live code for the address in the
    /// verified field, and then the code is spent and an audit event naming
    /// <paramref name="entryId"/> is staged, both committed by the caller's save with the entry.
    /// </summary>
    /// <remarks>
    /// A wrong code is counted and committed here, before the refusal, and the last allowed one
    /// kills the code. Nothing but the failure goes into <paramref name="failures"/>: never the code
    /// and never the address.
    /// </remarks>
    public async Task<bool> AcceptAsync(
        PublicForm form,
        ContentTypeDefinition definition,
        IReadOnlyDictionary<string, object> data,
        string? code,
        Guid entryId,
        List<ValidationFailure> failures,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(form.VerifyEmailField))
        {
            return true;
        }

        var field = FieldToVerify(definition, form);
        if (field is null)
        {
            // Refusing is the safe direction: the owner asked for verified addresses, and taking
            // unverified ones would look exactly like verification working.
            logger.LogError(
                "Form {Form} verifies an email field its content type no longer offers, so every submission to it is refused.",
                definition.Name);
            failures.Add(new ValidationFailure(CodeField, "This form cannot take submissions right now."));
            return false;
        }

        var address = Normalise(data.TryGetValue(field.Name, out var value) ? Text(value) : null);
        if (address.Length == 0)
        {
            var label = string.IsNullOrWhiteSpace(field.DisplayName) ? field.Name : field.DisplayName;
            failures.Add(new ValidationFailure($"data.{field.Name}", $"'{label}' is required."));
            return false;
        }

        var presented = (code ?? string.Empty).Trim();
        if (presented.Length == 0)
        {
            failures.Add(new ValidationFailure(CodeField, "Enter the code sent to your email address."));
            return false;
        }

        if (presented.Length != CodeLength || !presented.All(char.IsAsciiDigit))
        {
            failures.Add(new ValidationFailure(CodeField, Refusal));
            return false;
        }

        var maxAttempts = Math.Max(1, options.Value.EmailVerification.MaxAttempts);
        var id = AddressId(address);

        await session.BeginTransactionAsync(ct);
        await LockAsync($"address:{id}", ct);

        var row = await session.LoadAsync<FormEmailVerification>(id, ct);

        if (row?.CodeHash is null
            || !string.Equals(row.Form, definition.Name, StringComparison.Ordinal)
            || row.ExpiresAt <= DateTimeOffset.UtcNow
            || row.Attempts >= maxAttempts)
        {
            EmailVerificationToken.Matches(presented, NoCodeHash.Value);
            failures.Add(new ValidationFailure(CodeField, Refusal));
            return false;
        }

        row.Attempts += 1;

        if (!EmailVerificationToken.Matches(presented, row.CodeHash))
        {
            if (row.Attempts >= maxAttempts)
            {
                row.CodeHash = null;
            }

            session.Store(row);
            await session.SaveChangesAsync(ct);
            failures.Add(new ValidationFailure(CodeField, Refusal));
            return false;
        }

        row.CodeHash = null;
        session.Store(row);

        await AuditLog.RecordAsync(
            session,
            tenant.Slug,
            AuditAction,
            actorUserId: null,
            actorUsername: null,
            targetType: "Content",
            targetId: entryId.ToString(),
            metadata: new Dictionary<string, object> { ["form"] = definition.Name, ["field"] = field.Name },
            ct: ct);

        return true;
    }

    /// <summary>
    /// Stages the removal of rows whose code and window have both passed, at most
    /// <see cref="SweepBatch"/> of them, oldest first.
    /// </summary>
    /// <remarks>
    /// Deleted by a condition on the row and not by id. A send for one of those addresses can be
    /// rewriting its row at the same moment, and Postgres checks the condition again once that
    /// commits, so a row that was just refreshed is left alone.
    ///
    /// A full batch stops short of its newest row, which keeps one statement to a batch. Fewer than
    /// a batch is every stale row there is, and that set cannot grow, since a row's time only moves
    /// forward.
    /// </remarks>
    private async Task StageSweepAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        var oldest = await session.Query<FormEmailVerification>()
            .Where(v => v.LastSentAt < cutoff)
            .OrderBy(v => v.LastSentAt)
            .Take(SweepBatch)
            .Select(v => v.LastSentAt)
            .ToListAsync(ct);

        if (oldest.Count == 0)
        {
            return;
        }

        var before = oldest.Count < SweepBatch ? cutoff : oldest[oldest.Count - 1];
        session.DeleteWhere<FormEmailVerification>(v => v.LastSentAt < before);
    }

    private async Task LockAsync(string what, CancellationToken ct) =>
        await session.QueryAsync<int>(
            "select 1 from pg_advisory_xact_lock(hashtextextended(?, 0))",
            ct,
            $"barakocms:forms-email-verification:{session.TenantId}:{what}");

    /// <summary>The form's display name with control characters removed, since it goes into a subject line.</summary>
    private static string FormName(ContentTypeDefinition definition)
    {
        var name = string.IsNullOrWhiteSpace(definition.DisplayName) ? definition.Name : definition.DisplayName;
        var clean = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > 100 ? clean[..100] : clean;
    }

    private static string? Text(object? value) => value switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        _ => null,
    };
}
