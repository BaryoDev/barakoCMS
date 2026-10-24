#pragma warning disable CS0618 // takes an advisory lock inside the write's transaction, which needs the session itself
using barakoCMS.Core.Interfaces;
using Marten;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Booking;

/// <summary>
/// The rule a booking needs that a schema cannot state: it may not overlap another active booking
/// of the same resource.
/// </summary>
/// <remarks>
/// <para>
/// Runs inside the write, after a transaction-scoped advisory lock on the tenant and resource. Two
/// concurrent requests for the same slot both pass a check made outside the write; with the lock the
/// second waits until the first commits or rolls back and then reads the committed booking, so
/// exactly one of them is accepted. Writes to different resources do not wait on each other.
/// </para>
/// <para>
/// Times are half open: a booking that ends when another starts does not overlap it. A booking whose
/// state is one of <see cref="BookingOptions.CancelledStatuses"/> holds nothing, and saving one is
/// never refused for overlap. The refusal does not say which booking holds the time, because a
/// booking can be written from a public form.
/// </para>
/// </remarks>
public sealed class BookingOverlapHook : IContentLifecycleHook
{
    private readonly BookingOptions _options;

    public BookingOverlapHook(IOptions<BookingOptions> options) => _options = options.Value;

    public string ContentType => _options.ContentType;

    public async Task<IReadOnlyList<string>> OnBeforeSaveAsync(ContentLifecycleContext context, CancellationToken ct)
    {
        var data = context.Data;
        if (BookedStretches.IsCancelled(data, _options))
        {
            return [];
        }

        var errors = new List<string>();
        var resourceId = BookingData.Guid(data, _options.ResourceField);
        if (resourceId is null)
        {
            errors.Add($"Field '{_options.ResourceField}' must be the id of a booking resource.");
        }

        var start = BookingData.Instant(data, _options.StartField);
        if (start is null)
        {
            errors.Add($"Field '{_options.StartField}' must be a date and time with a UTC offset, such as 2026-10-20T09:00:00+08:00.");
        }

        var end = BookingData.Instant(data, _options.EndField);
        if (end is null)
        {
            errors.Add($"Field '{_options.EndField}' must be a date and time with a UTC offset, such as 2026-10-20T10:00:00+08:00.");
        }

        if (start is { } s && end is { } e && e <= s)
        {
            errors.Add($"Field '{_options.EndField}' must be after '{_options.StartField}'.");
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        var session = context.Session;
        await session.BeginTransactionAsync(ct);
        var lockKey = $"barakocms:booking:{session.TenantId}:{resourceId:D}";
        await session.QueryAsync<int>("select 1 from pg_advisory_xact_lock(hashtextextended(?, 0))", ct, lockKey);

        var resource = await session.LoadAsync<BookingResource>(resourceId!.Value, ct);
        if (resource is null || !resource.Active)
        {
            return [$"There is no active booking resource '{resourceId}'."];
        }

        var requested = new Stretch(start!.Value, end!.Value);
        var held = await BookedStretches.HeldAsync(session, _options, resourceId.Value, context.EntryId, ct, requested.Start, requested.End);
        return held.Any(requested.Overlaps)
            ? ["That time is not available for this resource."]
            : [];
    }
}
