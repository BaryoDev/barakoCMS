using System.Security.Claims;
using barakoCMS.Core.Interfaces;

namespace BarakoCMS.Tests;

/// <summary>
/// What a module that keeps payment receipts would hold: it stores a PDF for the payer and reads it
/// back for whoever asks. It is written against <see cref="IFileStore"/> alone. This file names
/// nothing from BarakoCMS.Files, which is the point of it.
/// </summary>
internal sealed class ReceiptKeeper(IFileStore files)
{
    public async Task<Guid> KeepAsync(byte[] pdf, string name, Guid payer, CancellationToken ct)
    {
        using var content = new MemoryStream(pdf);
        var saved = await files.SaveAsync(
            new FileToStore { Content = content, FileName = name, ContentType = "application/pdf", Owner = payer }, ct);

        return saved.File?.Id ?? throw new InvalidOperationException(saved.Refused);
    }

    public async Task<byte[]?> ReadAsync(Guid receipt, ClaimsPrincipal reader, CancellationToken ct)
    {
        var stream = await files.OpenAsync(receipt, reader, ct);
        if (stream is null)
        {
            return null;
        }

        await using (stream)
        {
            using var held = new MemoryStream();
            await stream.CopyToAsync(held, ct);
            return held.ToArray();
        }
    }
}
