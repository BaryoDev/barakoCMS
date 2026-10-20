using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using FastEndpoints;
using Marten;
using Microsoft.Extensions.Options;
using QRCoder;

namespace BarakoCMS.Qr.Features.Render;

/// <summary>
/// GET /api/public/{type}/{id}/qr/{field}?format=png|svg&amp;size=8: one field's value as a QR code.
/// </summary>
/// <remarks>
/// 404 for every refusal, so a caller cannot tell a missing entry from a draft, a field the type
/// does not allow from one that is empty. The entry goes through the same projector the core public
/// routes use, so it has to be published, Public, of a deliverable type, and the field has to be
/// marked Public on that type, on top of being listed in <see cref="QrOptions.Fields"/>.
/// </remarks>
internal sealed class Endpoint : Endpoint<Request>
{
    private const int DefaultPixelsPerModule = 8;

    private readonly IQuerySession _session;
    private readonly IPublicContentProjector _projector;
    private readonly QrOptions _options;

    public Endpoint(IQuerySession session, IPublicContentProjector projector, IOptions<QrOptions> options)
    {
        _session = session;
        _projector = projector;
        _options = options.Value;
    }

    public override void Configure()
    {
        Get("/api/public/{type}/{id}/qr/{field}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (!_options.Allows(req.Type, req.Field))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var lowered = req.Type.ToLower();
        var definition = await _session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name.ToLower() == lowered, ct);
        if (!_projector.IsDeliverable(definition)
            || await _session.LoadAsync<Content>(req.Id, ct) is not { } content
            || await _projector.ProjectAsync(content, definition, ct) is not { } entry
            || ValueOf(entry, req.Field) is not { Length: > 0 } value
            || value.Length > _options.MaxValueLength)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var size = req.Size ?? DefaultPixelsPerModule;
        using var generator = new QRCodeGenerator();
        QRCodeData data;
        try
        {
            data = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.M);
        }
        catch (QRCoder.Exceptions.DataTooLongException)
        {
            // Multi-byte text can exceed a code's capacity while under MaxValueLength characters.
            await Send.NotFoundAsync(ct);
            return;
        }

        using var _ = data;
        HttpContext.Response.Headers.CacheControl = "public, max-age=60";
        HttpContext.Response.Headers.Vary = barakoCMS.Infrastructure.Multitenancy.TenantResolutionMiddleware.TenantHeader;

        if (string.Equals(req.Format, "svg", StringComparison.OrdinalIgnoreCase))
        {
            var svg = new SvgQRCode(data).GetGraphic(size);
            await Send.BytesAsync(System.Text.Encoding.UTF8.GetBytes(svg), contentType: "image/svg+xml", cancellation: ct);
            return;
        }

        var png = new PngByteQRCode(data).GetGraphic(size);
        await Send.BytesAsync(png, contentType: "image/png", cancellation: ct);
    }

    private static string? ValueOf(PublicContentProjection entry, string field)
    {
        var raw = entry.Data.FirstOrDefault(p => string.Equals(p.Key, field, StringComparison.OrdinalIgnoreCase)).Value;
        return raw switch
        {
            null => null,
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            JsonElement { ValueKind: JsonValueKind.Number } e => e.GetRawText(),
            JsonElement => null,
            IConvertible c => c.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
        };
    }
}
