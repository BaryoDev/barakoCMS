using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using barakoCMS.Models;
using BarakoCMS.Payments;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Payments;

/// <summary>What the checkout and webhook tests share: a payable type, an entry, and signed bodies.</summary>
internal sealed class PaymentHarness(IntegrationTestFixture factory)
{
    public const double Fee = 1500.5;

    public FakePaymentProvider Fake =>
        (FakePaymentProvider)factory.Services.GetServices<IPaymentProvider>().Single(p => p.Name == "fake");

    public HttpClient Visitor()
    {
        var client = factory.CreateClient();
        var bytes = Guid.NewGuid().ToByteArray();
        client.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header,
            $"2001:db8:{bytes[0]:x2}{bytes[1]:x2}:{bytes[2]:x2}{bytes[3]:x2}:{bytes[4]:x2}{bytes[5]:x2}::1");
        return client;
    }

    public async Task<HttpClient> AdminAsync()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>A type with a Pending, Paid, Failed lifecycle and a fee field, declared payable unless told not to.</summary>
    public async Task<string> PayableTypeAsync(bool declare = true)
    {
        var name = $"registration-{Guid.NewGuid():N}";

        using (var scope = factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = name,
                DisplayName = "Registration",
                Fields =
                [
                    new FieldDefinition { Name = "name", DisplayName = "Name", Type = "string" },
                    new FieldDefinition { Name = "fee", DisplayName = "Fee", Type = "money" },
                ],
                Lifecycle = new LifecycleDefinition
                {
                    States = ["Pending", "Paid", "Failed"],
                    InitialState = "Pending",
                    Transitions =
                    [
                        new StateTransition { Name = "MarkPaid", From = "Pending", To = "Paid" },
                        new StateTransition { Name = "MarkFailed", From = "Pending", To = "Failed" },
                    ],
                },
            });
            await session.SaveChangesAsync();
        }

        if (declare)
        {
            var response = await (await AdminAsync()).PutAsJsonAsync($"/api/payments/payable/{name}", new
            {
                enabled = true,
                amountField = "fee",
                currency = "php",
                description = "Registration fee",
                paidTransition = "MarkPaid",
                failedTransition = "MarkFailed",
            });
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }

        return name;
    }

    public async Task<Guid> EntryAsync(string type)
    {
        var response = await (await AdminAsync()).PostAsJsonAsync("/api/contents", new
        {
            contentType = type,
            data = new Dictionary<string, object> { ["name"] = "Ana", ["fee"] = Fee },
        });
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    public async Task<(Guid AttemptId, string Reference)> CheckoutAsync(Guid entryId)
    {
        var response = await Visitor().PostAsJsonAsync("/api/public/payments/checkout", new { entryId, provider = "fake" });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var attemptId = doc.RootElement.GetProperty("attemptId").GetGuid();
        return (attemptId, (await AttemptAsync(attemptId)).Reference);
    }

    public static string Body(string eventId, string reference, string type) =>
        JsonSerializer.Serialize(new { id = eventId, reference, type });

    public Task<HttpResponseMessage> SignedWebhookAsync(string eventId, string reference, string type, decimal amount, string currency)
    {
        var body = JsonSerializer.Serialize(new { id = eventId, reference, type, amount, currency });
        return WebhookAsync(body, FakePaymentProvider.Sign(body));
    }

    public Task<HttpResponseMessage> WebhookAsync(string body, string? signature, string provider = "fake")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/payments/webhooks/{provider}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (signature is not null)
        {
            request.Headers.Add(FakePaymentProvider.SignatureHeader, signature);
        }

        return Visitor().SendAsync(request);
    }

    public Task<HttpResponseMessage> SignedWebhookAsync(string eventId, string reference, string type)
    {
        var body = Body(eventId, reference, type);
        return WebhookAsync(body, FakePaymentProvider.Sign(body));
    }

    public async Task<PaymentAttempt> AttemptAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var attempt = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<PaymentAttempt>(id);
        attempt.Should().NotBeNull();
        return attempt!;
    }

    public async Task<IReadOnlyList<PaymentAttempt>> AttemptsForAsync(Guid entryId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<PaymentAttempt>().Where(a => a.EntryId == entryId).ToListAsync();
    }

    public async Task<IReadOnlyList<PaymentEventRecord>> EventRecordsAsync(Guid attemptId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .Query<PaymentEventRecord>().Where(r => r.AttemptId == attemptId).ToListAsync();
    }

    public async Task<string?> StateAsync(Guid entryId)
    {
        using var scope = factory.Services.CreateScope();
        var entry = await scope.ServiceProvider.GetRequiredService<IQuerySession>().LoadAsync<Content>(entryId);
        entry.Should().NotBeNull();
        return entry!.LifecycleState;
    }

    public async Task ClearStateAsync(Guid entryId)
    {
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var entry = await session.LoadAsync<Content>(entryId);
        entry.Should().NotBeNull();
        entry!.LifecycleState = null;
        session.Store(entry);
        await session.SaveChangesAsync();
    }

    public async Task<string> TypeWithRequiredFieldOnPaidAsync()
    {
        var name = $"registration-{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = "Registration",
            Fields =
            [
                new FieldDefinition { Name = "fee", DisplayName = "Fee", Type = "money" },
                new FieldDefinition { Name = "receipt", DisplayName = "Receipt", Type = "string" },
            ],
            Lifecycle = new LifecycleDefinition
            {
                States = ["Pending", "Paid"],
                InitialState = "Pending",
                Transitions =
                [
                    new StateTransition { Name = "MarkPaid", From = "Pending", To = "Paid", RequiredFields = ["receipt"] },
                ],
            },
        });
        await session.SaveChangesAsync();
        return name;
    }
}
