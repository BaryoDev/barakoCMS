using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;
using FluentValidation.Results;
using Marten;

namespace BarakoCMS.Payments.Features.Payable;

internal sealed class Request
{
    public bool Enabled { get; set; }
    public string AmountField { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string PaidTransition { get; set; } = string.Empty;
    public string? FailedTransition { get; set; }
}

internal sealed class Response
{
    public string ContentType { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}

internal sealed class Validator : Validator<Request>
{
    public Validator()
    {
        When(x => x.Enabled, () =>
        {
            RuleFor(x => x.AmountField).NotEmpty();
            RuleFor(x => x.Currency).Matches("^[A-Za-z]{3}$").WithMessage("'Currency' is a three letter ISO 4217 code.");
            RuleFor(x => x.PaidTransition).NotEmpty();
        });
    }
}

/// <summary>
/// PUT /api/payments/payable/{contentType}. Declares what is payable on a content type, or stops it.
/// </summary>
/// <remarks>
/// Refuses a type with no lifecycle, or whose lifecycle lacks the named transitions, because a
/// payment could never move the entry. Refuses an amount field that is not a number field.
/// </remarks>
internal sealed class Endpoint(IDocumentSession session) : Endpoint<Request, Response>
{
    private static readonly string[] NumberTypes = ["money", "decimal", "int"];

    public override void Configure()
    {
        Put("/api/payments/payable/{contentType}");
        Definition.RequireCapability(PaymentsCapabilities.ManagePayments, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var name = Route<string>("contentType") ?? string.Empty;

        var definition = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == name, ct);
        if (definition is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!req.Enabled)
        {
            session.Delete<PayableType>(definition.Name);
            await session.SaveChangesAsync(ct);
            await Send.OkAsync(new Response { ContentType = definition.Name, Enabled = false }, ct);
            return;
        }

        var field = definition.Fields.FirstOrDefault(f => f.Name.Equals(req.AmountField, StringComparison.OrdinalIgnoreCase));
        if (field is null || !NumberTypes.Contains(field.Type, StringComparer.OrdinalIgnoreCase))
        {
            ValidationFailures.Add(new ValidationFailure("amountField", $"'{req.AmountField}' is not a money, decimal or int field of '{definition.Name}'."));
        }

        CheckTransition(definition, "paidTransition", req.PaidTransition);
        if (!string.IsNullOrEmpty(req.FailedTransition))
        {
            CheckTransition(definition, "failedTransition", req.FailedTransition);
        }

        if (ValidationFailures.Count > 0)
        {
            await Send.ErrorsAsync(400, ct);
            return;
        }

        session.Store(new PayableType
        {
            ContentType = definition.Name,
            AmountField = field!.Name,
            Currency = req.Currency.ToUpperInvariant(),
            Description = req.Description,
            PaidTransition = req.PaidTransition,
            FailedTransition = string.IsNullOrEmpty(req.FailedTransition) ? null : req.FailedTransition,
        });
        await session.SaveChangesAsync(ct);

        await Send.OkAsync(new Response { ContentType = definition.Name, Enabled = true }, ct);
    }

    private void CheckTransition(ContentTypeDefinition definition, string property, string transition)
    {
        var declared = definition.Lifecycle?.Transitions
            .FirstOrDefault(t => t.Name.Equals(transition, StringComparison.OrdinalIgnoreCase));
        if (declared is null)
        {
            ValidationFailures.Add(new ValidationFailure(property, $"'{definition.Name}' has no transition named '{transition}'."));
        }
        else if (declared.RequiredFields.Count > 0)
        {
            ValidationFailures.Add(new ValidationFailure(property, $"'{transition}' requires fields, and a provider event cannot send them."));
        }
    }
}
