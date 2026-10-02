using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
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
/// address, which both paths take, and one per form, which a send takes first and which turning a
/// form on or off takes too. That is what makes the attempt cap and the two stored limits hold
/// against requests sent side by side. Mail is sent after the commit, so no lock is held across
/// the provider call.
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

    /// <summary>The longest address taken, the limit SMTP puts on a path.</summary>
    public const int MaxAddressLength = 254;

    /// <summary>Stale rows one send removes at most.</summary>
    internal const int SweepBatch = 200;

    private const int MaxLocalPartLength = 64;
    private const string LocalPartSymbols = ".!#$%&'*+/=?^_`{|}~-";
    private const int CodeLength = 6;
    private const int MaxLoggedNameLength = 100;

    /// <summary>
    /// Compared against when there is no live code, so that refusal costs the hash a real
    /// comparison costs. It does not make the two paths equal: a wrong code against a live one also
    /// writes and commits the attempt, a few milliseconds a caller who measures could see.
    /// </summary>
    private static readonly Lazy<string> NoCodeHash =
        new(() => BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N")));

    /// <summary>The one normalisation, used when a code is sent and when it is checked.</summary>
    public static string Normalise(string? address) => (address ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// Whether <paramref name="address"/> is one bare mailbox: ASCII letters, digits and the usual
    /// symbols before the @, a dotted host name after it, at most <see cref="MaxAddressLength"/>
    /// characters.
    /// </summary>
    /// <remarks>
    /// Narrower than the core's email field check on purpose. That one takes a display name, a
    /// comment or a trailing dot, each of which a mail library reads as the same mailbox while the
    /// string, and so the row and its limits, differs. Everything here is spelled one way, so the
    /// per address limit and the attempt cap count the mailbox and not the spelling. What it cannot
    /// see is an alias the provider resolves, such as a plus tag.
    /// </remarks>
    public static bool IsAddress(string? address)
    {
        var normalised = Normalise(address);
        if (normalised.Length is 0 or > MaxAddressLength)
        {
            return false;
        }

        var at = normalised.IndexOf('@');
        if (at <= 0 || at != normalised.LastIndexOf('@'))
        {
            return false;
        }

        var local = normalised[..at];
        var host = normalised[(at + 1)..];

        return local.Length <= MaxLocalPartLength
            && local.All(c => char.IsAsciiLetterOrDigit(c) || LocalPartSymbols.Contains(c))
            && DotsAreInside(local)
            && host.Contains('.')
            && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')
            && DotsAreInside(host);
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
    /// Takes the form's lock for the rest of the session's transaction. A send holds it while it
    /// reads and writes the form's <see cref="FormEmailBudget"/>, so anything else that writes that
    /// row takes it first.
    /// </summary>
    public static async Task LockFormAsync(IDocumentSession session, string form, CancellationToken ct)
    {
        await session.BeginTransactionAsync(ct);
        await LockAsync(session, $"form:{form}", ct);
    }

    /// <summary>
    /// On turning a form off: moves the field it verified onto the form's
    /// <see cref="FormEmailBudget"/> row, which outlives the form row, and returns it. A form that
    /// verified nothing leaves no row behind. Staged on <paramref name="session"/> for the caller
    /// to save.
    /// </summary>
    /// <remarks>
    /// Under the form's lock, because a send writes the same row and would otherwise store its
    /// own copy over this one.
    /// </remarks>
    public static async Task<string?> RememberFieldAsync(IDocumentSession session, string form, CancellationToken ct)
    {
        await LockFormAsync(session, form, ct);

        var current = await session.LoadAsync<PublicForm>(form, ct);
        var budget = await session.LoadAsync<FormEmailBudget>(form, ct);
        var verified = current is not null ? current.VerifyEmailField : budget?.VerifyEmailFieldWhenOff;

        if (string.IsNullOrWhiteSpace(verified))
        {
            session.Delete<FormEmailBudget>(form);
            return null;
        }

        budget ??= new FormEmailBudget { Id = form, WindowStartedAt = DateTimeOffset.UtcNow };
        budget.VerifyEmailFieldWhenOff = verified;
        session.Store(budget);
        return verified;
    }

    /// <summary>
    /// On turning a form on: the field it verified when it was turned off, or null, with the note
    /// of it cleared in the caller's save.
    /// </summary>
    public static async Task<string?> TakeRememberedFieldAsync(IDocumentSession session, string form, CancellationToken ct)
    {
        await LockFormAsync(session, form, ct);

        var budget = await session.LoadAsync<FormEmailBudget>(form, ct);
        if (budget?.VerifyEmailFieldWhenOff is not { } verified)
        {
            return null;
        }

        budget.VerifyEmailFieldWhenOff = null;
        session.Store(budget);
        return verified;
    }

    /// <summary>
    /// Stores a fresh code for <paramref name="address"/> on this form and emails it.
    /// </summary>
    /// <returns>
    /// False when the address or the form is past its limit, in which case nothing is stored or
    /// sent. True otherwise, whether or not the provider took the message: a send that fails or
    /// runs past its deadline is logged and not reported, so the answer does not depend on the
    /// provider's view of an address.
    /// </returns>
    public async Task<bool> RequestCodeAsync(ContentTypeDefinition definition, string address, CancellationToken ct)
    {
        var settings = options.Value.EmailVerification;
        var window = TimeSpan.FromMinutes(settings.WindowMinutes);
        var lifetime = TimeSpan.FromMinutes(settings.CodeLifetimeMinutes);

        var to = Normalise(address);
        var id = AddressId(to);

        // Before the locks, so the slow hash is not what other requests wait behind.
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        var hash = BCrypt.Net.BCrypt.HashPassword(code);

        await LockFormAsync(session, definition.Name, ct);
        await LockAsync(session, $"address:{id}", ct);

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

        if (budget.Sent >= settings.CodesPerForm || row.Sent >= settings.CodesPerAddress)
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

        var name = Clean(string.IsNullOrWhiteSpace(definition.DisplayName) ? definition.Name : definition.DisplayName);
        var body =
            $"<p>Your verification code for {WebUtility.HtmlEncode(name)} is:</p>"
          + $"<p style=\"font-size:28px;font-weight:700;letter-spacing:4px\">{code}</p>"
          + $"<p>It expires in {settings.CodeLifetimeMinutes} minutes and works once. If you did not ask for it, ignore this email.</p>";

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(settings.SendTimeoutSeconds));
            await email.SendForTenantAsync(tenant.Slug, to, $"Your verification code for {name}", body, deadline.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Filtered on the request's own token and not on the exception's type: a provider that
            // times out throws a cancellation while the caller is still there, and that is a failed
            // send like any other. Only the type is logged. A provider's message commonly quotes the
            // recipient, and the address stays out of the log.
            logger.LogError(
                "The email verification code for form {Form} in tenant {Tenant} could not be sent ({Failure}).",
                Clean(definition.Name), Clean(tenant.Slug), ex.GetType().Name);
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
                "Form {Form} in tenant {Tenant} verifies an email field its content type no longer offers, so every submission to it is refused.",
                Clean(definition.Name), Clean(tenant.Slug));
            failures.Add(new ValidationFailure(CodeField, "This form cannot take submissions right now."));
            return false;
        }

        var label = string.IsNullOrWhiteSpace(field.DisplayName) ? field.Name : field.DisplayName;
        var address = Normalise(data.TryGetValue(field.Name, out var value) ? Text(value) : null);
        if (address.Length == 0)
        {
            failures.Add(new ValidationFailure($"data.{field.Name}", $"'{label}' is required."));
            return false;
        }

        if (!IsAddress(address))
        {
            failures.Add(new ValidationFailure($"data.{field.Name}", $"'{label}' must be a plain email address, such as name@example.com."));
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

        var maxAttempts = options.Value.EmailVerification.MaxAttempts;
        var id = AddressId(address);

        await session.BeginTransactionAsync(ct);
        await LockAsync(session, $"address:{id}", ct);

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
    /// The table has no index on <c>LastSentAt</c>, so this reads one tenant's rows on every send,
    /// under the form's lock. The send limits bound how many rows a tenant gathers in a window.
    ///
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

    private static async Task LockAsync(IDocumentSession session, string what, CancellationToken ct) =>
        await session.QueryAsync<int>(
            "select 1 from pg_advisory_xact_lock(hashtextextended(?, 0))",
            ct,
            $"barakocms:forms-email-verification:{session.TenantId}:{what}");

    /// <summary>
    /// A stored name with control characters removed and a length cap, for a subject line or a log
    /// line. A content type name can hold a newline, and the tenant comes from a request header.
    /// </summary>
    private static string Clean(string value)
    {
        var clean = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > MaxLoggedNameLength ? clean[..MaxLoggedNameLength] : clean;
    }

    private static bool DotsAreInside(string part) =>
        part.Length > 0 && part[0] != '.' && part[^1] != '.' && !part.Contains("..", StringComparison.Ordinal);

    private static string? Text(object? value) => value switch
    {
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        _ => null,
    };
}
