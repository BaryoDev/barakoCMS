namespace BarakoCMS.Qr.Features.Render;

internal sealed class Request
{
    public string Type { get; set; } = string.Empty;

    public Guid Id { get; set; }

    public string Field { get; set; } = string.Empty;

    /// <summary>png or svg. Defaults to png.</summary>
    public string? Format { get; set; }

    /// <summary>Pixels per QR module. Defaults to 8.</summary>
    public int? Size { get; set; }
}
