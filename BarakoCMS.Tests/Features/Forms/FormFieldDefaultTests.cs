using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.Forms;

/// <summary>
/// A field's DefaultValue fills a key a form submission cannot carry, and a required field that has
/// one no longer makes the type unusable as a form.
/// </summary>
[Collection("Integration1")]
public class FormFieldDefaultTests
{
    private readonly IntegrationTestFixture _factory;

    public FormFieldDefaultTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Visitor()
    {
        var client = _factory.CreateClient();
        var bytes = Guid.NewGuid().ToByteArray();
        var ip = $"2001:db8:{bytes[0]:x2}{bytes[1]:x2}:{bytes[2]:x2}{bytes[3]:x2}:{bytes[4]:x2}{bytes[5]:x2}::2";
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, ip);
        return client;
    }

    /// <summary>A form with a name, and a required field of a kind no visitor can fill in, with a default.</summary>
    private async Task<string> CreateFormAsync(bool withDefault)
    {
        var name = $"form-{Guid.NewGuid():N}";

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = name,
                DisplayName = "Signup",
                Fields =
                [
                    new FieldDefinition { Name = "name", DisplayName = "Name", Type = "string", IsRequired = true },
                    new FieldDefinition
                    {
                        Name = "source",
                        DisplayName = "Source",
                        Type = "string",
                        IsRequired = true,
                        Sensitivity = SensitivityLevel.Sensitive,
                        DefaultValue = withDefault ? "web" : null,
                    },
                ],
            });
            await session.SaveChangesAsync(Ct);
        }

        return name;
    }

    private async Task<HttpResponseMessage> EnableAsync(string name)
    {
        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync("Admin"));
        return await admin.PutAsJsonAsync($"/api/forms/{name}", new { enabled = true }, Ct);
    }

    [Fact]
    public async Task A_required_field_a_visitor_cannot_fill_in_is_accepted_when_it_has_a_default()
    {
        var name = await CreateFormAsync(withDefault: true);

        var enabled = await EnableAsync(name);

        enabled.StatusCode.Should().Be(HttpStatusCode.OK, await enabled.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_required_field_a_visitor_cannot_fill_in_is_still_refused_without_a_default()
    {
        var name = await CreateFormAsync(withDefault: false);

        (await EnableAsync(name)).StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_submission_stores_the_default_of_a_field_the_form_does_not_show()
    {
        var name = await CreateFormAsync(withDefault: true);
        (await EnableAsync(name)).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await Visitor().PostAsJsonAsync($"/api/public/forms/{name}", new { data = new { name = "Ana" } }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        using var scope = _factory.Services.CreateScope();
        var entry = (await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<Content>().Where(c => c.ContentType == name).ToListAsync(Ct)).Single();
        entry.Data["source"].ToString().Should().Be("web");
    }
}
