using FluentValidation;

namespace barakoCMS.Features.Content.Batch;

internal sealed class BatchValidator : FastEndpoints.Validator<BatchRequest>
{
    public BatchValidator()
    {
        RuleFor(x => x.Steps)
            .NotEmpty().WithMessage("At least one step is required.")
            .Must(s => s.Count <= BatchOps.MaxSteps).WithMessage($"A batch holds at most {BatchOps.MaxSteps} steps.");

        RuleForEach(x => x.Steps).ChildRules(step =>
        {
            step.RuleFor(s => s.Op)
                .Must(op => IsOp(op, BatchOps.Create) || IsOp(op, BatchOps.Transition))
                .WithMessage("Op is 'create' or 'transition'.");
            step.RuleFor(s => s.Id).NotEmpty().WithMessage("Id is required.");

            step.When(s => IsOp(s.Op, BatchOps.Create), () =>
            {
                step.RuleFor(s => s.ContentType).NotEmpty().WithMessage("ContentType is required to create.");
                step.RuleFor(s => s.Status)
                    .Must(v => v is null || BatchSteps.ParseStatus(v) is not null)
                    .WithMessage("Status is not a valid value.");
                step.RuleFor(s => s.Sensitivity)
                    .Must(v => v is null || BatchSteps.ParseSensitivity(v) is not null)
                    .WithMessage("Sensitivity is not a valid value.");
            });

            step.When(s => IsOp(s.Op, BatchOps.Transition), () =>
                step.RuleFor(s => s.Transition).NotEmpty().WithMessage("Transition is required to transition."));
        });
    }

    internal static bool IsOp(string? op, string expected) =>
        string.Equals(op, expected, StringComparison.OrdinalIgnoreCase);
}
