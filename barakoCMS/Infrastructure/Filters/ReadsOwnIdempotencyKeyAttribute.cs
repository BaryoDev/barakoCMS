namespace barakoCMS.Infrastructure.Filters;

/// <summary>
/// Marks an endpoint that reads the <c>Idempotency-Key</c> header itself, so the global filter
/// leaves the request alone: no claim, no replay, no 422 for a key reused on another path.
/// </summary>
/// <remarks>
/// For a route where the same key legitimately names different things, as a key sent to start one
/// workflow on two entries does. The endpoint derives an id from the key and the target and lets a
/// second insert of that id say the request was a repeat. That needs no stored response, and it
/// holds for as long as the run it made exists, which is until retention removes it.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class ReadsOwnIdempotencyKeyAttribute : Attribute;
