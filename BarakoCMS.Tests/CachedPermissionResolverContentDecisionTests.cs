using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using Marten.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace BarakoCMS.Tests;

[Collection("Integration1")]
public sealed class CachedPermissionResolverContentDecisionTests : IDisposable
{
    private readonly IntegrationTestFixture _fixture;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 100 });

    public CachedPermissionResolverContentDecisionTests(IntegrationTestFixture fixture) => _fixture = fixture;

    public void Dispose() => _cache.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private CachedPermissionResolver ResolverOver(IServiceProvider services) =>
        ActivatorUtilities.CreateInstance<CachedPermissionResolver>(
            services, ActivatorUtilities.CreateInstance<PermissionResolver>(services), _cache);

    [Fact]
    public async Task A_decision_read_during_a_user_invalidation_is_not_kept()
    {
        var slug = $"cachedperm_{Guid.NewGuid():N}";
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"cachedperm_{Guid.NewGuid():N}",
            Permissions = [new ContentTypePermission { ContentTypeSlug = slug, Read = new PermissionRule { Enabled = true } }],
        };
        var user = new User { Id = Guid.NewGuid(), Username = role.Name, Email = $"{role.Name}@example.com", RoleIds = [role.Id] };

        using var readScope = _fixture.Services.CreateScope();
        var readSession = readScope.ServiceProvider.GetRequiredService<IDocumentSession>();
        readSession.Store(role);
        await readSession.SaveChangesAsync(Ct);

        var reader = ResolverOver(readScope.ServiceProvider);
        readSession.Logger = new RunOnFirstQuery(() => reader.InvalidateUserPermissions(user.Id));

        Assert.True(await reader.CanPerformActionAsync(user, slug, "read", null, Ct));

        using var writeScope = _fixture.Services.CreateScope();
        var writeSession = writeScope.ServiceProvider.GetRequiredService<IDocumentSession>();
        role.Permissions = [];
        writeSession.Store(role);
        await writeSession.SaveChangesAsync(Ct);

        using var laterScope = _fixture.Services.CreateScope();
        var later = ResolverOver(laterScope.ServiceProvider);

        Assert.False(await later.CanPerformActionAsync(user, slug, "read", null, Ct));
    }

    private sealed class RunOnFirstQuery(Action action) : IMartenSessionLogger
    {
        private bool _ran;

        private void Once()
        {
            if (_ran) return;
            _ran = true;
            action();
        }

        public void LogSuccess(NpgsqlCommand command) { }
        public void LogFailure(NpgsqlCommand command, Exception ex) { }
        public void LogSuccess(NpgsqlBatch batch) { }
        public void LogFailure(NpgsqlBatch batch, Exception ex) { }
        public void LogFailure(Exception ex, string message) { }
        public void RecordSavedChanges(IDocumentSession session, IChangeSet commit) { }
        public void OnBeforeExecute(NpgsqlCommand command) => Once();
        public void OnBeforeExecute(NpgsqlBatch batch) => Once();
    }
}
