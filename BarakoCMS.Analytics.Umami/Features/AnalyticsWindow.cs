using FastEndpoints;
using FluentValidation;

namespace BarakoCMS.Analytics.Umami.Features;

/// <summary>A request that reads a window: a range token, or an explicit from and to.</summary>
internal interface IAnalyticsWindow
{
    DateTimeOffset? From { get; }
    DateTimeOffset? To { get; }
}

internal static class AnalyticsWindowRules
{
    public static void Apply<T>(Validator<T> validator) where T : IAnalyticsWindow
    {
        validator.RuleFor(x => x)
            .Must(x => x.From.HasValue == x.To.HasValue)
            .WithName("from")
            .WithMessage("from and to are set together, or neither is.");
        validator.RuleFor(x => x)
            .Must(x => x.From is not { } from || x.To is not { } to || from < to)
            .WithName("to")
            .WithMessage("to must be after from.");
        validator.RuleFor(x => x)
            .Must(x => x.From is not { } from || x.To is not { } to || to - from <= AnalyticsRange.MaxWindow)
            .WithName("to")
            .WithMessage($"A window is at most {AnalyticsRange.MaxWindow.TotalDays} days.");
    }
}
