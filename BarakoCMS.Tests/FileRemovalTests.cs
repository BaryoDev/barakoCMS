using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Security;
using BarakoCMS.Files;
using BarakoCMS.Files.Storage;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BarakoCMS.Tests;

/// <summary>
/// What removing a file does, and in which order, against a real database and a storage that
/// records what it was asked to do.
/// </summary>
[Collection("Integration1")]
public class FileRemovalTests(IntegrationTestFixture factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class SpyStorage(Func<string, Task>? onDelete = null) : IFileStorage
    {
        public string Provider => "spy";

        public List<string> Deleted { get; } = [];

        public Task<StoredObjectRef> PutAsync(Stream content, string key, string contentType, bool isPublic, CancellationToken ct = default) =>
            Task.FromResult(new StoredObjectRef(key, null));

        public Task<byte[]?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);

        public string? PublicUrl(string key, bool isPublic) => null;

        public async Task DeleteAsync(string key, CancellationToken ct = default)
        {
            Deleted.Add(key);
            if (onDelete is not null)
            {
                await onDelete(key);
            }
        }
    }

    private static FileStorageResolver ResolverFor(IServiceProvider scoped, IFileStorage configured)
    {
        var environment = Substitute.For<IStorageEnvironment>();
        environment.Keys.Returns([]);

        return new FileStorageResolver(
            configured,
            new PostgresFileStorage(scoped.GetRequiredService<IDocumentSession>()),
            [],
            environment,
            scoped.GetRequiredService<IQuerySession>(),
            new MemoryCache(new MemoryCacheOptions()),
            Substitute.For<ISecretProtector>(),
            new TenantContext(),
            NullLogger<FileStorageResolver>.Instance);
    }

    private async Task<StoredFile> StoreFileAsync(string provider)
    {
        var file = new StoredFile
        {
            FileName = $"removal-{Guid.NewGuid():N}.pdf",
            Provider = provider,
            StorageKey = $"removal/{Guid.NewGuid():N}",
        };

        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(file);
        await session.SaveChangesAsync(Ct);
        return file;
    }

    private async Task<StoredFile?> RecordAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<StoredFile>(id, Ct);
    }

    private static Task RemoveAsync(IServiceProvider scoped, FileStorageResolver resolver, StoredFile file) =>
        FileRemoval.RemoveAsync(
            scoped.GetRequiredService<IDocumentSession>(),
            resolver,
            "removal-tests",
            file,
            Guid.NewGuid(),
            "removal-tests",
            forced: true,
            ipAddress: null,
            Ct);

    [Fact]
    public async Task The_bytes_are_deleted_only_after_the_record_is_gone()
    {
        var file = await StoreFileAsync("spy");
        bool? recordStillThere = null;
        var spy = new SpyStorage(async _ => recordStillThere = await RecordAsync(file.Id) is not null);

        using (var scope = factory.Services.CreateScope())
        {
            await RemoveAsync(scope.ServiceProvider, ResolverFor(scope.ServiceProvider, spy), file);
        }

        spy.Deleted.Should().HaveCount(1);
        spy.Deleted.Should().Contain(file.StorageKey);
        recordStillThere.Should().BeFalse("bytes deleted while the record is still saved leave a record with nothing behind it");
        (await RecordAsync(file.Id)).Should().BeNull();
    }

    [Fact]
    public async Task A_failed_byte_delete_does_not_skip_the_rest()
    {
        var file = await StoreFileAsync("spy");
        var variant = new StoredFile
        {
            FileName = file.FileName,
            Provider = "spy",
            StorageKey = $"removal/{Guid.NewGuid():N}",
            ParentFileId = file.Id,
            VariantWidth = 100,
        };
        using (var scope = factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(variant);
            await session.SaveChangesAsync(Ct);
        }

        var spy = new SpyStorage(key => key == variant.StorageKey
            ? throw new IOException("storage unavailable")
            : Task.CompletedTask);

        using (var scope = factory.Services.CreateScope())
        {
            var remove = () => RemoveAsync(scope.ServiceProvider, ResolverFor(scope.ServiceProvider, spy), file);

            await remove.Should().ThrowAsync<IOException>();
        }

        spy.Deleted.Should().HaveCount(2, "the original's bytes are still tried after the resize's delete failed");
        spy.Deleted.Should().Contain(file.StorageKey);
        (await RecordAsync(file.Id)).Should().BeNull();
        (await RecordAsync(variant.Id)).Should().BeNull();
    }

    [Fact]
    public async Task A_file_whose_storage_cannot_be_built_is_kept_with_its_bytes()
    {
        var file = await StoreFileAsync(StorageProviders.S3);
        var spy = new SpyStorage();

        using (var scope = factory.Services.CreateScope())
        {
            var remove = () => RemoveAsync(scope.ServiceProvider, ResolverFor(scope.ServiceProvider, spy), file);

            await remove.Should().ThrowAsync<InvalidOperationException>();
        }

        spy.Deleted.Should().BeEmpty("the bytes are not in the storage the fallback would have deleted from");
        (await RecordAsync(file.Id)).Should().NotBeNull("a record with no way to reach its bytes is not removed");
    }
}
