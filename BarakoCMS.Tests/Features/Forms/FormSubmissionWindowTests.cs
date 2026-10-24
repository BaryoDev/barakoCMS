using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Forms;

/// <summary>
/// A form with an opening and closing date read from a singleton entry: submissions outside the
/// window are refused with 403, and editing the date moves the window with no restart.
/// </summary>
[Collection("Integration1")]
public class FormSubmissionWindowTests
{
    private readonly IntegrationTestFixture _factory;

    public FormSubmissionWindowTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task A_submission_before_the_opening_date_is_refused_and_stores_nothing()
    {
        var (form, _) = await SetUpAsync(opens: DateTime.UtcNow.AddDays(1), closes: DateTime.UtcNow.AddDays(30));

        var response = await SubmitAsync(form);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("not open yet");
        (await EntriesAsync(form)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_submission_after_the_closing_date_is_refused_and_stores_nothing()
    {
        var (form, _) = await SetUpAsync(opens: DateTime.UtcNow.AddDays(-30), closes: DateTime.UtcNow.AddDays(-1));

        var response = await SubmitAsync(form);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("closed");
        (await EntriesAsync(form)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_submission_inside_the_window_is_accepted()
    {
        var (form, _) = await SetUpAsync(opens: DateTime.UtcNow.AddDays(-1), closes: DateTime.UtcNow.AddDays(1));

        var response = await SubmitAsync(form);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        (await EntriesAsync(form)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Moving_the_closing_date_reopens_the_form_with_no_restart()
    {
        var (form, settings) = await SetUpAsync(opens: DateTime.UtcNow.AddDays(-30), closes: DateTime.UtcNow.AddDays(-1));
        (await SubmitAsync(form)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await SetDatesAsync(settings, opens: DateTime.UtcNow.AddDays(-30), closes: DateTime.UtcNow.AddDays(7));

        var response = await SubmitAsync(form);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        (await EntriesAsync(form)).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_window_entry_stored_under_another_letter_case_still_closes_the_form()
    {
        var (form, settings) = await SetUpAsync(opens: DateTime.UtcNow.AddDays(-30), closes: DateTime.UtcNow.AddDays(-1));
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var entry = await session.Query<Content>().FirstAsync(c => c.ContentType == settings);
            entry.ContentType = settings.ToUpperInvariant();
            session.Store(entry);
            await session.SaveChangesAsync();
        }

        var response = await SubmitAsync(form);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await EntriesAsync(form)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_form_with_no_window_configured_takes_submissions_as_before()
    {
        var form = await CreateFormTypeAsync();
        await PutFormAsync(form, new { enabled = true });

        var response = await SubmitAsync(form);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        using var definition = JsonDocument.Parse(await Visitor().GetStringAsync($"/api/public/forms/{form}"));
        definition.RootElement.TryGetProperty("open", out var open).Should().BeTrue();
        open.ValueKind.Should().Be(JsonValueKind.Null, "a form with no window reports nothing about one");
    }

    [Fact]
    public async Task The_anonymous_definition_reports_whether_the_form_is_open()
    {
        var (closed, _) = await SetUpAsync(opens: DateTime.UtcNow.AddDays(-30), closes: DateTime.UtcNow.AddDays(-1));
        var (open, _) = await SetUpAsync(opens: DateTime.UtcNow.AddDays(-1), closes: DateTime.UtcNow.AddDays(1));

        using var closedBody = JsonDocument.Parse(await Visitor().GetStringAsync($"/api/public/forms/{closed}"));
        using var openBody = JsonDocument.Parse(await Visitor().GetStringAsync($"/api/public/forms/{open}"));

        closedBody.RootElement.GetProperty("open").GetBoolean().Should().BeFalse();
        closedBody.RootElement.GetProperty("closedMessage").GetString().Should().NotBeNullOrWhiteSpace();
        openBody.RootElement.GetProperty("open").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Setting_a_window_on_a_type_that_is_not_a_singleton_or_a_field_that_is_not_a_date_is_refused()
    {
        var form = await CreateFormTypeAsync();
        var notSingleton = await CreateSettingsTypeAsync(singleton: false);
        var settings = await CreateSettingsTypeAsync(singleton: true);

        var wrongType = await PutFormAsync(form, new { enabled = true, windowContentType = notSingleton, closesAtField = "closesAt" });
        var wrongField = await PutFormAsync(form, new { enabled = true, windowContentType = settings, closesAtField = "label" });
        var noType = await PutFormAsync(form, new { enabled = true, closesAtField = "closesAt" });

        wrongType.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        wrongField.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        noType.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SubmitAsync(form)).StatusCode.Should().Be(HttpStatusCode.NotFound, "a refused PUT does not enable the form");
    }

    private async Task<(string Form, string Settings)> SetUpAsync(DateTime? opens, DateTime? closes)
    {
        var form = await CreateFormTypeAsync();
        var settings = await CreateSettingsTypeAsync(singleton: true);
        await SetDatesAsync(settings, opens, closes);
        var put = await PutFormAsync(form, new
        {
            enabled = true,
            windowContentType = settings,
            opensAtField = "opensAt",
            closesAtField = "closesAt",
        });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        return (form, settings);
    }

    private async Task SetDatesAsync(string settings, DateTime? opens, DateTime? closes)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var entry = await session.Query<Content>().FirstOrDefaultAsync(c => c.ContentType == settings)
            ?? new Content { Id = Guid.NewGuid(), ContentType = settings, Status = ContentStatus.Published };
        entry.Data = new Dictionary<string, object> { ["label"] = "Registration" };
        if (opens is { } o) entry.Data["opensAt"] = o.ToString("O");
        if (closes is { } c) entry.Data["closesAt"] = c.ToString("O");
        entry.UpdatedAt = DateTime.UtcNow;
        session.Store(entry);
        await session.SaveChangesAsync();
    }

    private async Task<string> CreateFormTypeAsync()
    {
        var name = $"form-{Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = "Registration",
            Fields = [new FieldDefinition { Name = "name", DisplayName = "Name", Type = "string", IsRequired = true }],
        });
        await session.SaveChangesAsync();
        return name;
    }

    private async Task<string> CreateSettingsTypeAsync(bool singleton)
    {
        var name = $"settings-{Guid.NewGuid():N}";
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = "Settings",
            IsSingleton = singleton,
            Fields =
            [
                new FieldDefinition { Name = "label", DisplayName = "Label", Type = "string" },
                new FieldDefinition { Name = "opensAt", DisplayName = "Opens at", Type = "datetime" },
                new FieldDefinition { Name = "closesAt", DisplayName = "Closes at", Type = "datetime" },
            ],
        });
        await session.SaveChangesAsync();
        return name;
    }

    private async Task<HttpResponseMessage> PutFormAsync(string form, object body)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("Admin"));
        return await client.PutAsJsonAsync($"/api/forms/{form}", body);
    }

    private HttpClient Visitor()
    {
        var client = _factory.CreateClient();
        var bytes = Guid.NewGuid().ToByteArray();
        var ip = $"2001:db8::{bytes[0]:x2}{bytes[1]:x2}:{bytes[2]:x2}{bytes[3]:x2}:{bytes[4]:x2}{bytes[5]:x2}";
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ip);
        return client;
    }

    private Task<HttpResponseMessage> SubmitAsync(string form) =>
        Visitor().PostAsJsonAsync($"/api/public/forms/{form}", new { data = new { name = "Ana" } });

    private async Task<IReadOnlyList<Content>> EntriesAsync(string form)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<Content>().Where(c => c.ContentType == form).ToListAsync();
    }
}
