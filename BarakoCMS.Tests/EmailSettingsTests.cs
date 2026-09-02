using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// Email credentials set in the admin: encrypted at rest, never handed back, and beating what the
/// deployment configured.
/// </summary>
/// <remarks>
/// A process owner cannot edit appsettings, so email being a deployment-time decision is the step
/// that stops them standing this up on their own.
///
/// Every assertion about the key not being exposed is checked against the raw document or the raw
/// response body rather than through the service that decrypts it. Reading it back through the
/// provider would pass just as well against a document holding the plaintext, which is the failure
/// being ruled out.
/// </remarks>
[Collection("Sequential")]
public class EmailSettingsTests
{
    private const string Key = "re_a_key_nobody_should_ever_see_again";

    private readonly IntegrationTestFixture _factory;

    public EmailSettingsTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task The_stored_key_is_encrypted_in_the_document()
    {
        var client = await SuperAdminAsync();

        (await Put(client, new { apiKey = Key })).IsSuccessStatusCode.Should().BeTrue();

        var raw = await RawDocumentJsonAsync();

        raw.Should().NotBeNull("the setting was saved, so there is a document to read");
        raw.Should().NotContain(Key,
            "the whole point is that a database dump does not hand over a working credential");
        raw.Should().Contain("ProtectedApiKey", "and the ciphertext is what is stored in its place");
    }

    /// <summary>
    /// The control for the test above. Without it, a save that silently stored nothing would pass it.
    /// </summary>
    [Fact]
    public async Task The_stored_key_is_what_comes_back_out_of_the_provider()
    {
        var client = await SuperAdminAsync();

        (await Put(client, new { apiKey = Key })).IsSuccessStatusCode.Should().BeTrue();

        using var scope = _factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IEmailSettingsProvider>();
        var resolved = await provider.GetAsync(TestContext.Current.CancellationToken);

        resolved.ApiKey.Should().Be(Key, "encrypting it is only useful if it decrypts again");
        resolved.ApiKeySource.Should().Be(EmailSettingSource.Stored);
    }

    [Fact]
    public async Task The_settings_endpoint_never_returns_the_key()
    {
        var client = await SuperAdminAsync();
        await Put(client, new { apiKey = Key });

        var body = await (await client.GetAsync("/api/settings/email", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.Should().NotContain(Key, "an admin screen that repopulates the box puts the secret in every cache");
        body.Should().Contain("apiKeySet", "it says whether one is set, which is what the screen needs");
        body.Should().Contain("true");
    }

    /// <summary>
    /// The same for the response to the write, which is the one that has the plaintext in hand.
    /// </summary>
    [Fact]
    public async Task The_update_response_never_returns_the_key_either()
    {
        var client = await SuperAdminAsync();

        var body = await (await Put(client, new { apiKey = Key }))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.Should().NotContain(Key, "the endpoint that was handed the secret is the easiest one to echo it from");
    }

    /// <summary>
    /// Stored beats configuration, and its pair: configuration still works when nothing is stored.
    /// </summary>
    /// <remarks>
    /// The pair matters more than the winner. Asserting only that stored wins passes against an
    /// implementation that ignores configuration entirely, which would break every deployment that
    /// seeds its key from an environment variable and has no database row yet.
    /// </remarks>
    [Fact]
    public async Task Stored_beats_configuration_and_configuration_still_works_alone()
    {
        await ClearStoredAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var provider = scope.ServiceProvider.GetRequiredService<IEmailSettingsProvider>();
            var fromConfig = await provider.GetAsync(TestContext.Current.CancellationToken);

            fromConfig.ApiKey.Should().Be(IntegrationTestFixture.ConfiguredResendKey,
                "nothing is stored, so the deployment's own value is what a send would use");
            fromConfig.ApiKeySource.Should().Be(EmailSettingSource.Configuration);
        }

        var client = await SuperAdminAsync();
        (await Put(client, new { apiKey = Key })).IsSuccessStatusCode.Should().BeTrue();

        using (var scope = _factory.Services.CreateScope())
        {
            var provider = scope.ServiceProvider.GetRequiredService<IEmailSettingsProvider>();
            var stored = await provider.GetAsync(TestContext.Current.CancellationToken);

            stored.ApiKey.Should().Be(Key,
                "the stored one is the one a person set most recently, and through the surface this exists to provide");
            stored.ApiKeySource.Should().Be(EmailSettingSource.Stored);
        }
    }

    /// <summary>
    /// Precedence is per field, so filling in one box does not switch the other off.
    /// </summary>
    [Fact]
    public async Task A_stored_from_address_does_not_take_the_configured_key_out_of_use()
    {
        await ClearStoredAsync();

        var client = await SuperAdminAsync();
        (await Put(client, new { fromAddress = "invoices@example.com" })).IsSuccessStatusCode.Should().BeTrue();

        using var scope = _factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IEmailSettingsProvider>();
        var resolved = await provider.GetAsync(TestContext.Current.CancellationToken);

        resolved.FromAddress.Should().Be("invoices@example.com");
        resolved.FromAddressSource.Should().Be(EmailSettingSource.Stored);
        resolved.ApiKey.Should().Be(IntegrationTestFixture.ConfiguredResendKey,
            "an all or nothing cliff would stop email working the moment somebody set a From address");
        resolved.ApiKeySource.Should().Be(EmailSettingSource.Configuration);
    }

    /// <summary>
    /// Editing the From address does not wipe the key.
    /// </summary>
    /// <remarks>
    /// The screen cannot show the current key, so it has no way to send it back unchanged. An absent
    /// field meaning "clear it" would delete the credential every time somebody corrected a typo in
    /// the address, and they would not find out until the next invoice.
    /// </remarks>
    [Fact]
    public async Task Changing_only_the_from_address_leaves_the_key_alone()
    {
        var client = await SuperAdminAsync();
        (await Put(client, new { apiKey = Key })).IsSuccessStatusCode.Should().BeTrue();

        (await Put(client, new { fromAddress = "billing@example.com" })).IsSuccessStatusCode.Should().BeTrue();

        using var scope = _factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IEmailSettingsProvider>();
        var resolved = await provider.GetAsync(TestContext.Current.CancellationToken);

        resolved.ApiKey.Should().Be(Key, "an omitted field is not an instruction to delete a credential");
        resolved.FromAddress.Should().Be("billing@example.com");
    }

    /// <summary>An empty key, unlike an absent one, clears the stored value.</summary>
    [Fact]
    public async Task An_empty_key_clears_the_stored_one_and_configuration_takes_over()
    {
        var client = await SuperAdminAsync();
        (await Put(client, new { apiKey = Key })).IsSuccessStatusCode.Should().BeTrue();

        (await Put(client, new { apiKey = "" })).IsSuccessStatusCode.Should().BeTrue();

        using var scope = _factory.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IEmailSettingsProvider>();
        var resolved = await provider.GetAsync(TestContext.Current.CancellationToken);

        resolved.ApiKey.Should().Be(IntegrationTestFixture.ConfiguredResendKey);
        resolved.ApiKeySource.Should().Be(EmailSettingSource.Configuration);
    }

    [Fact]
    public async Task The_change_is_audited_without_the_value()
    {
        var client = await SuperAdminAsync();
        (await Put(client, new { apiKey = Key })).IsSuccessStatusCode.Should().BeTrue();

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var entries = await session.Query<AuditEvent>()
            .Where(e => e.Action == "settings.email.changed")
            .ToListAsync(TestContext.Current.CancellationToken);

        entries.Should().NotBeEmpty("changing where the system's email comes from is a takeover, not a tweak");

        var serialised = System.Text.Json.JsonSerializer.Serialize(entries);
        serialised.Should().NotContain(Key,
            "an audit entry that quotes the key puts it in the one table designed never to be deleted from");
    }

    /// <summary>
    /// A credential cannot be put in the generic settings store, where it would sit in plaintext.
    /// </summary>
    [Fact]
    public async Task The_generic_settings_endpoint_refuses_a_credential_shaped_key()
    {
        var client = await SuperAdminAsync();

        var res = await client.PostAsJsonAsync("/api/settings",
            new { key = "Resend:ApiKey", value = Key }, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "everything stored there is plaintext and GET /api/settings hands all of it back");

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var stored = await session.Query<SystemSetting>()
            .Where(s => s.Key == "Resend:ApiKey")
            .ToListAsync(TestContext.Current.CancellationToken);

        stored.Should().BeEmpty("refused has to mean not written, not written and complained about");
    }

    /// <summary>The control: an ordinary setting still saves.</summary>
    [Fact]
    public async Task The_generic_settings_endpoint_still_takes_an_ordinary_key()
    {
        var client = await SuperAdminAsync();

        var res = await client.PostAsJsonAsync("/api/settings",
            new { key = "Serilog__WriteToFile", value = "true" }, TestContext.Current.CancellationToken);

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}",
            res.StatusCode, await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A test send goes to the caller's own address and nowhere else.
    /// </summary>
    /// <remarks>
    /// The recipient is not a parameter on purpose. An endpoint that takes one is a way to send mail
    /// from this deployment's domain to any address somebody names, and the person who needs to see
    /// the test is the one who just typed the credentials in.
    /// </remarks>
    [Fact]
    public async Task A_test_send_goes_to_the_caller_and_nowhere_else()
    {
        var (client, email) = await SuperAdminWithEmailAsync();
        await Put(client, new { apiKey = Key });

        var before = _factory.Email.Messages.Count;
        var res = await client.PostAsync("/api/settings/email/test", null, TestContext.Current.CancellationToken);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        res.IsSuccessStatusCode.Should().BeTrue("got {0}: {1}", res.StatusCode, body);

        var sent = _factory.Email.Messages.Skip(before).ToList();
        sent.Should().ContainSingle("a configuration screen that cannot tell you whether it worked "
          + "moves the failure to the first real invoice");
        sent[0].To.Should().Be(email.ToLowerInvariant());
    }

    /// <summary>
    /// The test send refuses when nothing would be delivered.
    /// </summary>
    /// <remarks>
    /// The mock provider logs and returns, so a test button in front of it answers "sent" every time.
    /// A configuration screen that cannot fail is worse than no button at all: it moves the failure
    /// to the first real invoice and tells the operator it already worked.
    ///
    /// Its own host, because the shared fixture registers a recording transport that does deliver as
    /// far as the endpoint can tell. Asserting this against that host would assert nothing.
    /// </remarks>
    [Fact]
    public async Task The_test_send_refuses_when_no_provider_would_deliver_it()
    {
        var host = _factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<IEmailService>();
            services.AddSingleton<IEmailService, barakoCMS.Infrastructure.Services.MockEmailService>();
        }));

        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var res = await client.PostAsync("/api/settings/email/test", null, TestContext.Current.CancellationToken);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "got {0}: {1}", res.StatusCode, body);
        body.Should().Contain("provider", "and it says why, rather than reporting a send that went nowhere");
    }

    private async Task<HttpClient> SuperAdminAsync() => (await SuperAdminWithEmailAsync()).Client;

    private async Task<(HttpClient Client, string Email)> SuperAdminWithEmailAsync()
    {
        var (token, userId) = await TestHelpers.CreateAdminUserAsync(_factory);

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var user = await session.LoadAsync<User>(userId, TestContext.Current.CancellationToken);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, user!.Email);
    }

    private static Task<HttpResponseMessage> Put(HttpClient client, object body) =>
        client.PutAsJsonAsync("/api/settings/email", body, TestContext.Current.CancellationToken);

    private async Task ClearStoredAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Delete<EmailSettings>(EmailSettings.SingletonId);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The document as Postgres holds it, not as Marten hands it back. Reading it through the
    /// provider would pass against a row storing the plaintext.
    /// </summary>
    private async Task<string?> RawDocumentJsonAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        await using var conn = store.Storage.Database.CreateConnection();
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select data::text from public.mt_doc_email_settings limit 1";
        return (await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken)) as string;
    }
}
