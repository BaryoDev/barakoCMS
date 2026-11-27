using BarakoCMS.Tests.Features.Connectors;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.EmailTemplates;

/// <summary>
/// The bounds a template runs inside: a reference loop of 50, a loop that does not nest, an output
/// of 1 MB, and a template that is read in full or refused.
/// </summary>
[Collection("Integration1")]
public class TemplateLimitTests(IntegrationTestFixture fixture)
{
    private const string Loop = "{{#each data.Runners}}{{data.FirstName}},{{/each}}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(50, false)]
    [InlineData(51, true)]
    public async Task A_reference_loop_renders_fifty_and_notes_the_rest_only_past_fifty(int references, bool noted)
    {
        var (text, notes) = await RenderLoopAsync(references);

        text.Split(',', StringSplitOptions.RemoveEmptyEntries).Should().Equal(Enumerable.Range(0, 50).Select(i => $"R{i:00}"));
        if (noted)
        {
            notes.Should().Equal($"The loop over data.Runners rendered from the first 50 of its {references} references and stopped there.");
        }
        else
        {
            notes.Should().BeEmpty();
        }
    }

    [Fact]
    public void A_loop_inside_a_loop_is_left_as_written_and_named_by_the_problem_check()
    {
        const string Nested = "{{#each data.Runners}}{{#each data.Runners}}x{{/each}}{{/each}}";
        var entry = new Content { Id = Guid.NewGuid(), ContentType = "batch", Data = [] };

        TemplateVariableExtractor.Resolve(Nested, entry, TemplateValueEncoding.None).Should().Be(Nested);
        TemplateExpression.Pieces(Nested).Should().ContainSingle(p => p.Kind == TemplateExpression.PieceKind.Loop && p.Nested);
        TemplateExpression.Problems(Nested, onTransition: false).Should().ContainSingle()
            .Which.Should().Be("The loop over 'data.Runners' holds another loop, and loops do not nest, so it is sent as written.");
    }

    [Fact]
    public void A_dataset_loop_inside_a_loop_is_refused_by_the_name_of_the_dataset()
    {
        const string Nested = "{{#each lines}}{{#each lines}}{{row.Qty}}{{/each}}{{/each}}";

        TemplateExpression.Problems(Nested, onTransition: false).Should().ContainSingle()
            .Which.Should().Contain("'lines'").And.Contain("loops do not nest");
    }

    [Fact]
    public void An_output_just_under_one_megabyte_renders_and_one_just_over_fails_with_the_limit_in_the_message()
    {
        var entry = new Content { Id = Guid.NewGuid(), ContentType = "big", Data = new() { ["Big"] = new string('x', 20_000) } };
        var under = string.Concat(Enumerable.Repeat("{{data.Big}}", 50));
        var over = string.Concat(Enumerable.Repeat("{{data.Big}}", 53));

        TemplateVariableExtractor.Resolve(under, entry, TemplateValueEncoding.None).Should().HaveLength(1_000_000);

        var act = () => TemplateVariableExtractor.Resolve(over, entry, TemplateValueEncoding.None);
        act.Should().Throw<TemplateRenderException>().WithMessage("*more than 1048576 characters*");
    }

    [Fact]
    public async Task A_dataset_loop_that_renders_past_one_megabyte_fails_the_send_and_sends_nothing()
    {
        var kit = new InvoiceKit(fixture);
        await kit.SeedAsync(
            [.. Enumerable.Range(1, 200).Select(i => new InvoiceKit.Line(new string((char)('a' + i % 26), 6_000), 1, 1m, 1m))],
            template: "{{#each lines}}{{row.Product.Name}}\n{{/each}}");

        var (result, sent) = await kit.SendAsync();

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("more than 1048576 characters");
        sent.Should().BeNull();
    }

    [Fact]
    public void A_template_over_the_input_cap_is_refused_and_never_read_in_part()
    {
        var template = new string('a', TemplateHoles.MaxScanLength) + "{{Ssn}}";

        var act = () => TemplateHoles.Scan(template).ToList();

        act.Should().Throw<TemplateScanException>().Which.TimedOut.Should().BeFalse();
        TemplateExpression.Problems(template, onTransition: false).Should().ContainSingle()
            .Which.Should().Contain("at most 262144 characters").And.EndWith("It is sent as written.");
        TemplateExpression.NamesAddress(template).Should().BeTrue("a template that cannot be read is taken to name an address");
    }

    [Fact]
    public async Task A_template_whose_scan_runs_out_of_time_is_refused_and_the_send_fails_with_nothing_sent()
    {
        var kit = new InvoiceKit(fixture);
        await kit.SeedAsync([new("Widget", 1, 1m, 1m)]);

        TemplateHoles.TimeOutScans.Value = true;
        try
        {
            TemplateExpression.Problems("{{data.A}}", onTransition: false).Should().ContainSingle()
                .Which.Should().Contain("took too long").And.EndWith("It is sent as written.");
            TemplateExpression.NamesAddress("{{data.A}}").Should().BeTrue();

            var (result, sent) = await kit.SendAsync();

            result.Succeeded.Should().BeFalse();
            result.Error.Should().Contain("took too long");
            sent.Should().BeNull();
        }
        finally
        {
            TemplateHoles.TimeOutScans.Value = false;
        }
    }

    private async Task<(string Text, List<string> Notes)> RenderLoopAsync(int references)
    {
        var runner = "tl" + Guid.NewGuid().ToString("n")[..8];
        var batch = "tl" + Guid.NewGuid().ToString("n")[..8];

        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = runner, DisplayName = runner, Fields = [new FieldDefinition { Name = "FirstName", Type = "string" }],
        });
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = batch,
            DisplayName = batch,
            Fields = [new FieldDefinition { Name = "Runners", DisplayName = "Runners", Type = "reference", ReferenceType = runner, Multiple = true }],
        });

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = "Role_" + Guid.NewGuid().ToString("n"),
            Permissions = [.. new[] { batch, runner }.Select(t => new ContentTypePermission { ContentTypeSlug = t, Read = new PermissionRule { Enabled = true } })],
        };
        var user = new User { Id = Guid.NewGuid(), Username = role.Name, Email = role.Name + "@example.com", RoleIds = [role.Id] };
        session.Store(role);
        session.Store(user);

        var ids = new List<string>();
        for (var i = 0; i < references; i++)
        {
            var entry = new Content
            {
                Id = Guid.NewGuid(), ContentType = runner, Status = ContentStatus.Published, Data = new() { ["FirstName"] = $"R{i:00}" },
            };
            session.Store(entry);
            ids.Add(entry.Id.ToString());
        }

        var batchEntry = new Content
        {
            Id = Guid.NewGuid(), ContentType = batch, Status = ContentStatus.Published, Data = new() { ["Runners"] = ids }, LastModifiedBy = user.Id,
        };
        session.Store(batchEntry);
        await session.SaveChangesAsync(Ct);

        var extractor = scope.ServiceProvider.GetRequiredService<ITemplateVariableExtractor>();
        await extractor.PrepareAsync(batchEntry, WorkflowEvents.Updated, 0, [Loop], Ct);

        var text = extractor.ResolveVariables(Loop, batchEntry);
        return (text, [.. extractor.Notes]);
    }
}
