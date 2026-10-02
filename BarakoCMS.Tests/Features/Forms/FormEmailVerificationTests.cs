using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using BarakoCMS.Forms;
using BarakoCMS.Tests.Features.Email;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BarakoCMS.Tests.Features.Forms;

/// <summary>
/// A form that verifies an email field: POST /api/public/forms/{slug}/email-code sends the code and
/// POST /api/public/forms/{slug} takes a submission only with it.
/// </summary>
/// <remarks>
/// Every request comes from its own client IP unless the test is about the per client limit, and
/// every test uses its own form and its own addresses, because the per address and per form limits
/// are stored and would otherwise be shared across the class.
/// </remarks>
[Collection("Sequential")]
public class FormEmailVerificationTests
{
    private const string CodeError = "emailVerificationCode";

    private readonly IntegrationTestFixture _factory;

    public FormEmailVerificationTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task A_submission_without_the_code_is_refused_and_one_with_it_is_accepted_and_audited()
    {
        var type = await CreateTypeAsync();
        await EnableAsync(type, "email");
        var address = Address();

        var bare = await SubmitAsync(type, address, code: null);
        bare.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the form verifies its email field");
        (await ErrorNamesAsync(bare)).Should().Equal(CodeError);
        (await EntriesAsync(type)).Should().BeEmpty();

        await RequestCodeAsync(type, address);
        var code = CodeFor(_factory.Email, address);

        var row = await RowAsync(address);
        row.Should().NotBeNull("a code was sent, so the address has a row");
        row!.CodeHash.Should().StartWith("$2", "the code is stored as a BCrypt hash");
        row.CodeHash.Should().NotContain(code);
        row.Form.Should().Be(type);

        var accepted = await SubmitAsync(type, address, code);
        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync());

        var entries = await EntriesAsync(type);
        entries.Should().HaveCount(1);
        entries[0].Data["email"].ToString().Should().Be(address);

        using var scope = _factory.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<AuditEvent>()
            .Where(e => e.Action == "form.email.verified" && e.TargetId == entries[0].Id.ToString())
            .ToListAsync();
        audit.Should().HaveCount(1, "the accepted entry is recorded as verified");
        audit[0].TargetType.Should().Be("Content");
        audit[0].ActorUserId.Should().BeNull();
        audit[0].IpAddress.Should().BeNull();
        audit[0].Metadata.Should().NotBeNull();
        audit[0].Metadata!.Keys.Should().BeEquivalentTo(["form", "field"], "neither the code nor the address is recorded");
        audit[0].Metadata!["form"].ToString().Should().Be(type);
        audit[0].Metadata!["field"].ToString().Should().Be("email");
    }

    [Fact]
    public async Task A_code_works_for_one_submission_only()
    {
        var type = await CreateTypeAsync();
        await EnableAsync(type, "email");
        var address = Address();
        await RequestCodeAsync(type, address);
        var code = CodeFor(_factory.Email, address);

        var first = await SubmitAsync(type, address, code);
        var second = await SubmitAsync(type, address, code);

        first.StatusCode.Should().Be(HttpStatusCode.Accepted, await first.Content.ReadAsStringAsync());
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the code was spent by the first submission");
        (await ErrorNamesAsync(second)).Should().Equal(CodeError);
        (await EntriesAsync(type)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Five_wrong_codes_kill_the_code_so_the_right_one_is_refused_too()
    {
        var type = await CreateTypeAsync();
        await EnableAsync(type, "email");
        var address = Address();
        await RequestCodeAsync(type, address);
        var code = CodeFor(_factory.Email, address);
        var wrong = code == "000000" ? "000001" : "000000";

        for (var i = 0; i < 5; i++)
        {
            var guess = await SubmitAsync(type, address, wrong);
            guess.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ErrorNamesAsync(guess)).Should().Equal(CodeError);
        }

        var late = await SubmitAsync(type, address, code);

        late.StatusCode.Should().Be(HttpStatusCode.BadRequest, "five wrong checks are all a code survives");
        (await EntriesAsync(type)).Should().BeEmpty();
        (await RowAsync(address))!.CodeHash.Should().BeNull("a dead code keeps no hash");
    }

    [Fact]
    public async Task A_wrong_code_within_the_limit_leaves_the_right_one_usable()
    {
        var type = await CreateTypeAsync();
        await EnableAsync(type, "email");
        var address = Address();
        await RequestCodeAsync(type, address);
        var code = CodeFor(_factory.Email, address);
        var wrong = code == "000000" ? "000001" : "000000";

        var guess = await SubmitAsync(type, address, wrong);
        var right = await SubmitAsync(type, address, code);

        guess.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        right.StatusCode.Should().Be(HttpStatusCode.Accepted, await right.Content.ReadAsStringAsync());
        (await EntriesAsync(type)).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_code_is_refused_for_another_address()
    {
        var type = await CreateTypeAsync();
        await EnableAsync(type, "email");
        var address = Address();
        var other = Address();
        await RequestCodeAsync(type, address);
        var code = CodeFor(_factory.Email, address);

        var borrowed = await SubmitAsync(type, other, code);
        borrowed.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the code was sent to a different address");
        (await EntriesAsync(type)).Should().BeEmpty();

        var own = await SubmitAsync(type, address, code);
        own.StatusCode.Should().Be(HttpStatusCode.Accepted, await own.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_code_is_refused_on_another_form()
    {
        var sentFor = await CreateTypeAsync();
        var otherForm = await CreateTypeAsync();
        await EnableAsync(sentFor, "email");
        await EnableAsync(otherForm, "email");
        var address = Address();
        await RequestCodeAsync(sentFor, address);
        var code = CodeFor(_factory.Email, address);

        var crossed = await SubmitAsync(otherForm, address, code);
        crossed.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the code was sent for a different form");
        (await EntriesAsync(otherForm)).Should().BeEmpty();

        var own = await SubmitAsync(sentFor, address, code);
        own.StatusCode.Should().Be(HttpStatusCode.Accepted, await own.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_code_is_refused_in_another_tenant()
    {
        var sentIn = await TenantAsync();
        var otherTenant = await TenantAsync();
        var type = $"form-{Guid.NewGuid():N}";
        await StoreVerifiedFormAsync(sentIn, type);
        await StoreVerifiedFormAsync(otherTenant, type);
        var address = Address();

        var sent = await Visitor(tenant: sentIn).PostAsJsonAsync($"/api/public/forms/{type}/email-code", new { email = address });
        sent.StatusCode.Should().Be(HttpStatusCode.Accepted, await sent.Content.ReadAsStringAsync());
        var code = CodeFor(_factory.Email, address);

        var crossed = await SubmitAsync(type, address, code, tenant: otherTenant);
        crossed.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the code was sent in a different tenant");
        (await ErrorNamesAsync(crossed)).Should().Equal(CodeError);
        (await EntryCountAsync(otherTenant, type)).Should().Be(0);

        var own = await SubmitAsync(type, address, code, tenant: sentIn);
        own.StatusCode.Should().Be(HttpStatusCode.Accepted, await own.Content.ReadAsStringAsync());
        (await EntryCountAsync(sentIn, type)).Should().Be(1);
    }

    [Fact]
    public async Task An_expired_code_is_refused()
    {
        var type = await CreateTypeAsync();
        await EnableAsync(type, "email");
        var address = Address();
        await RequestCodeAsync(type, address);
        var code = CodeFor(_factory.Email, address);

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var row = await session.LoadAsync<FormEmailVerification>(RowId(address));
            row.Should().NotBeNull();
            row!.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            session.Store(row);
            await session.SaveChangesAsync();
        }

        var late = await SubmitAsync(type, address, code);

        late.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorNamesAsync(late)).Should().Equal(CodeError);
        (await EntriesAsync(type)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_address_is_matched_without_case_or_surrounding_space()
    {
        var type = await CreateTypeAsync();
        await EnableAsync(type, "email");
        var address = Address();
        var asTyped = "  " + address.ToUpperInvariant() + " ";

        var sent = await Visitor().PostAsJsonAsync($"/api/public/forms/{type}/email-code", new { email = asTyped });
        sent.StatusCode.Should().Be(HttpStatusCode.Accepted, await sent.Content.ReadAsStringAsync());
        var code = CodeFor(_factory.Email, address);

        var accepted = await SubmitAsync(type, address.ToUpperInvariant(), code);

        accepted.StatusCode.Should().Be(HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync());
        (await EntriesAsync(type)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Code_requests_for_one_address_past_the_limit_are_429_and_send_nothing_more()
    {
        var type = await CreateTypeAsync();
        await EnableAsync(type, "email");
        var address = Address();

        for (var i = 0; i < 5; i++)
        {
            await RequestCodeAsync(type, address);
        }

        var refused = await Visitor().PostAsJsonAsync($"/api/public/forms/{type}/email-code", new { email = address });
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "five codes per address per hour is the default");
        _factory.Email.Messages.Count(m => m.To == address).Should().Be(5);

        var other = await Visitor().PostAsJsonAsync($"/api/public/forms/{type}/email-code", new { email = Address() });
        other.StatusCode.Should().Be(HttpStatusCode.Accepted, "the limit is on the address, and the form has budget left");
    }

    [Fact]
    public async Task Code_requests_past_the_form_limit_are_429_for_every_address()
    {
        var host = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:Forms:EmailVerification:CodesPerForm"] = "2",
            })));
        var type = await CreateTypeAsync();
        var otherForm = await CreateTypeAsync();
        await EnableAsync(type, "email");
        await EnableAsync(otherForm, "email");
        var third = Address();

        for (var i = 0; i < 2; i++)
        {
            var allowed = await Visitor(host).PostAsJsonAsync($"/api/public/forms/{type}/email-code", new { email = Address() });
            allowed.StatusCode.Should().Be(HttpStatusCode.Accepted, await allowed.Content.ReadAsStringAsync());
        }

        var refused = await Visitor(host).PostAsJsonAsync($"/api/public/forms/{type}/email-code", new { email = third });
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        host.Services.GetRequiredService<RecordingEmailService>().Messages.Count(m => m.To == third).Should().Be(0);

        var elsewhere = await Visitor(host).PostAsJsonAsync($"/api/public/forms/{otherForm}/email-code", new { email = third });
        elsewhere.StatusCode.Should().Be(HttpStatusCode.Accepted, "the limit is per form");
    }

    [Fact]
    public async Task Code_requests_from_one_client_past_the_limit_are_429()
    {
        var type = await CreateTypeAsync();
        await EnableAsync(type, "email");
        var client = Visitor();

        for (var i = 0; i < 5; i++)
        {
            var allowed = await client.PostAsJsonAsync($"/api/public/forms/{type}/email-code", new { email = Address() });
            allowed.StatusCode.Should().Be(HttpStatusCode.Accepted, await allowed.Content.ReadAsStringAsync());
        }

        var address = Address();
        var refused = await client.PostAsJsonAsync($"/api/public/forms/{type}/email-code", new { email = address });
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        _factory.Email.Messages.Count(m => m.To == address).Should().Be(0);

        var other = await Visitor().PostAsJsonAsync($"/api/public/forms/{type}/email-code", new { email = address });
        other.StatusCode.Should().Be(HttpStatusCode.Accepted, "the limit is per client");
    }

    [Fact]
    public async Task A_code_request_is_answered_the_same_when_the_provider_is_down()
    {
        var failing = new FailingEmailService();
        var broken = _factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailService>();
            services.AddSingleton<IEmailService>(failing);
        }));
        var type = await CreateTypeAsync();
        await EnableAsync(type, "email");

        var down = await Visitor(broken).PostAsJsonAsync($"/api/public/forms/{type}/email-code", new { email = Address() });
        var up = await Visitor().PostAsJsonAsync($"/api/public/forms/{type}/email-code", new { email = Address() });

        failing.Attempts.Should().Be(1, "the broken host did try to send");
        down.StatusCode.Should().Be(HttpStatusCode.Accepted);
        up.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await down.Content.ReadAsStringAsync()).Should().Be(await up.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_filled_honeypot_on_a_code_request_is_answered_like_a_success_and_sends_nothing()
    {
        var type = await CreateTypeAsync();
        await EnableAsync(type, "email");
        var address = Address();

        var bot = await Visitor().PostAsJsonAsync($"/api/public/forms/{type}/email-code",
            new { email = address, honeypot = "http://spam.example" });
        var real = await Visitor().PostAsJsonAsync($"/api/public/forms/{type}/email-code", new { email = Address() });

        bot.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await bot.Content.ReadAsStringAsync()).Should().Be(await real.Content.ReadAsStringAsync());
        _factory.Email.Messages.Count(m => m.To == address).Should().Be(0);
        (await RowAsync(address)).Should().BeNull();
    }

    [Fact]
    public async Task A_code_request_without_one_email_address_is_400_and_stores_nothing()
    {
        var type = await CreateTypeAsync();
        await EnableAsync(type, "email");

        foreach (var bad in new[] { "", "not-an-address", "ana@example.com\r\nBcc: other@example.com", "a b@example.com" })
        {
            var response = await Visitor().PostAsJsonAsync($"/api/public/forms/{type}/email-code", new { email = bad });

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "'{0}' is not one address", bad);
            (await RowAsync(bad)).Should().BeNull();
        }
    }

    /// <summary>
    /// A guard: it passes with or without the change, and says a form that did not opt in is left
    /// as it was.
    /// </summary>
    [Fact]
    public async Task A_form_that_does_not_verify_sends_no_code_and_takes_a_submission_without_one()
    {
        var type = await CreateTypeAsync();
        await EnableAsync(type, verifyEmailField: null);
        var address = Address();

        var request = await Visitor().PostAsJsonAsync($"/api/public/forms/{type}/email-code", new { email = address });
        var submitted = await SubmitAsync(type, address, code: null);

        request.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _factory.Email.Messages.Count(m => m.To == address).Should().Be(0);
        submitted.StatusCode.Should().Be(HttpStatusCode.Accepted, await submitted.Content.ReadAsStringAsync());
        (await EntriesAsync(type)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Verification_is_turned_on_only_for_an_email_field_a_visitor_can_fill_in()
    {
        var type = await CreateTypeAsync();
        var admin = await AdminAsync();

        foreach (var notUsable in new[] { "name", "backupEmail", "no-such-field" })
        {
            var refused = await admin.PutAsJsonAsync($"/api/forms/{type}", new { enabled = true, verifyEmailField = notUsable });
            refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, "'{0}' is not a submittable email field", notUsable);
            (await ErrorNamesAsync(refused)).Should().Equal("verifyEmailField");
        }

        var on = await admin.PutAsJsonAsync($"/api/forms/{type}", new { enabled = true, verifyEmailField = "EMAIL" });
        on.StatusCode.Should().Be(HttpStatusCode.OK, await on.Content.ReadAsStringAsync());
        (await VerifyFieldAsync(on)).Should().Be("email", "the stored name is the type's own spelling");

        var definition = await Visitor().GetAsync($"/api/public/forms/{type}");
        (await VerifyFieldAsync(definition)).Should().Be("email");
        (await ListedVerifyFieldAsync(admin, type)).Should().Be("email");

        var kept = await admin.PutAsJsonAsync($"/api/forms/{type}", new { enabled = true });
        (await VerifyFieldAsync(kept)).Should().Be("email", "a request that leaves the field out keeps what the form has");

        var off = await admin.PutAsJsonAsync($"/api/forms/{type}", new { enabled = true, verifyEmailField = "" });
        (await VerifyFieldAsync(off)).Should().BeNull("an empty string turns verification off");
        (await SubmitAsync(type, Address(), code: null)).StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task The_definition_marks_the_verified_field_required_even_when_the_type_does_not()
    {
        var type = await CreateTypeAsync(emailRequired: false);
        await EnableAsync(type, "email");

        var response = await Visitor().GetAsync($"/api/public/forms/{type}");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var email = body.RootElement.GetProperty("fields").EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "email");
        email.GetProperty("required").GetBoolean().Should().BeTrue();

        var blank = await Visitor().PostAsJsonAsync($"/api/public/forms/{type}",
            new { data = new { name = "Ana" }, emailVerificationCode = "123456" });
        blank.StatusCode.Should().Be(HttpStatusCode.BadRequest, "there is no address to have verified");
        (await ErrorNamesAsync(blank)).Should().Equal("data.email");
        (await EntriesAsync(type)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_form_whose_verified_field_stops_being_an_email_field_refuses_every_submission()
    {
        var type = await CreateTypeAsync();
        await EnableAsync(type, "email");
        var address = Address();
        await RequestCodeAsync(type, address);
        var code = CodeFor(_factory.Email, address);

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var definition = await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type);
            definition.Fields.Single(f => f.Name == "email").Type = "string";
            session.Store(definition);
            await session.SaveChangesAsync();
        }

        var submitted = await SubmitAsync(type, address, code);
        var request = await Visitor().PostAsJsonAsync($"/api/public/forms/{type}/email-code", new { email = Address() });

        submitted.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the owner asked for verified addresses and none can be verified now");
        (await ErrorNamesAsync(submitted)).Should().Equal(CodeError);
        (await EntriesAsync(type)).Should().BeEmpty();
        request.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_code_request_removes_rows_whose_code_and_window_have_both_passed()
    {
        var stale = $"stale-{Guid.NewGuid():N}";
        var recent = $"recent-{Guid.NewGuid():N}";
        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var now = DateTimeOffset.UtcNow;
            session.Store(new FormEmailVerification { Id = stale, Form = "gone", LastSentAt = now.AddHours(-3), WindowStartedAt = now.AddHours(-3) });
            session.Store(new FormEmailVerification { Id = recent, Form = "live", LastSentAt = now.AddMinutes(-5), WindowStartedAt = now.AddMinutes(-5) });
            await session.SaveChangesAsync();
        }

        var type = await CreateTypeAsync();
        await EnableAsync(type, "email");
        await RequestCodeAsync(type, Address());

        using var read = _factory.Services.CreateScope();
        var query = read.ServiceProvider.GetRequiredService<IQuerySession>();
        (await query.LoadAsync<FormEmailVerification>(recent)).Should().NotBeNull("its window has not passed");
        (await query.LoadAsync<FormEmailVerification>(stale)).Should().BeNull("its code and its window passed hours ago");
    }

    [Fact]
    public async Task Turning_a_form_off_removes_its_code_count()
    {
        var type = await CreateTypeAsync();
        await EnableAsync(type, "email");
        await RequestCodeAsync(type, Address());

        var counted = await BudgetAsync(type);
        counted.Should().NotBeNull("a code was sent for the form");
        counted!.Sent.Should().Be(1);

        var off = await (await AdminAsync()).PutAsJsonAsync($"/api/forms/{type}", new { enabled = false });
        off.StatusCode.Should().Be(HttpStatusCode.OK, await off.Content.ReadAsStringAsync());

        (await BudgetAsync(type)).Should().BeNull();
    }

    [Fact]
    public async Task With_turnstile_on_a_code_request_needs_a_token_cloudflare_accepts()
    {
        var host = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:Forms:Turnstile:Enabled"] = "true",
                ["Modules:Forms:Turnstile:SecretKey"] = "test-turnstile-secret",
            }));
            b.ConfigureServices(services => services
                .AddHttpClient<ITurnstileVerifier, TurnstileVerifier>()
                .ConfigurePrimaryHttpMessageHandler(() => new TurnstileStub()));
        });
        var emails = host.Services.GetRequiredService<RecordingEmailService>();
        var type = await CreateTypeAsync();
        await EnableAsync(type, "email");
        var refusedAddress = Address();
        var passedAddress = Address();

        var missing = await Visitor(host).PostAsJsonAsync($"/api/public/forms/{type}/email-code", new { email = refusedAddress });
        var rejected = await Visitor(host).PostAsJsonAsync($"/api/public/forms/{type}/email-code",
            new { email = refusedAddress, turnstileToken = "bad" });
        var passed = await Visitor(host).PostAsJsonAsync($"/api/public/forms/{type}/email-code",
            new { email = passedAddress, turnstileToken = "good" });

        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorNamesAsync(missing)).Should().Equal("turnstileToken");
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        passed.StatusCode.Should().Be(HttpStatusCode.Accepted, await passed.Content.ReadAsStringAsync());
        emails.Messages.Count(m => m.To == refusedAddress).Should().Be(0);
        emails.Messages.Count(m => m.To == passedAddress).Should().Be(1);
    }

    /// <summary>Answers success only for the token "good" sent with the configured secret.</summary>
    private sealed class TurnstileStub : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var form = await request.Content!.ReadAsStringAsync(ct);
            var ok = form.Contains("secret=test-turnstile-secret") && form.Contains("response=good");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"success\":{(ok ? "true" : "false")}}}", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private static string Address() => $"ana-{Guid.NewGuid():N}@example.com";

    private static string RowId(string address) => FormEmailVerifier.AddressId(FormEmailVerifier.Normalise(address));

    private static string CodeFor(RecordingEmailService email, string address)
    {
        var message = email.Messages.LastOrDefault(m => m.To == address);
        message.Should().NotBeNull("a code was requested for {0}", address);
        var match = Regex.Match(message!.Body, @">(\d{6})<");
        match.Success.Should().BeTrue("the message carries a six digit code");
        return match.Groups[1].Value;
    }

    private HttpClient Visitor(WebApplicationFactory<Program>? host = null, string? tenant = null)
    {
        var client = (host ?? _factory).CreateClient();
        var bytes = Guid.NewGuid().ToByteArray();
        var ip = $"2001:db8::{bytes[0]:x2}{bytes[1]:x2}:{bytes[2]:x2}{bytes[3]:x2}:{bytes[4]:x2}{bytes[5]:x2}";
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ip);
        if (tenant is not null)
        {
            client.DefaultRequestHeaders.Add("X-Tenant", tenant);
        }
        return client;
    }

    private async Task RequestCodeAsync(string type, string address)
    {
        var response = await Visitor().PostAsJsonAsync($"/api/public/forms/{type}/email-code", new { email = address });
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
    }

    private Task<HttpResponseMessage> SubmitAsync(string type, string address, string? code, string? tenant = null) =>
        Visitor(tenant: tenant).PostAsJsonAsync($"/api/public/forms/{type}", new
        {
            data = new { name = "Ana", email = address },
            emailVerificationCode = code,
        });

    private async Task<HttpClient> AdminAsync()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("Admin"));
        return client;
    }

    private async Task EnableAsync(string type, string? verifyEmailField)
    {
        var response = await (await AdminAsync()).PutAsJsonAsync($"/api/forms/{type}", new { enabled = true, verifyEmailField });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static ContentTypeDefinition Definition(string name, bool emailRequired = true) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        DisplayName = "Race sign-up",
        Fields =
        [
            new FieldDefinition { Name = "name", DisplayName = "Name", Type = "string", IsRequired = true },
            new FieldDefinition { Name = "email", DisplayName = "Email", Type = "email", IsRequired = emailRequired },
            new FieldDefinition
            {
                Name = "backupEmail", DisplayName = "Backup email", Type = "email",
                Sensitivity = SensitivityLevel.Sensitive,
            },
        ],
    };

    private async Task<string> CreateTypeAsync(bool emailRequired = true)
    {
        var name = $"form-{Guid.NewGuid():N}";

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(Definition(name, emailRequired));
        await session.SaveChangesAsync();
        return name;
    }

    private async Task<string> TenantAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"club-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, IsActive = true });
        await session.SaveChangesAsync();
        return slug;
    }

    /// <summary>Stores the type and its form row straight into a tenant's partition.</summary>
    private async Task StoreVerifiedFormAsync(string tenant, string type)
    {
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(tenant);
        session.Store(Definition(type));
        session.Store(new PublicForm { ContentType = type, EnabledAt = DateTimeOffset.UtcNow, VerifyEmailField = "email" });
        await session.SaveChangesAsync();
    }

    private async Task<int> EntryCountAsync(string tenant, string type)
    {
        var store = _factory.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(tenant);
        return await session.Query<Content>().CountAsync(c => c.ContentType == type);
    }

    private async Task<FormEmailVerification?> RowAsync(string address)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .LoadAsync<FormEmailVerification>(RowId(address));
    }

    private async Task<FormEmailBudget?> BudgetAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<FormEmailBudget>(type);
    }

    private async Task<IReadOnlyList<Content>> EntriesAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<Content>().Where(c => c.ContentType == type).ToListAsync();
    }

    private static async Task<string?> VerifyFieldAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("verifyEmailField", out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString()
            : null;
    }

    /// <summary>The form's row in GET /api/forms, read a page at a time since other tests leave forms behind.</summary>
    private static async Task<string?> ListedVerifyFieldAsync(HttpClient admin, string type)
    {
        for (var page = 1; page <= 200; page++)
        {
            using var body = JsonDocument.Parse(await admin.GetStringAsync($"/api/forms?page={page}&pageSize=100"));
            var items = body.RootElement.GetProperty("items").EnumerateArray().ToList();
            if (items.Count == 0)
            {
                break;
            }

            foreach (var item in items.Where(i => i.GetProperty("contentType").GetString() == type))
            {
                return item.TryGetProperty("verifyEmailField", out var field) ? field.GetString() : null;
            }
        }

        throw new Xunit.Sdk.XunitException($"GET /api/forms never listed {type}");
    }

    private static async Task<List<string>> ErrorNamesAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("errors").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!)
            .ToList();
    }
}
