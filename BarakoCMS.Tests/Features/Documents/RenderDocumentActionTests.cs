using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using BarakoCMS.Documents;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Documents;

[Collection("Integration4")]
public class RenderDocumentActionTests(IntegrationTestFixture factory)
{
    private IDocumentStore Store => factory.Services.GetRequiredService<IDocumentStore>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class CountingFiles : IFileStore
    {
        public List<(string Name, byte[] Bytes)> Saved { get; } = [];

        public Task<StoredFileInfo?> FindPublicAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult<StoredFileInfo?>(null);

        public Task<Stream?> OpenPublicAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Stream?>(null);

        public Task<FileSaveResult> SaveAsync(FileToStore file, CancellationToken ct = default)
        {
            using var copy = new MemoryStream();
            file.Content.CopyTo(copy);
            Saved.Add((file.FileName, copy.ToArray()));
            return Task.FromResult(new FileSaveResult
            {
                File = new StoredFileInfo
                {
                    Id = Guid.NewGuid(),
                    FileName = file.FileName,
                    ContentType = file.ContentType,
                    Size = copy.Length,
                },
            });
        }
    }

    private sealed class ThrowingFiles : IFileStore
    {
        public Task<StoredFileInfo?> FindPublicAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult<StoredFileInfo?>(null);

        public Task<Stream?> OpenPublicAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Stream?>(null);

        public Task<FileSaveResult> SaveAsync(FileToStore file, CancellationToken ct = default) =>
            throw new IOException("the disk went away");
    }

    private sealed class SlowFiles : IFileStore
    {
        private readonly CountingFiles _inner = new();

        public int Count => _inner.Saved.Count;

        public Task<StoredFileInfo?> FindPublicAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult<StoredFileInfo?>(null);

        public Task<Stream?> OpenPublicAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Stream?>(null);

        public async Task<FileSaveResult> SaveAsync(FileToStore file, CancellationToken ct = default)
        {
            await Task.Delay(300, ct);
            return await _inner.SaveAsync(file, ct);
        }
    }

    private sealed class RefusingFiles(bool canRetry) : IFileStore
    {
        public Task<StoredFileInfo?> FindPublicAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult<StoredFileInfo?>(null);

        public Task<Stream?> OpenPublicAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Stream?>(null);

        public Task<FileSaveResult> SaveAsync(FileToStore file, CancellationToken ct = default) =>
            Task.FromResult(new FileSaveResult { Refused = "no room", CanRetry = canRetry });
    }

    private async Task<string> ResultKeyAsync(string tenant, string key)
    {
        await using var session = Store.LightweightSession(tenant);
        var template = await session.Query<DocumentTemplate>().SingleAsync(Ct);
        return DocumentRender.KeyFor(template.Id, key);
    }

    private async Task<bool> ClaimExistsAsync(string tenant, string resultKey, int generation)
    {
        await using var session = Store.LightweightSession(tenant);
        return await session.LoadAsync<DocumentRender>(DocumentRender.ClaimKeyFor(resultKey, generation), Ct) is not null;
    }

    private async Task<(string Tenant, Content Content)> SeedAsync(string source, string fileName = "")
    {
        var tenant = "doc-" + Guid.NewGuid().ToString("n")[..8];
        await using var session = Store.LightweightSession(tenant);

        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = "invoice",
            DisplayName = "Invoice",
            Fields =
            [
                new FieldDefinition { Name = "Number", Type = "string" },
                new FieldDefinition { Name = "TaxId", Type = "string", Sensitivity = SensitivityLevel.Sensitive },
            ],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        session.Store(new DocumentTemplate { Slug = "invoice", ContentType = "invoice", Source = source, FileName = fileName });
        await session.SaveChangesAsync(Ct);

        return (tenant, new Content
        {
            Id = Guid.NewGuid(),
            ContentType = "invoice",
            Data = new() { ["Number"] = "INV-1", ["TaxId"] = "123-45" },
        });
    }

    private static Dictionary<string, string> Parameters(string? key = null)
    {
        var p = new Dictionary<string, string> { ["Template"] = "invoice", ["TriggerEvent"] = "Published" };
        if (key is not null) p["IdempotencyKey"] = key;
        return p;
    }

    [Fact]
    public async Task Two_renders_under_the_same_idempotency_key_store_one_document()
    {
        var (tenant, content) = await SeedAsync("# Invoice {{Number}}");
        var files = new CountingFiles();
        var key = Guid.NewGuid().ToString("n") + "-0";

        await using var first = Store.LightweightSession(tenant);
        var one = await new RenderDocumentAction(first, files).RunAsync(Parameters(key), content, Ct);

        await using var second = Store.LightweightSession(tenant);
        var two = await new RenderDocumentAction(second, files).RunAsync(Parameters(key), content, Ct);

        one.Succeeded.Should().BeTrue("got: {0}", one.Error);
        two.Succeeded.Should().BeTrue("got: {0}", two.Error);
        files.Saved.Should().HaveCount(1);
        two.Outputs["FileId"].Should().Be(one.Outputs["FileId"]);
        files.Saved[0].Name.Should().Be("invoice.pdf");
    }

    [Fact]
    public async Task A_different_idempotency_key_stores_another_document()
    {
        var (tenant, content) = await SeedAsync("# Invoice {{Number}}");
        var files = new CountingFiles();

        await using var session = Store.LightweightSession(tenant);
        var action = new RenderDocumentAction(session, files);
        await action.RunAsync(Parameters("run-a-0"), content, Ct);
        await action.RunAsync(Parameters("run-b-0"), content, Ct);

        files.Saved.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_template_naming_a_Sensitive_field_stores_nothing()
    {
        var (tenant, content) = await SeedAsync("# Invoice\nTax id {{TaxId}}");
        var files = new CountingFiles();

        await using var session = Store.LightweightSession(tenant);
        var result = await new RenderDocumentAction(session, files).RunAsync(Parameters("k-0"), content, Ct);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("TaxId");
        files.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task A_save_that_throws_leaves_no_claim_so_the_retry_stores_one_document()
    {
        var (tenant, content) = await SeedAsync("# Invoice {{Number}}");
        var key = Guid.NewGuid().ToString("n") + "-0";

        await using (var crashed = Store.LightweightSession(tenant))
        {
            var act = () => new RenderDocumentAction(crashed, new ThrowingFiles()).RunAsync(Parameters(key), content, Ct);
            await act.Should().ThrowAsync<IOException>();
        }

        var files = new CountingFiles();
        await using var retry = Store.LightweightSession(tenant);
        var one = await new RenderDocumentAction(retry, files).RunAsync(Parameters(key), content, Ct);
        await using var again = Store.LightweightSession(tenant);
        var two = await new RenderDocumentAction(again, files).RunAsync(Parameters(key), content, Ct);

        one.Succeeded.Should().BeTrue("got: {0}", one.Error);
        files.Saved.Should().HaveCount(1);
        two.Outputs["FileId"].Should().Be(one.Outputs["FileId"]);
    }

    [Fact]
    public async Task Two_runs_at_once_under_one_key_store_one_document()
    {
        var (tenant, content) = await SeedAsync("# Invoice {{Number}}");
        var files = new SlowFiles();
        var key = Guid.NewGuid().ToString("n") + "-0";

        async Task<WorkflowActionResult> Run()
        {
            await using var session = Store.LightweightSession(tenant);
            return await new RenderDocumentAction(session, files).RunAsync(Parameters(key), content, Ct);
        }

        var results = await Task.WhenAll(Run(), Run());

        files.Count.Should().Be(1);
        results.Count(r => r.Succeeded).Should().BeGreaterThanOrEqualTo(1);
        results.Where(r => !r.Succeeded).Should().OnlyContain(r => r.Retryable, "the loser is told to try again");
    }

    [Fact]
    public async Task A_template_with_no_content_is_refused_with_a_clear_error()
    {
        var (tenant, content) = await SeedAsync("");
        var files = new CountingFiles();

        await using var session = Store.LightweightSession(tenant);
        var result = await new RenderDocumentAction(session, files).RunAsync(Parameters("e-0"), content, Ct);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("no content");
        files.Saved.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_name_loses_its_directories_and_control_characters()
    {
        var (tenant, content) = await SeedAsync("# Invoice", "..\\..\\etc/pa\u0001ss<>wd");
        var files = new CountingFiles();

        await using var session = Store.LightweightSession(tenant);
        var result = await new RenderDocumentAction(session, files).RunAsync(Parameters("n-0"), content, Ct);

        result.Succeeded.Should().BeTrue("got: {0}", result.Error);
        files.Saved.Should().HaveCount(1);
        files.Saved[0].Name.Should().Be("passwd.pdf");
    }

    [Fact]
    public async Task A_template_for_another_content_type_is_refused()
    {
        var (tenant, content) = await SeedAsync("# Invoice");
        content.ContentType = "receipt";
        var files = new CountingFiles();

        await using var session = Store.LightweightSession(tenant);
        var result = await new RenderDocumentAction(session, files).RunAsync(Parameters("m-0"), content, Ct);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("not 'receipt'");
        files.Saved.Should().BeEmpty();
    }

    [Fact]
    public void The_module_registers_the_render_action()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        new DocumentsModule().ConfigureServices(services, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        services.Should().ContainSingle(d => d.ServiceType == typeof(barakoCMS.Features.Workflows.IWorkflowAction)
            && d.ImplementationType == typeof(RenderDocumentAction));
        new DocumentsModule().Name.Should().Be("Documents");
    }

    [Fact]
    public async Task Two_retries_taking_over_one_abandoned_claim_store_one_document()
    {
        var (tenant, content) = await SeedAsync("# Invoice {{Number}}");
        var key = Guid.NewGuid().ToString("n") + "-0";
        var resultKey = await ResultKeyAsync(tenant, key);

        await using (var seed = Store.LightweightSession(tenant))
        {
            seed.Insert(new DocumentRender
            {
                Id = DocumentRender.ClaimKeyFor(resultKey, 0),
                ClaimedAt = DateTimeOffset.UtcNow - RenderDocumentAction.ClaimLifetime - TimeSpan.FromMinutes(1),
            });
            await seed.SaveChangesAsync(Ct);
        }

        var files = new SlowFiles();

        async Task<WorkflowActionResult> Run()
        {
            await using var session = Store.LightweightSession(tenant);
            return await new RenderDocumentAction(session, files).RunAsync(Parameters(key), content, Ct);
        }

        var results = await Task.WhenAll(Run(), Run());

        files.Count.Should().Be(1, "only one retry owns the next generation");
        results.Count(r => r.Succeeded).Should().Be(1);
        results.Single(r => !r.Succeeded).Retryable.Should().BeTrue();
    }

    [Fact]
    public async Task A_claim_that_is_not_abandoned_is_left_alone()
    {
        var (tenant, content) = await SeedAsync("# Invoice {{Number}}");
        var key = Guid.NewGuid().ToString("n") + "-0";
        var resultKey = await ResultKeyAsync(tenant, key);

        await using (var seed = Store.LightweightSession(tenant))
        {
            seed.Insert(new DocumentRender { Id = DocumentRender.ClaimKeyFor(resultKey, 0) });
            await seed.SaveChangesAsync(Ct);
        }

        var files = new CountingFiles();
        await using var session = Store.LightweightSession(tenant);
        var result = await new RenderDocumentAction(session, files).RunAsync(Parameters(key), content, Ct);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeTrue();
        files.Saved.Should().BeEmpty();
        (await ClaimExistsAsync(tenant, resultKey, 0)).Should().BeTrue("the live run's claim is not touched");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_file_store_refusal_releases_the_claim_so_the_retry_stores_one_document(bool canRetry)
    {
        var (tenant, content) = await SeedAsync("# Invoice {{Number}}");
        var key = Guid.NewGuid().ToString("n") + "-0";
        var resultKey = await ResultKeyAsync(tenant, key);

        await using (var refused = Store.LightweightSession(tenant))
        {
            var result = await new RenderDocumentAction(refused, new RefusingFiles(canRetry)).RunAsync(Parameters(key), content, Ct);
            result.Succeeded.Should().BeFalse();
            result.Retryable.Should().Be(canRetry);
        }

        (await ClaimExistsAsync(tenant, resultKey, 0)).Should().BeFalse("a refused save leaves no claim behind");

        var files = new CountingFiles();
        await using var retry = Store.LightweightSession(tenant);
        var again = await new RenderDocumentAction(retry, files).RunAsync(Parameters(key), content, Ct);

        again.Succeeded.Should().BeTrue("got: {0}", again.Error);
        files.Saved.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_finished_run_keeps_its_result_when_a_slower_run_finishes_after_it()
    {
        var (tenant, content) = await SeedAsync("# Invoice {{Number}}");
        var key = Guid.NewGuid().ToString("n") + "-0";
        var resultKey = await ResultKeyAsync(tenant, key);
        var winner = Guid.NewGuid();

        await using (var seed = Store.LightweightSession(tenant))
        {
            seed.Insert(new DocumentRender { Id = resultKey, FileId = winner });
            await seed.SaveChangesAsync(Ct);
        }

        var files = new CountingFiles();
        await using var session = Store.LightweightSession(tenant);
        var result = await new RenderDocumentAction(session, files).RunAsync(Parameters(key), content, Ct);

        result.Outputs["FileId"].Should().Be(winner.ToString());
        files.Saved.Should().BeEmpty();
    }
}
