using barakoCMS.Features.EmailTemplates;
using barakoCMS.Infrastructure.Migrations;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.EmailTemplates;

/// <summary>
/// <c>ExpressionTemplates.ListAsync</c> reads email templates and workflows across tenants and
/// names each one that holds an expression, the way <c>db-migrate --status</c> prints it.
/// </summary>
[Collection("Integration1")]
public class StoredExpressionTemplatesListTests
{
    private readonly IntegrationTestFixture _fixture;

    public StoredExpressionTemplatesListTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private IDocumentStore Store() => _fixture.Services.GetRequiredService<IDocumentStore>();

    [Fact]
    public async Task A_stored_template_and_workflow_holding_an_expression_are_named_in_every_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store();
        var tenants = new[] { "exprlist-" + Guid.NewGuid().ToString("N"), "exprlist-" + Guid.NewGuid().ToString("N") };
        var entries = new List<(string Tenant, Guid Id)>();
        var plainIds = new List<Guid>();
        var stored = new List<(string Tenant, Guid Id)>();
        var workflows = new List<(string Tenant, Guid Id, string Name)>();

        try
        {
            foreach (var tenant in tenants)
            {
                var entry = new Content
                {
                    Id = Guid.NewGuid(),
                    ContentType = EmailTemplateRenderer.TemplateType,
                    Status = ContentStatus.Published,
                    Data = new Dictionary<string, object> { ["Body"] = "Total {{= 1}}", ["Subject"] = "Plain" },
                };
                var plain = new Content
                {
                    Id = Guid.NewGuid(),
                    ContentType = EmailTemplateRenderer.TemplateType,
                    Status = ContentStatus.Published,
                    Data = new Dictionary<string, object> { ["Body"] = "Hello {{data.Name}}" },
                };
                var workflow = new WorkflowDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = "exprlist-" + Guid.NewGuid().ToString("N"),
                    TriggerContentType = "post",
                    TriggerEvent = "Created",
                    Actions =
                    [
                        new WorkflowAction { Type = "Email", Parameters = new() { ["Subject"] = "{{= 1}}", ["To"] = "a@example.com" } },
                    ],
                };

                await using var session = store.LightweightSession(tenant);
                session.Store(entry, plain);
                session.Store(workflow);
                await session.SaveChangesAsync(ct);
                entries.Add((tenant, entry.Id));
                stored.Add((tenant, entry.Id));
                plainIds.Add(plain.Id);
                stored.Add((tenant, plain.Id));
                workflows.Add((tenant, workflow.Id, workflow.Name));
            }

            var lines = await ExpressionTemplates.ListAsync(store, ct);

            foreach (var (_, id) in entries)
            {
                lines.Should().Contain($"{EmailTemplateRenderer.TemplateType} {id}, field Body");
            }

            foreach (var (_, _, name) in workflows)
            {
                lines.Should().Contain($"workflow '{name}', 1 parameter(s)");
            }

            foreach (var id in plainIds)
            {
                lines.Should().NotContain(l => l.Contains(id.ToString()), "an entry with no expression is not listed");
            }
        }
        finally
        {
            foreach (var (tenant, id) in stored)
            {
                await using var session = store.LightweightSession(tenant);
                session.HardDelete<Content>(id);
                await session.SaveChangesAsync(ct);
            }

            foreach (var (tenant, id, _) in workflows)
            {
                await using var session = store.LightweightSession(tenant);
                session.Delete<WorkflowDefinition>(id);
                await session.SaveChangesAsync(ct);
            }
        }
    }
}
