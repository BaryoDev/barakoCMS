using System.Net;
using System.Text.Json;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using BarakoCMS.Tests.Features.Config;
using FluentAssertions;
using Marten;
using static BarakoCMS.Tests.Features.Config.ConfigCallers;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>A schedule on a workflow: the offset text, and how the workflows config section stores and refuses it.</summary>
[Collection("Integration1")]
public class ScheduleTriggerConfigTests
{
    private readonly ConfigCallers _callers;

    public ScheduleTriggerConfigTests(IntegrationTestFixture fixture) => _callers = new ConfigCallers(fixture);

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():n}"[..(prefix.Length + 10)];

    [Theory]
    [InlineData("0m", 0)]
    [InlineData("-24h", -24 * 60)]
    [InlineData("2d", 2 * 24 * 60)]
    [InlineData("-90m", -90)]
    [InlineData("365d", 365 * 24 * 60)]
    public void The_offset_reads_a_signed_number_and_a_unit(string text, int minutes)
    {
        ScheduleOffset.TryParse(text, out var offset).Should().BeTrue();
        offset.Should().Be(TimeSpan.FromMinutes(minutes));
    }

    [Theory]
    [InlineData("")]
    [InlineData("24")]
    [InlineData("1w")]
    [InlineData("h")]
    [InlineData("366d")]
    [InlineData("-9000h")]
    [InlineData("99999999999d")]
    public void The_offset_refuses_anything_else(string text) =>
        ScheduleOffset.TryParse(text, out _).Should().BeFalse();

    [Fact]
    public async Task A_schedule_is_stored_and_exported_back_unchanged()
    {
        var (tenant, type, client) = await TenantWithTypeAsync();
        var name = Unique("remind-");
        var document = new { version = 1, workflows = new Dictionary<string, object> { [name] = Workflow(type, "-24h", dateField: "startsAt") } };

        (await ApplyAsync(client, document)).Response.StatusCode.Should().Be(HttpStatusCode.OK);

        var stored = await _callers.InTenantAsync(tenant, s => s.Query<WorkflowDefinition>().SingleAsync(w => w.Name == name, Ct));
        stored!.DateSchedule.Should().NotBeNull();
        stored.DateSchedule!.Offset.Should().Be("-24h");
        stored.DateSchedule.InState.Should().Be("Booked");
        stored.DateSchedule.Where.Should().Equal(new Dictionary<string, string> { ["Type"] = "Walk-in" });
        stored.TriggerEvent.Should().Be($"schedule:{name}");
        stored.TriggerContentType.Should().Be(type);

        var (_, export) = await ExportAsync(client, "workflows");
        var exported = export.GetProperty("workflows").GetProperty(name);
        exported.GetProperty("dateSchedule").GetProperty("dateField").GetString().Should().Be("startsAt");
        exported.GetProperty("triggerEvents").GetArrayLength().Should().Be(0);

        var (response, plan) = await PlanAsync(client, new { version = 1, workflows = JsonSerializer.Deserialize<JsonElement>(export.GetProperty("workflows").GetRawText()) });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Changes(plan).Should().BeEmpty("the exported document applies as no change");
    }

    [Theory]
    [InlineData("unknown-type", "startsAt", "-24h", "dateSchedule.contentType")]
    [InlineData("", "", "-24h", "dateSchedule.dateField")]
    [InlineData("", "startsAt", "tomorrow", "dateSchedule.offset")]
    public async Task A_bad_schedule_is_refused_by_name(string contentType, string dateField, string offset, string field)
    {
        var (tenant, type, client) = await TenantWithTypeAsync();
        var name = Unique("remind-");
        var typeName = contentType.Length == 0 ? type : contentType;
        var document = new { version = 1, workflows = new Dictionary<string, object> { [name] = Workflow(typeName, offset, dateField) } };

        var (response, body) = await ApplyAsync(client, document);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body.ToString());
        body.ToString().Should().Contain(name).And.Contain(field);
        (await _callers.InTenantAsync(tenant, s => s.Query<WorkflowDefinition>().AnyAsync(w => w.Name == name, Ct))).Should().BeFalse();
    }

    private async Task<(string Tenant, string Type, HttpClient Client)> TenantWithTypeAsync()
    {
        var tenant = await _callers.TenantAsync();
        var type = Unique("Appointment");
        await _callers.StoreInTenantAsync(tenant, Type(type, ("startsAt", "string"), ("Type", "string")));
        return (tenant, type, await _callers.SuperAdminAsync(tenant));
    }

    private static object Workflow(string contentType, string offset, string dateField) => new
    {
        dateSchedule = new { contentType, dateField, offset, inState = "Booked", where = new Dictionary<string, string> { ["Type"] = "Walk-in" } },
        actions = new[] { new { type = "Webhook", parameters = new Dictionary<string, string> { ["Url"] = "https://hooks.example.com/remind" } } },
    };
}
