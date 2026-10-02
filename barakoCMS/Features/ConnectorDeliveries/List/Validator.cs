using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.ConnectorDeliveries.List;

internal sealed class Validator : Validator<ListConnectorDeliveriesRequest>
{
    public Validator()
    {
        // Refused, the way the webhook delivery list refuses it. A filter that is dropped returns
        // more rows than were asked for, and the caller cannot tell that from "no matches".
        RuleFor(x => x.Status)
            .Must(status => status is not null
                && ListConnectorDeliveriesRequest.StatusClasses.Any(known => string.Equals(known, status.Trim(), StringComparison.OrdinalIgnoreCase)))
            .When(x => !string.IsNullOrWhiteSpace(x.Status))
            .WithMessage($"Status must be one of: {string.Join(", ", ListConnectorDeliveriesRequest.StatusClasses)}.");

        RuleFor(x => x.Connector).MaximumLength(200);
        RuleFor(x => x.RequestSlug).MaximumLength(200);
    }
}
