using barakoCMS.Infrastructure.Envelope;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Numbering;

[Collection("Sequential")]
public class BranchCodesTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task The_branch_type_is_matched_ignoring_case()
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var type = "site" + Guid.NewGuid().ToString("n")[..8];
        var branch = new Content
        {
            Id = Guid.CreateVersion7(), ContentType = type,
            Data = new Dictionary<string, object> { ["Code"] = "BR1" },
        };
        session.Store(branch);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Envelope:BranchContentType"] = type.ToUpperInvariant() })
            .Build();

        var code = await new BranchCodes(session, config).CodeOfAsync(branch.Id, TestContext.Current.CancellationToken);

        code.Should().Be("BR1");
    }
}
