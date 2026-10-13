using barakoCMS.Core.Interfaces;

namespace barakoCMS.Infrastructure.Numbering;

/// <summary>The core gapless allocator, for code that builds its services without the container.</summary>
/// <remarks>
/// A module resolves <see cref="INumberAllocator"/> from the container. This is for a constructor
/// kept for callers that build a module's services by hand, so they number from the same counters
/// as every other path rather than from a count of their own, which would hand a number out twice.
/// </remarks>
public static class NumberAllocators
{
    public static INumberAllocator Gapless() => new GaplessNumberAllocator(TimeProvider.System);
}
