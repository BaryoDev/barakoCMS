using System.Net;
using System.Net.Sockets;
using System.Text;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Http;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// What a webhook delivery actually puts on the wire: the content type's Public fields only, and
/// never to an address the guard blocks.
/// </summary>
/// <remarks>
/// The action is driven against a real listener and the assertions are on the received body and on
/// whether a socket was opened at all, rather than on the payload object the action builds. A
/// projection can be correct in the object and wrong by the time it is serialised.
/// </remarks>
[Collection("Sequential")]
public class WebhookPayloadTests
{
    private const string Ssn = "123-45-6789";
    private const string BirthDay = "1990-05-15";

    private readonly IntegrationTestFixture _fixture;

    public WebhookPayloadTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_webhook_payload_carries_the_public_fields_and_not_the_sensitive_ones()
    {
        const string tenant = "webhook-payload-fields";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentType = await SeedTypeAsync(store, tenant);
        var content = Record(contentType, SensitivityLevel.Public);

        using var listener = new RecordingListener();
        await SendAsync(store, tenant, content, listener.Url, PermitsLoopback);

        listener.WasCalled.Should().BeTrue("a permitted target still receives the delivery");
        listener.LastBody.Should().NotBeNull();
        listener.LastBody.Should().Contain("Sarah", "Name is a Public field, so the webhook is still useful");
        listener.LastBody.Should().NotContain(Ssn, "SSN is Hidden on the content type and a read would remove it");
        listener.LastBody.Should().NotContain(BirthDay, "BirthDay is Sensitive and there is no role behind a workflow");
    }

    [Fact]
    public async Task A_webhook_payload_for_a_sensitive_document_carries_no_data_at_all()
    {
        const string tenant = "webhook-payload-document";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentType = await SeedTypeAsync(store, tenant);
        var content = Record(contentType, SensitivityLevel.Sensitive);

        using var listener = new RecordingListener();
        await SendAsync(store, tenant, content, listener.Url, PermitsLoopback);

        listener.WasCalled.Should().BeTrue();
        listener.LastBody.Should().Contain(content.Id.ToString(), "the notification itself is still delivered");
        listener.LastBody.Should().NotContain("Sarah", "a read clears the data of a Sensitive document");
        listener.LastBody.Should().NotContain(Ssn);
    }

    /// <summary>
    /// A name that answers with a public address for the pre-flight check and a blocked one for the
    /// connection never reaches the blocked address.
    /// </summary>
    /// <remarks>
    /// The sink is a bare TCP listener rather than an HTTP one: what is being asserted is whether a
    /// socket was opened to that address, and a raw accept records that without any dependence on the
    /// Host header matching a prefix.
    ///
    /// The paired case below flips only the address policy, so it proves the same flipping resolver
    /// does deliver when the address is permitted, and that this test is not passing because the
    /// action refuses everything.
    /// </remarks>
    [Fact]
    public async Task A_dns_answer_that_changes_after_the_check_does_not_move_the_connection()
    {
        const string tenant = "webhook-rebinding-blocked";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentType = await SeedTypeAsync(store, tenant);

        using var sink = new AcceptingListener();
        var guard = new OutboundAddressGuard(resolve: RebindingResolver(sink.Port));

        await SendAsync(store, tenant, Record(contentType, SensitivityLevel.Public), RebindUrl(sink.Port), guard);

        sink.WasConnected.Should().BeFalse(
            "the address dialled is the address the connect callback checked, not the one the pre-flight saw");
    }

    [Fact]
    public async Task The_same_changing_answer_is_delivered_when_the_address_is_permitted()
    {
        const string tenant = "webhook-rebinding-permitted";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentType = await SeedTypeAsync(store, tenant);

        using var sink = new AcceptingListener();
        var guard = new OutboundAddressGuard(resolve: RebindingResolver(sink.Port), isBlocked: _ => false);

        await SendAsync(store, tenant, Record(contentType, SensitivityLevel.Public), RebindUrl(sink.Port), guard);

        sink.WasConnected.Should().BeTrue("nothing about the delivery path is broken; only the address policy differs");
    }

    private static string RebindUrl(int port) => $"http://rebind.example:{port}/hook";

    /// <summary>Public for the first answer, then the sink's loopback address for every later one.</summary>
    private static Func<string, CancellationToken, Task<IPAddress[]>> RebindingResolver(int port)
    {
        var answered = 0;
        return (_, _) =>
        {
            var first = Interlocked.Increment(ref answered) == 1;
            return Task.FromResult(new[] { first ? IPAddress.Parse("203.0.113.10") : IPAddress.Loopback });
        };
    }

    private static OutboundAddressGuard PermitsLoopback => new(isBlocked: _ => false);

    private static async Task<string> SeedTypeAsync(IDocumentStore store, string tenant)
    {
        var name = $"{tenant}-record";
        await using var session = store.LightweightSession(tenant);
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            Fields =
            [
                new FieldDefinition { Name = "Name", Type = "string" },
                new FieldDefinition { Name = "BirthDay", Type = "datetime", Sensitivity = SensitivityLevel.Sensitive },
                new FieldDefinition { Name = "SSN", Type = "string", Sensitivity = SensitivityLevel.Hidden },
            ],
        });
        await session.SaveChangesAsync();
        return name;
    }

    private static barakoCMS.Models.Content Record(string contentType, SensitivityLevel level) => new()
    {
        Id = Guid.NewGuid(),
        ContentType = contentType,
        Status = ContentStatus.Published,
        Sensitivity = level,
        Data = new Dictionary<string, object>
        {
            { "Name", "Sarah" },
            { "BirthDay", BirthDay },
            { "SSN", Ssn },
        },
    };

    /// <summary>
    /// The idempotency key the runner computed is actually sent.
    /// </summary>
    /// <remarks>
    /// It was not. <c>WorkflowRunner</c> put <c>IdempotencyKey</c> into the resolved parameters and
    /// no action read it, so the key was dead: the retries happened, the header did not, and every
    /// comment saying a duplicate delivery would be absorbed downstream was describing something
    /// that was never on the wire. A receiver had no way to tell a retry from a second publish.
    ///
    /// Asserted on the request rather than on the parameters, because the parameters were always
    /// right. Only the listener can say whether anything left the process.
    /// </remarks>
    [Fact]
    public async Task A_webhook_carries_the_idempotency_key_the_runner_computed()
    {
        const string tenant = "webhook-idempotency";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentType = await SeedTypeAsync(store, tenant);
        var content = Record(contentType, SensitivityLevel.Public);

        using var listener = new RecordingListener();

        await SendAsync(store, tenant, content, listener.Url, PermitsLoopback, new Dictionary<string, string>
        {
            ["Url"] = listener.Url,
            ["IdempotencyKey"] = "run-1234-ordinal-2",
        });

        listener.WasCalled.Should().BeTrue();
        listener.LastHeaders.Should().ContainKey("Idempotency-Key");
        listener.LastHeaders["Idempotency-Key"].Should().Be("run-1234-ordinal-2");
    }

    [Fact]
    public async Task A_webhook_configured_without_a_key_sends_no_header_rather_than_an_empty_one()
    {
        // The pairing. An empty Idempotency-Key is worse than none: a receiver deduplicating on it
        // would treat every delivery that carried one as the same delivery.
        const string tenant = "webhook-no-idempotency";
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentType = await SeedTypeAsync(store, tenant);
        var content = Record(contentType, SensitivityLevel.Public);

        using var listener = new RecordingListener();
        await SendAsync(store, tenant, content, listener.Url, PermitsLoopback);

        listener.WasCalled.Should().BeTrue();
        listener.LastHeaders.Should().NotContainKey("Idempotency-Key");
    }

    private static async Task SendAsync(
        IDocumentStore store,
        string tenant,
        barakoCMS.Models.Content content,
        string url,
        OutboundAddressGuard guard,
        Dictionary<string, string>? parameters = null)
    {
        await using var session = store.LightweightSession(tenant);
        using var handler = OutboundHttpHandler.Create(guard);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        var action = new WebhookAction(
            new SingleClientFactory(client),
            session,
            new Moq.Mock<barakoCMS.Infrastructure.Security.ISecretProtector>().Object,
            guard,
            NullLogger<WebhookAction>.Instance);

        await action.ExecuteAsync(parameters ?? new Dictionary<string, string> { { "Url", url } }, content, CancellationToken.None);
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public SingleClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    /// <summary>Records whether a TCP connection was ever opened to it, and answers a bare 200.</summary>
    private sealed class AcceptingListener : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopping = new();

        public AcceptingListener()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptAsync();
        }

        public int Port { get; }

        public bool WasConnected { get; private set; }

        private async Task AcceptAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                }
                catch
                {
                    return;
                }

                WasConnected = true;
                using (client)
                {
                    var reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    try
                    {
                        await client.GetStream().WriteAsync(reply, _stopping.Token);
                    }
                    catch
                    {
                        // The caller may already be gone; the accept is what this records.
                    }
                }
            }
        }

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Stop();
            _stopping.Dispose();
        }
    }
}
