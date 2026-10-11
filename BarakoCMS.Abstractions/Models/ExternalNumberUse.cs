namespace barakoCMS.Models;

public enum ExternalNumberStatus
{
    Issued,
    Voided,
    Spoiled,
}

/// <summary>
/// One number from an externally issued series, recorded as used. Voided and spoiled numbers are
/// recorded too: they are accounted for, which is what a gap report checks.
/// </summary>
public class ExternalNumberUse
{
    /// <summary>Derived from tenant, series and number, so recording one twice collides.</summary>
    public Guid Id { get; set; }

    public string Series { get; set; } = string.Empty;

    public long Number { get; set; }

    /// <summary>What the number was used on, for example the id of the payment it was printed for.</summary>
    public string Reference { get; set; } = string.Empty;

    public ExternalNumberStatus Status { get; set; }

    public DateTimeOffset RecordedAt { get; set; }
}
