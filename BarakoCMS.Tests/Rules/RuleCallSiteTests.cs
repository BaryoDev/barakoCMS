using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using static BarakoCMS.Tests.Rules.RuleKit;

namespace BarakoCMS.Tests.Rules;

/// <summary>Where the engine is called: every lifecycle transition, by a user or by a module.</summary>
[Collection("Integration4")]
public class RuleCallSiteTests(IntegrationTestFixture host)
{
    private const string Refusal = "A order is required.";

    private static readonly ContentTransitionOptions Trusted = new() { SkipPermissionChecks = true };

    private async Task<string> TypeAsync()
    {
        var rule = Rule("order", Cond("Order", "notEmpty"));
        rule.When = RuleWhen.Transition;
        rule.Transition = "Issue";
        rule.Message = Refusal;
        rule.Set["Stamp"] = "{{Order}}-ok";

        var name = $"rcs{Guid.NewGuid():n}"[..20];
        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = "Receipt",
            Fields =
            [
                new FieldDefinition { Name = "Title", Type = "string" },
                new FieldDefinition { Name = "Order", Type = "string" },
                new FieldDefinition { Name = "Stamp", Type = "string" },
            ],
            Lifecycle = new LifecycleDefinition
            {
                States = ["Draft", "Issued", "Voided"],
                InitialState = "Draft",
                Transitions =
                [
                    new StateTransition { Name = "Issue", From = "Draft", To = "Issued" },
                    new StateTransition { Name = "Void", From = "Draft", To = "Voided" },
                ],
            },
            Rules = [rule],
        });
        await session.SaveChangesAsync();
        return name;
    }

    private async Task<HttpClient> AdminAsync()
    {
        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var roleIds = new List<Guid>();
        foreach (var name in new[] { "SuperAdmin", "Admin" })
        {
            var role = await session.Query<Role>().FirstOrDefaultAsync(r => r.Name == name);
            if (role is null) { role = new Role { Id = Guid.NewGuid(), Name = name }; session.Store(role); }
            roleIds.Add(role.Id);
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"rcs_{Guid.NewGuid():n}",
            Email = $"rcs_{Guid.NewGuid():n}@example.com",
            RoleIds = roleIds,
        };
        session.Store(user);
        await session.SaveChangesAsync();

        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", host.CreateToken(roles: ["SuperAdmin", "Admin"], userId: user.Id.ToString()));
        return client;
    }

    /// <summary>Raised by another admin, since nobody may move an entry they created.</summary>
    private async Task<Guid> EntryAsync(HttpClient _, string type, string? order = null)
    {
        var client = await AdminAsync();
        var data = new Dictionary<string, object> { ["Title"] = "a receipt" };
        if (order is not null) data["Order"] = order;
        var res = await client.PostAsJsonAsync("/api/contents", new { contentType = type, data });
        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, await res.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> MoveAsync(HttpClient client, Guid id, string transition) =>
        client.PutAsJsonAsync($"/api/contents/{id}/status", new { id, transition });

    private async Task<Content> LoadAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Content>(id))!;
    }

    private async Task<ContentTransitionResult> SystemMoveAsync(Content content, string transition)
    {
        using var scope = host.Services.CreateScope();
        var transitioner = scope.ServiceProvider.GetRequiredService<IContentTransitioner>();
        return await transitioner.TransitionAsync(content, transition, ContentTransitionActor.ForSystem("test"), Trusted);
    }

    [Fact]
    public async Task Failed_rule_returns_422_with_the_message()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync();
        var id = await EntryAsync(admin, type);

        var res = await MoveAsync(admin, id, "Issue");

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, await res.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var error = doc.RootElement.GetProperty("errors")[0];
        error.GetProperty("name").GetString().Should().Be("rules");
        error.GetProperty("reason").GetString().Should().Be(Refusal);
    }

    [Fact]
    public async Task No_data_transition_still_runs_rules()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync();
        var without = await EntryAsync(admin, type);
        var with = await EntryAsync(admin, type, order: "V1");

        (await MoveAsync(admin, without, "Issue")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var passed = await MoveAsync(admin, with, "Issue");
        passed.StatusCode.Should().Be(HttpStatusCode.OK, await passed.Content.ReadAsStringAsync());
        (await LoadAsync(with)).LifecycleState.Should().Be("Issued");
    }

    [Fact]
    public async Task Failed_rule_saves_nothing()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync();
        using (var scope = host.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new WorkflowDefinition
            {
                Id = Guid.NewGuid(),
                Name = $"rcswf{Guid.NewGuid():n}"[..20],
                TriggerContentType = type,
                TriggerEvent = WorkflowEvents.ForTransition("Issue"),
                Actions = [new WorkflowAction { Type = "CreateTask", Parameters = new() { ["ContentType"] = type + "probe", ["Title"] = "x" } }],
            });
            await session.SaveChangesAsync();
        }
        var id = await EntryAsync(admin, type);
        long before;
        using (var scope = host.Services.CreateScope())
            before = (await scope.ServiceProvider.GetRequiredService<IQuerySession>().Events.FetchStreamAsync(id)).Count;

        (await MoveAsync(admin, id, "Issue")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        await Task.Delay(TimeSpan.FromSeconds(2));

        (await LoadAsync(id)).LifecycleState.Should().Be("Draft");
        using var check = host.Services.CreateScope();
        var query = check.ServiceProvider.GetRequiredService<IQuerySession>();
        (await query.Events.FetchStreamAsync(id)).Count.Should().Be((int)before);
        var target = id.ToString();
        (await query.Query<AuditEvent>().Where(e => e.Action == "content.transitioned" && e.TargetId == target).ToListAsync())
            .Should().BeEmpty();
        (await query.Query<WorkflowRun>().Where(r => r.ContentId == id).ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Stale_version_wins_over_a_failing_rule()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync();
        var id = await EntryAsync(admin, type);
        var stale = await LoadAsync(id);
        (await MoveAsync(admin, id, "Void")).StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await SystemMoveAsync(stale, "Issue");

        result.Outcome.Should().Be(ContentTransitionOutcome.Conflict);
        result.Errors.Should().Equal("The content was changed by another writer. Please refresh and try again.");
    }

    [Fact]
    public async Task Passing_rule_set_writes_the_derived_field()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync();
        var id = await EntryAsync(admin, type, order: "V1");

        var res = await MoveAsync(admin, id, "Issue");

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var content = await LoadAsync(id);
        content.LifecycleState.Should().Be("Issued");
        content.Data["Stamp"].ToString().Should().Be("V1-ok");
    }

    [Fact]
    public async Task Module_style_system_call_is_also_checked()
    {
        var admin = await AdminAsync();
        var type = await TypeAsync();
        var id = await EntryAsync(admin, type);

        var result = await SystemMoveAsync(await LoadAsync(id), "Issue");

        result.Outcome.Should().Be(ContentTransitionOutcome.RuleFailed);
        result.Errors.Should().Equal(Refusal);
        (await LoadAsync(id)).LifecycleState.Should().Be("Draft");
    }

    [Fact]
    public async Task A_derived_field_cannot_move_the_entry_outside_the_actors_transition_rule()
    {
        var move = Rule("move");
        move.When = RuleWhen.Transition;
        move.Set["Region"] = "South";
        var type = $"rcs{Guid.NewGuid():n}"[..20];
        using (var scope = host.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                Fields = [new FieldDefinition { Name = "Title", Type = "string" }, new FieldDefinition { Name = "Region", Type = "string" }],
                Lifecycle = new LifecycleDefinition
                {
                    States = ["Draft", "Issued"],
                    InitialState = "Draft",
                    Transitions = [new StateTransition { Name = "Issue", From = "Draft", To = "Issued" }],
                },
                Rules = [move],
            });
            var role = new Role
            {
                Id = Guid.NewGuid(),
                Name = $"Role_{Guid.NewGuid():n}",
                Permissions =
                [
                    new ContentTypePermission
                    {
                        ContentTypeSlug = type,
                        Read = new PermissionRule { Enabled = true },
                        Transitions = new(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Issue"] = new PermissionRule
                            {
                                Enabled = true,
                                Conditions = new() { ["Region"] = new Dictionary<string, object> { ["_eq"] = "North" } },
                            },
                        },
                    },
                ],
            };
            var user = new User { Id = Guid.NewGuid(), Username = $"rcs_{Guid.NewGuid():n}", Email = $"rcs_{Guid.NewGuid():n}@example.com", RoleIds = [role.Id] };
            session.Store(role);
            session.Store(user);
            await session.SaveChangesAsync();
            var clerk = host.CreateClient();
            clerk.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", host.CreateToken(roles: [role.Name], userId: user.Id.ToString()));

            var admin = await AdminAsync();
            var res = await admin.PostAsJsonAsync("/api/contents", new { contentType = type, data = new Dictionary<string, object> { ["Title"] = "t", ["Region"] = "North" } });
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var id = doc.RootElement.GetProperty("id").GetGuid();

            var moved = await MoveAsync(clerk, id, "Issue");

            moved.StatusCode.Should().Be(HttpStatusCode.Forbidden, await moved.Content.ReadAsStringAsync());
            var content = await LoadAsync(id);
            content.LifecycleState.Should().Be("Draft");
            content.Data["Region"].ToString().Should().Be("North");
        }
    }

    /// <summary>A type with no lifecycle whose save rules need Code, set Slug and keep Code unique.</summary>
    private async Task<string> SaveTypeAsync()
    {
        var needs = Rule("code", Cond("Code", "notEmpty"));
        needs.Message = "A code is required.";
        needs.Set["Slug"] = "{{Name}}-x";
        var unique = Rule("unique", new RuleCondition { Kind = "unique", Field = "Code" });
        unique.Message = "That code is taken.";

        var name = $"rcs{Guid.NewGuid():n}"[..20];
        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = "Item",
            Fields =
            [
                new FieldDefinition { Name = "Name", Type = "string", IsRequired = true },
                new FieldDefinition { Name = "Code", Type = "string" },
                new FieldDefinition { Name = "Slug", Type = "string" },
            ],
            Rules = [needs, unique],
        });
        await session.SaveChangesAsync();
        return name;
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string type, Dictionary<string, object> data) =>
        client.PostAsJsonAsync("/api/contents", new { contentType = type, data });

    private async Task<List<Content>> EntriesAsync(string type)
    {
        using var scope = host.Services.CreateScope();
        var query = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await query.Query<Content>().Where(c => c.ContentType == type).ToListAsync()).ToList();
    }

    [Fact]
    public async Task Create_with_a_failing_save_rule_is_refused_and_nothing_is_stored()
    {
        var admin = await AdminAsync();
        var type = await SaveTypeAsync();

        var res = await CreateAsync(admin, type, new() { ["Name"] = "Pills" });

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, await res.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var error = doc.RootElement.GetProperty("errors")[0];
        error.GetProperty("name").GetString().Should().Be("rules");
        error.GetProperty("reason").GetString().Should().Be("A code is required.");
        (await EntriesAsync(type)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_non_rule_create_error_is_still_400()
    {
        var admin = await AdminAsync();
        var type = await SaveTypeAsync();

        var res = await CreateAsync(admin, type, new() { ["Code"] = "P1" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Create_applies_derived_fields()
    {
        var admin = await AdminAsync();
        var type = await SaveTypeAsync();

        var res = await CreateAsync(admin, type, new() { ["Name"] = "Pills", ["Code"] = "P1" });

        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync());
        (await EntriesAsync(type)).Single().Data["Slug"].ToString().Should().Be("Pills-x");
    }

    [Fact]
    public async Task Create_rules_see_other_entries()
    {
        var admin = await AdminAsync();
        var type = await SaveTypeAsync();
        (await CreateAsync(admin, type, new() { ["Name"] = "Pills", ["Code"] = "P1" })).IsSuccessStatusCode.Should().BeTrue();

        var second = await CreateAsync(admin, type, new() { ["Name"] = "Tabs", ["Code"] = "P1" });

        second.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, await second.Content.ReadAsStringAsync());
        (await second.Content.ReadAsStringAsync()).Should().Contain("That code is taken.");
        (await EntriesAsync(type)).Should().HaveCount(1);
    }

    private async Task<Guid> SavedAsync(HttpClient admin, string type, string name = "Pills", string code = "P1")
    {
        var res = await CreateAsync(admin, type, new() { ["Name"] = name, ["Code"] = code });
        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> UpdateAsync(HttpClient client, Guid id, Dictionary<string, object> data, bool dryRun = false) =>
        client.PutAsJsonAsync($"/api/contents/{id}", new { id, data, dryRun });

    [Fact]
    public async Task Update_with_a_failing_save_rule_is_refused_and_nothing_changes()
    {
        var admin = await AdminAsync();
        var type = await SaveTypeAsync();
        var id = await SavedAsync(admin, type);

        // Data is replaced whole, so leaving Code out removes it, and the rule sees that.
        var res = await UpdateAsync(admin, id, new() { ["Name"] = "Tabs" });

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, await res.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("errors")[0].GetProperty("name").GetString().Should().Be("rules");
        (await LoadAsync(id)).Data["Name"].ToString().Should().Be("Pills");
    }

    [Fact]
    public async Task Update_dry_run_reports_the_rule_failure()
    {
        var admin = await AdminAsync();
        var type = await SaveTypeAsync();
        var id = await SavedAsync(admin, type);

        var res = await UpdateAsync(admin, id, new() { ["Name"] = "Tabs" }, dryRun: true);

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Update_applies_derived_fields()
    {
        var admin = await AdminAsync();
        var type = await SaveTypeAsync();
        var id = await SavedAsync(admin, type);

        var res = await UpdateAsync(admin, id, new() { ["Name"] = "Tabs", ["Code"] = "P1" });

        res.IsSuccessStatusCode.Should().BeTrue(await res.Content.ReadAsStringAsync());
        (await LoadAsync(id)).Data["Slug"].ToString().Should().Be("Tabs-x");
    }

    [Fact]
    public async Task Update_unique_rule_ignores_the_entry_itself()
    {
        var admin = await AdminAsync();
        var type = await SaveTypeAsync();
        var id = await SavedAsync(admin, type);
        var other = await SavedAsync(admin, type, "Tabs", "T1");

        (await UpdateAsync(admin, id, new() { ["Name"] = "Pills 2", ["Code"] = "P1" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var taken = await UpdateAsync(admin, other, new() { ["Name"] = "Tabs", ["Code"] = "P1" });
        taken.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, await taken.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_derived_field_cannot_move_the_entry_outside_the_actors_update_rule()
    {
        var move = Rule("move");
        move.Set["Region"] = "South";
        var type = $"rcs{Guid.NewGuid():n}"[..20];
        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            Fields = [new FieldDefinition { Name = "Title", Type = "string" }, new FieldDefinition { Name = "Region", Type = "string" }],
        });
        var north = new PermissionRule
        {
            Enabled = true,
            Conditions = new() { ["Region"] = new Dictionary<string, object> { ["_eq"] = "North" } },
        };
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Role_{Guid.NewGuid():n}",
            Permissions = [new ContentTypePermission { ContentTypeSlug = type, Read = new PermissionRule { Enabled = true }, Update = north }],
        };
        var user = new User { Id = Guid.NewGuid(), Username = $"rcs_{Guid.NewGuid():n}", Email = $"rcs_{Guid.NewGuid():n}@example.com", RoleIds = [role.Id] };
        session.Store(role);
        session.Store(user);
        await session.SaveChangesAsync();
        var clerk = host.CreateClient();
        clerk.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", host.CreateToken(roles: [role.Name], userId: user.Id.ToString()));
        var admin = await AdminAsync();
        var created = await CreateAsync(admin, type, new() { ["Title"] = "t", ["Region"] = "North" });
        using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("id").GetGuid();

        // The rule is stored after the entry, so the create is not the one it moves.
        var definition = await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type);
        definition.Rules = [move];
        session.Store(definition);
        await session.SaveChangesAsync();

        var res = await UpdateAsync(clerk, id, new() { ["Title"] = "edited", ["Region"] = "North" });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, await res.Content.ReadAsStringAsync());
        (await LoadAsync(id)).Data["Region"].ToString().Should().Be("North");
    }

    /// <summary>A user whose one role may read the type and create only entries in Region North.</summary>
    private async Task<HttpClient> NorthClerkAsync(string type)
    {
        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var north = new PermissionRule
        {
            Enabled = true,
            Conditions = new() { ["Region"] = new Dictionary<string, object> { ["_eq"] = "North" } },
        };
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"Role_{Guid.NewGuid():n}",
            Permissions = [new ContentTypePermission { ContentTypeSlug = type, Read = new PermissionRule { Enabled = true }, Create = north }],
        };
        var user = new User { Id = Guid.NewGuid(), Username = $"rcs_{Guid.NewGuid():n}", Email = $"rcs_{Guid.NewGuid():n}@example.com", RoleIds = [role.Id] };
        session.Store(role);
        session.Store(user);
        await session.SaveChangesAsync();
        var clerk = host.CreateClient();
        clerk.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", host.CreateToken(roles: [role.Name], userId: user.Id.ToString()));
        return clerk;
    }

    private async Task<string> RegionTypeAsync(RuleDefinition rule, string regionType = "string", string regionFieldType = "string")
    {
        var type = $"rcs{Guid.NewGuid():n}"[..20];
        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = type,
            Fields =
            [
                new FieldDefinition { Name = "Code", Type = "string" },
                new FieldDefinition { Name = "Region", Type = regionFieldType },
                new FieldDefinition { Name = "Count", Type = regionType },
            ],
            Rules = [rule],
        });
        await session.SaveChangesAsync();
        return type;
    }

    [Fact]
    public async Task A_create_rule_refusal_is_not_shown_to_a_caller_whose_create_rule_does_not_hold()
    {
        var taken = Rule("unique", new RuleCondition { Kind = "unique", Field = "Code" });
        taken.Message = "That code is taken.";
        var type = await RegionTypeAsync(taken);
        var admin = await AdminAsync();
        (await CreateAsync(admin, type, new() { ["Code"] = "SECRET", ["Region"] = "South" })).IsSuccessStatusCode.Should().BeTrue();
        var clerk = await NorthClerkAsync(type);

        var probe = await CreateAsync(clerk, type, new() { ["Code"] = "SECRET", ["Region"] = "South" });

        probe.StatusCode.Should().Be(HttpStatusCode.Forbidden, await probe.Content.ReadAsStringAsync());
        (await probe.Content.ReadAsStringAsync()).Should().NotContain("taken");
        var allowed = await CreateAsync(clerk, type, new() { ["Code"] = "SECRET", ["Region"] = "North" });
        allowed.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, await allowed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_caller_whose_create_rule_does_not_hold_gets_403_whether_the_rule_passes_or_fails()
    {
        var taken = Rule("unique", new RuleCondition { Kind = "unique", Field = "Code" });
        taken.Message = "That code is taken.";
        var type = await RegionTypeAsync(taken, regionType: "int");
        var admin = await AdminAsync();
        (await CreateAsync(admin, type, new() { ["Code"] = "SECRET", ["Region"] = "South" })).IsSuccessStatusCode.Should().BeTrue();
        var clerk = await NorthClerkAsync(type);

        // An invalid field makes a passing rule fall through to validation, so a 400 here and a 403
        // for a taken code would tell the caller which codes exist.
        var free = await CreateAsync(clerk, type, new() { ["Code"] = "OTHER", ["Region"] = "South", ["Count"] = "x" });
        var held = await CreateAsync(clerk, type, new() { ["Code"] = "SECRET", ["Region"] = "South", ["Count"] = "x" });

        free.StatusCode.Should().Be(HttpStatusCode.Forbidden, await free.Content.ReadAsStringAsync());
        held.StatusCode.Should().Be(HttpStatusCode.Forbidden, await held.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_field_a_rule_derives_does_not_let_a_caller_tell_whether_the_rule_passed()
    {
        // A passing rule moves the entry into the caller's region; a failing one leaves it outside.
        var taken = Rule("unique", new RuleCondition { Kind = "unique", Field = "Code" });
        taken.Set["Region"] = "North";
        var type = await RegionTypeAsync(taken, regionType: "int");
        var admin = await AdminAsync();
        (await CreateAsync(admin, type, new() { ["Code"] = "SECRET", ["Region"] = "North" })).IsSuccessStatusCode.Should().BeTrue();
        var clerk = await NorthClerkAsync(type);

        var free = await CreateAsync(clerk, type, new() { ["Code"] = "OTHER", ["Region"] = "South", ["Count"] = "x" });
        var held = await CreateAsync(clerk, type, new() { ["Code"] = "SECRET", ["Region"] = "South", ["Count"] = "x" });

        free.StatusCode.Should().Be(HttpStatusCode.Forbidden, await free.Content.ReadAsStringAsync());
        held.StatusCode.Should().Be(HttpStatusCode.Forbidden, await held.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_field_the_caller_may_not_write_does_not_pass_the_create_rule_before_rules_run()
    {
        // Region is a token, which write sensitivity drops from what a caller sends. Asked of the
        // data as sent, the clerk's rule held; asked after a rule set Region only when it passed,
        // it told the clerk whether the code was taken.
        var taken = Rule("unique", new RuleCondition { Kind = "unique", Field = "Code" });
        taken.Set["Region"] = "North";
        var type = await RegionTypeAsync(taken, regionType: "int", regionFieldType: "token");
        var admin = await AdminAsync();
        (await CreateAsync(admin, type, new() { ["Code"] = "SECRET" })).IsSuccessStatusCode.Should().BeTrue();
        var clerk = await NorthClerkAsync(type);

        var free = await CreateAsync(clerk, type, new() { ["Code"] = "OTHER", ["Region"] = "North", ["Count"] = "x" });
        var held = await CreateAsync(clerk, type, new() { ["Code"] = "SECRET", ["Region"] = "North", ["Count"] = "x" });

        held.StatusCode.Should().Be(free.StatusCode, await free.Content.ReadAsStringAsync() + await held.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_derived_create_field_goes_through_schema_validation()
    {
        var bad = Rule("bad");
        bad.Set["Count"] = "not a number";
        var type = await RegionTypeAsync(bad, regionType: "int");
        var admin = await AdminAsync();

        var res = await CreateAsync(admin, type, new() { ["Code"] = "A" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        (await EntriesAsync(type)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_derived_create_field_cannot_move_the_entry_outside_the_actors_create_rule()
    {
        var move = Rule("move");
        move.Set["Region"] = "South";
        var type = await RegionTypeAsync(move);
        var clerk = await NorthClerkAsync(type);

        var res = await CreateAsync(clerk, type, new() { ["Code"] = "A", ["Region"] = "North" });

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, await res.Content.ReadAsStringAsync());
        (await EntriesAsync(type)).Should().BeEmpty();
    }
}
