extern alias App;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net.Http.Json;
using System.Net;
using barakoCMS.Models;
using FastEndpoints.Security;
using System.Net.Http.Headers;
using Xunit; // Added based on the provided Code Edit
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Core.Interfaces;

namespace AttendancePOC.Tests;

public class AttendanceTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly CustomWebApplicationFactory _factory;

    public AttendanceTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task SubmitAttendance_ShouldTriggerWorkflow()
    {
        // 1. Authenticate as HR (Standard User)
        var token = JWTBearer.CreateToken(
            signingKey: "test-super-secret-key-that-is-at-least-32-chars-long",
            expireAt: DateTime.UtcNow.AddDays(1),
            privileges: u =>
            {
                u.Roles.Add("HR");
                u.Roles.Add("Admin"); // Required to access Create endpoint
                u.Claims.Add(new("UserId", Guid.NewGuid().ToString()));
                u.Claims.Add(new("Username", "hr_user"));
            });

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 2. Submit Attendance
        var data = new Dictionary<string, object>
        {
            { "FirstName", "John" },
            { "LastName", "Doe" },
            { "BirthDay", "1990-01-01" },
            { "JobDescription", "Software Engineer" },
            { "Gender", "Male" },
            { "SSN", "123-45-6789" }
        };

        var req = new barakoCMS.Features.Content.Create.Request
        {
            ContentType = "AttendanceRecord",
            Data = data,
            Status = ContentStatus.Published
        };

        var res = await _client.PostAsJsonAsync("/api/contents", req);
        res.EnsureSuccessStatusCode();

        // 3. Verify Workflow Triggered (Spy Email Service)
        var spy = _factory.Services.GetRequiredService<IEmailService>() as CustomWebApplicationFactory.SpyEmailService;
        spy.Should().NotBeNull();

        // Wait loop for async processing if needed, but since it's awaited in Endpoint, it should be done.
        // However, WorkflowEngine might do async things? It awaits _emailService.SendEmailAsync.
        // So it should be synchronous to the request.

        spy!.SentEmails.Should().Contain(e => e.To == "hr-group@company.com");
    }

    [Fact]
    public async Task GetAttendance_ShouldMaskSensitiveData_ForNonSuperAdmin()
    {
        // 1. Create Record as SuperAdmin
        var adminToken = JWTBearer.CreateToken(
            signingKey: "test-super-secret-key-that-is-at-least-32-chars-long",
            expireAt: DateTime.UtcNow.AddDays(1),
            privileges: u =>
            {
                u.Roles.Add("SuperAdmin");
                u.Roles.Add("Admin"); // Required to access Create endpoint
                u.Claims.Add(new("UserId", Guid.NewGuid().ToString()));
            });

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var data = new Dictionary<string, object>
        {
            { "FirstName", "Jane" },
            { "LastName", "Doe" },
            { "BirthDay", "1995-05-05" },
            { "SSN", "987-65-4321" }
        };

        var createRes = await _client.PostAsJsonAsync("/api/contents", new barakoCMS.Features.Content.Create.Request
        {
            ContentType = "AttendanceRecord",
            Data = data,
            Status = ContentStatus.Published
        });
        if (!createRes.IsSuccessStatusCode)
        {
            var error = await createRes.Content.ReadAsStringAsync();
            throw new Exception($"Create failed: {createRes.StatusCode} - {error}");
        }
        var contentId = (await createRes.Content.ReadFromJsonAsync<barakoCMS.Features.Content.Create.Response>())!.Id;

        // 2. Fetch as Standard User (Not HR, Not SuperAdmin)
        var userToken = JWTBearer.CreateToken(
            signingKey: "test-super-secret-key-that-is-at-least-32-chars-long",
            expireAt: DateTime.UtcNow.AddDays(1),
            privileges: u =>
            {
                u.Claims.Add(new("UserId", Guid.NewGuid().ToString()));
            });

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken);

        var getRes = await _client.GetAsync($"/api/contents/{contentId}");
        if (!getRes.IsSuccessStatusCode)
        {
            var error = await getRes.Content.ReadAsStringAsync();
            throw new Exception($"Get failed: {getRes.StatusCode} - {error}");
        }
        var json = await getRes.Content.ReadAsStringAsync();
        Console.WriteLine($"[CLIENT] Received JSON: {json}");
        var content = await getRes.Content.ReadFromJsonAsync<Content>();

        // 3. Verify Fields
        // Standard User should see BirthDay masked and NO SSN
        content!.Data.Should().ContainKey("BirthDay");
        content.Data["BirthDay"].ToString().Should().Be("***");
        content.Data.Should().NotContainKey("SSN");
    }

    [Fact]
    public async Task GetAttendance_ShouldReturnAllData_ForSuperAdmin()
    {
        // 1. Create Record as SuperAdmin
        var adminToken = JWTBearer.CreateToken(
            signingKey: "test-super-secret-key-that-is-at-least-32-chars-long",
            expireAt: DateTime.UtcNow.AddDays(1),
            privileges: u =>
            {
                u.Roles.Add("SuperAdmin");
                u.Roles.Add("Admin");
                u.Claims.Add(new("UserId", Guid.NewGuid().ToString()));
            });

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var data = new Dictionary<string, object>
        {
            { "FirstName", "Boss" },
            { "LastName", "Man" },
            { "BirthDay", "1980-01-01" },
            { "SSN", "111-22-3333" }
        };

        var createRes = await _client.PostAsJsonAsync("/api/contents", new barakoCMS.Features.Content.Create.Request
        {
            ContentType = "AttendanceRecord",
            Data = data,
            Status = ContentStatus.Published
        });
        createRes.EnsureSuccessStatusCode();
        var contentId = (await createRes.Content.ReadFromJsonAsync<barakoCMS.Features.Content.Create.Response>())!.Id;

        // 2. Fetch as SuperAdmin
        var getRes = await _client.GetAsync($"/api/contents/{contentId}");
        getRes.EnsureSuccessStatusCode();
        var content = await getRes.Content.ReadFromJsonAsync<Content>();

        // 3. Verify Fields (Should see everything)
        content!.Data.Should().ContainKey("SSN");
        content.Data["SSN"].ToString().Should().Be("111-22-3333");
        // Date might be ISO format
        content.Data["BirthDay"].ToString().Should().StartWith("1980-01-01");
    }

    [Fact]
    public async Task CreateAttendance_ShouldFail_ForAnonymousUser()
    {
        _client.DefaultRequestHeaders.Authorization = null;

        var req = new barakoCMS.Features.Content.Create.Request
        {
            ContentType = "AttendanceRecord",
            Data = new Dictionary<string, object>(),
            Status = ContentStatus.Published
        };

        var res = await _client.PostAsJsonAsync("/api/contents", req);
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
