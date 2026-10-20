using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using BarakoCMS.Documents;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Documents;

[Collection("Sequential")]
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

    private async Task<(string Tenant, Content Content)> SeedAsync(string source)
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
        session.Store(new DocumentTemplate { Slug = "invoice", ContentType = "invoice", Source = source });
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
}
