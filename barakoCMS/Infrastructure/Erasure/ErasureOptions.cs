using Microsoft.Extensions.Configuration;

namespace barakoCMS.Infrastructure.Erasure;

/// <summary>
/// The erasure policy this deployment runs, read once at startup.
/// </summary>
/// <remarks>
/// Validated at startup rather than at first use, because the failure this guards against is an
/// operator believing a mode is in force when it is not, and the only moment that belief is cheap to
/// correct is before the deployment serves anything. See DECISIONS.md D9.
/// </remarks>
public sealed class ErasureOptions
{
    public ErasureMode Mode { get; init; } = ErasureMode.Compact;

    /// <summary>Required to run with <see cref="ErasureMode.None"/>, so it cannot be arrived at by accident.</summary>
    public bool AcknowledgeNoErasure { get; init; }

    public static ErasureOptions FromConfiguration(IConfiguration configuration)
    {
        var raw = configuration["Erasure:Mode"];
        var mode = ErasureMode.Compact;

        if (!string.IsNullOrWhiteSpace(raw) && !Enum.TryParse(raw, ignoreCase: true, out mode))
        {
            throw new InvalidOperationException(
                $"Erasure:Mode is '{raw}', which is not a mode. Valid values: "
                + string.Join(", ", Enum.GetNames<ErasureMode>()) + ".");
        }

        return new ErasureOptions
        {
            Mode = mode,
            AcknowledgeNoErasure = configuration.GetValue<bool>("Erasure:AcknowledgeNoErasure"),
        };
    }

    /// <summary>
    /// Throws when the configured mode cannot do what its name says.
    /// </summary>
    public void Validate()
    {
        if (Mode == ErasureMode.CryptoShred)
        {
            // Refused rather than accepted-and-inert. An operator who sets CryptoShred has decided
            // they need real erasure; letting the deployment start while nothing encrypts anything
            // would give them the belief without the property, which is the exact failure D9 exists
            // to prevent, and it is the one that only surfaces in a regulator's letter.
            throw new InvalidOperationException(
                "Erasure:Mode is CryptoShred, which is not available yet. It needs an answer to which "
                + "field identifies the data subject of a content item, and a CMS has no natural one "
                + "(see DECISIONS.md D9 and issue #301). Use Compact, which removes the events, the "
                + "stream and the document.");
        }

        if (Mode == ErasureMode.None && !AcknowledgeNoErasure)
        {
            throw new InvalidOperationException(
                "Erasure:Mode is None, which leaves no way to erase content. Set "
                + "Erasure:AcknowledgeNoErasure to true to confirm this deployment's content never "
                + "holds personal data, or use Compact.");
        }
    }
}
