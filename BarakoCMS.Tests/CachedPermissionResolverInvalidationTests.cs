using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BarakoCMS.Tests;

public sealed class CachedPermissionResolverInvalidationTests : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 100 });
    private readonly IDocumentSession _session = Substitute.For<IDocumentSession>();
    private readonly CachedPermissionResolver _resolver;

    public CachedPermissionResolverInvalidationTests()
    {
        var inner = new PermissionResolver(
            _session, Substitute.For<IConditionEvaluator>(), new TenantContext(), Substitute.For<IEventEnvelopeContext>());
        _resolver = new CachedPermissionResolver(inner, _cache, NullLogger<CachedPermissionResolver>.Instance, new TenantContext());
    }

    public void Dispose() => _cache.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RunDuringLookup(Guid userId, Action invalidate) =>
        _session.LoadAsync<User>(userId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            invalidate();
            return Task.FromResult<User?>(null);
        });

    private int Lookups(Guid userId) =>
        _session.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IQuerySession.LoadAsync)
            && c.GetArguments().FirstOrDefault() is Guid id && id == userId);

    [Fact]
    public async Task A_capability_read_during_a_user_invalidation_is_not_kept()
    {
        var userId = Guid.NewGuid();
        RunDuringLookup(userId, () => _resolver.InvalidateUserPermissions(userId));

        await _resolver.HasCapabilityAsync(userId, "content.read", Ct);
        await _resolver.HasCapabilityAsync(userId, "content.read", Ct);

        Assert.Equal(2, Lookups(userId));
    }

    [Fact]
    public async Task A_capability_read_during_a_global_invalidation_is_not_kept()
    {
        var userId = Guid.NewGuid();
        RunDuringLookup(userId, _resolver.InvalidateAllPermissions);

        await _resolver.HasCapabilityAsync(userId, "content.read", Ct);
        await _resolver.HasCapabilityAsync(userId, "content.read", Ct);

        Assert.Equal(2, Lookups(userId));
    }

    [Fact]
    public async Task A_capability_read_with_no_invalidation_is_kept()
    {
        var userId = Guid.NewGuid();

        await _resolver.HasCapabilityAsync(userId, "content.read", Ct);
        await _resolver.HasCapabilityAsync(userId, "content.read", Ct);

        Assert.Equal(1, Lookups(userId));
    }
}
