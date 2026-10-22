using System.Net;
using System.Text;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// Issue #1201: the Request action keeps named values from the provider's response as step outputs.
/// </summary>
/// <remarks>
/// The provider is a stub that answers a body the test chose. A response is untrusted, so the
/// bodies here include a planted placeholder, a credential header and a body over the read cap.
/// </remarks>
[Collection("Sequential")]
public class RequestOutputsActionTests : IAsyncLifetime
{
    private const string Reply =
        "{\"id\":\"r1\",\"choices\":[{\"message\":{\"content\":\"Plan: ship it\"}}],\"note\":null}";

    private readonly IntegrationTestFixture _factory;
    private readonly List<string> _hosts = [];
    private readonly List<(string Tenant, Guid ConnectorId)> _seeded = [];
    private RecordingSender? _lastSender;

    public RequestOutputsActionTests(IntegrationTestFixture factory) => _factory = factory;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var tenant in _seeded.GroupBy(s => s.Tenant))
        {
            await using var session = Store.LightweightSession(tenant.Key);

            foreach (var (_, id) in tenant)
            {
                session.DeleteWhere<WebhookDelivery>(d => d.ConnectorId == id);
                session.Delete<Connector>(id);
            }

            await session.SaveChangesAsync();
        }

        foreach (var host in _hosts) OAuthProviderStub.Forget(host);
    }

    private WebApplicationFactory<Program> Host => OAuthProviderStub.HostFor(_factory);

    private IDocumentStore Store => _factory.Services.GetRequiredService<IDocumentStore>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_named_value_from_the_response_is_returned_as_an_output()
    {
        var (action, request, content, tenant) = await ArrangeAsync(Reply, r =>
            r.Outputs = new() { ["plan"] = "$.choices[0].message.content", ["id"] = "$.id" });

        var result = await RunAsync(action, tenant, request, content);

        result.Succeeded.Should().BeTrue("got: {0}", result.Error);
        result.Outputs.Should().HaveCount(2);
        result.Outputs["plan"].Should().Be("Plan: ship it");
        result.Outputs["id"].Should().Be("r1");
    }

    [Fact]
    public async Task A_definition_with_no_outputs_returns_none_as_before()
    {
        var (action, request, content, tenant) = await ArrangeAsync(Reply, _ => { });

        var result = await RunAsync(action, tenant, request, content);

        result.Succeeded.Should().BeTrue("got: {0}", result.Error);
        result.Outputs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_later_create_entry_step_holds_the_response_value()
    {
        var (action, request, content, tenant) = await ArrangeAsync(Reply, r =>
            r.Outputs = new() { ["plan"] = "$.choices[0].message.content" });

        var result = await RunAsync(action, tenant, request, content);
        result.Succeeded.Should().BeTrue("got: {0}", result.Error);

        var (kept, note) = ActionOutputs.Keep(result.Outputs, ["plan"]);
        note.Should().BeNull();
        kept.Should().NotBeNull();

        var staged = await RunCreateEntryReadingAsync("{{actions.0.plan}}", kept!);

        staged.Should().NotBeNull("the second step ran and staged an entry");
        staged!.Data["Body"].Should().Be("Plan: ship it");
    }

    [Fact]
    public async Task A_response_value_holding_a_placeholder_is_stored_as_that_text_and_not_resolved()
    {
        const string body = "{\"text\":\"mail {{createdBy.email}} token {{actions.0.token}}\"}";
        var (action, request, content, tenant) = await ArrangeAsync(body, r =>
            r.Outputs = new() { ["plan"] = "$.text" });

        var result = await RunAsync(action, tenant, request, content);
        result.Succeeded.Should().BeTrue("got: {0}", result.Error);

        var (kept, _) = ActionOutputs.Keep(result.Outputs, ["plan"]);
        kept.Should().NotBeNull();
        kept!["plan"].Should().Be("mail {{createdBy.email}} token {{actions.0.token}}");

        var staged = await RunCreateEntryReadingAsync("{{actions.0.plan}}", kept);

        staged.Should().NotBeNull();
        staged!.Data["Body"].Should().Be("mail {{createdBy.email}} token {{actions.0.token}}");
    }

    [Fact]
    public async Task A_missing_path_fails_the_step_and_is_not_retried_when_the_definition_says_fail()
    {
        var (action, request, content, tenant) = await ArrangeAsync(Reply, r =>
        {
            r.Outputs = new() { ["plan"] = "$.choices[3].message.content" };
            r.MissingOutput = MissingOutputRule.Fail;
        });

        var result = await RunAsync(action, tenant, request, content);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse("sending again would call the provider again for the same shape of answer");
        result.Error.Should().Contain("'plan'").And.Contain("$.choices[3].message.content");
        result.Outputs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_path_gives_an_empty_output_when_the_definition_says_empty()
    {
        var (action, request, content, tenant) = await ArrangeAsync(Reply, r =>
        {
            r.Outputs = new() { ["plan"] = "$.choices[3].message.content", ["id"] = "$.id" };
            r.MissingOutput = MissingOutputRule.Empty;
        });

        var result = await RunAsync(action, tenant, request, content);

        result.Succeeded.Should().BeTrue("got: {0}", result.Error);
        result.Outputs.Should().HaveCount(2);
        result.Outputs["plan"].Should().BeEmpty();
        result.Outputs["id"].Should().Be("r1");
    }

    [Fact]
    public async Task A_value_longer_than_the_definitions_cap_fails_the_step_naming_the_cap()
    {
        var body = "{\"text\":\"" + new string('x', 41) + "\"}";
        var (action, request, content, tenant) = await ArrangeAsync(body, r =>
        {
            r.Outputs = new() { ["plan"] = "$.text" };
            r.MaxOutputLength = 40;
        });

        var result = await RunAsync(action, tenant, request, content);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("'plan'").And.Contain("40");
        result.Outputs.Should().BeEmpty("nothing is cut and kept");
    }

    [Fact]
    public async Task A_response_over_the_read_cap_fails_the_step_naming_the_cap()
    {
        var body = "{\"text\":\"" + new string('x', ResponseOutputs.MaxBodyBytes + 10) + "\"}";
        var (action, request, content, tenant) = await ArrangeAsync(body, r =>
            r.Outputs = new() { ["plan"] = "$.text" });

        var result = await RunAsync(action, tenant, request, content);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain(ResponseOutputs.MaxBodyBytes.ToString());
    }

    [Fact]
    public async Task A_response_that_is_not_json_fails_the_step()
    {
        var (action, request, content, tenant) = await ArrangeAsync("<html>bad gateway</html>", r =>
            r.Outputs = new() { ["plan"] = "$.text" });

        var result = await RunAsync(action, tenant, request, content);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("not JSON");
    }

    [Fact]
    public async Task A_response_header_is_never_an_output_and_the_body_is_never_stored_whole()
    {
        var (action, request, content, tenant) = await ArrangeAsync(Reply, r =>
            r.Outputs = new() { ["id"] = "$.id" }, responseHeader: ("Set-Cookie", "session=hunter2-cookie"));

        var result = await RunAsync(action, tenant, request, content);

        result.Succeeded.Should().BeTrue("got: {0}", result.Error);
        result.Outputs.Should().HaveCount(1);
        result.Outputs.Keys.Should().Equal("id");
        result.Outputs.Values.Should().NotContain(v => v.Contains("hunter2-cookie") || v.Contains("Plan: ship it"));
    }

    [Fact]
    public async Task An_output_the_workflow_does_not_capture_is_dropped_before_the_run_is_stored()
    {
        var (action, request, content, tenant) = await ArrangeAsync(Reply, r =>
            r.Outputs = new() { ["plan"] = "$.choices[0].message.content", ["id"] = "$.id" });

        var result = await RunAsync(action, tenant, request, content);
        result.Succeeded.Should().BeTrue("got: {0}", result.Error);
        result.Outputs.Should().HaveCount(2);

        var (kept, _) = ActionOutputs.Keep(result.Outputs, ["id"]);

        kept.Should().NotBeNull();
        kept!.Keys.Should().Equal("id");
        kept.Should().NotContainKey("plan");
    }

    [Fact]
    public async Task A_failed_send_returns_no_outputs_even_when_the_body_holds_the_paths()
    {
        var (action, request, content, tenant) = await ArrangeAsync(Reply, r =>
            r.Outputs = new() { ["id"] = "$.id" }, status: HttpStatusCode.InternalServerError);

        var result = await RunAsync(action, tenant, request, content);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeTrue("a 500 is the provider's trouble, not the definition's");
        _lastSender.Should().NotBeNull();
        _lastSender!.Last.Should().NotBeNull("the sender was asked");
        _lastSender.Last!.Succeeded.Should().BeFalse();
        _lastSender.Last.Outputs.Should().BeNull("a failed call hands back no outputs at all, not an empty set");
    }

    [Fact]
    public async Task A_sender_a_host_put_in_place_cannot_return_outputs_so_the_step_fails_without_sending()
    {
        var (_, request, content, tenant) = await ArrangeAsync(Reply, r =>
            r.Outputs = new() { ["id"] = "$.id" });
        var sender = new HostSender();

        await using var session = Store.QuerySession(tenant);
        var action = new RequestAction(
            session,
            new RequestComposer(session, _factory.Services.GetRequiredService<IConfiguration>(), new QueryRunner(session)),
            sender,
            NullLogger<RequestAction>.Instance);

        var result = await action.RunAsync(new() { ["Request"] = request.Slug }, content, Ct);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("outputs");
        sender.Sent.Should().Be(0, "a step that cannot keep what it asked for should not make the call");
    }

    [Fact]
    public async Task A_response_streamed_past_the_read_cap_is_cut_off_before_it_is_all_read()
    {
        var stream = new EndlessContent(declaredLength: null);
        var (action, request, content, tenant) = await ArrangeAsync(
            string.Empty, r => r.Outputs = new() { ["plan"] = "$.text" }, respond: () => Respond(stream));

        var result = await RunAsync(action, tenant, request, content);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain(ResponseOutputs.MaxBodyBytes.ToString());
        stream.TotalRead.Should().BeGreaterThan(0, "the cap was enforced by reading, so some of it was read");
        stream.TotalRead.Should().BeLessThanOrEqualTo(
            ResponseOutputs.MaxBodyBytes + 16384, "reading stops at the cap and does not buffer the rest");
    }

    [Fact]
    public async Task A_response_that_declares_a_length_over_the_cap_is_refused_without_reading_it()
    {
        var stream = new EndlessContent(declaredLength: ResponseOutputs.MaxBodyBytes + 1);
        var (action, request, content, tenant) = await ArrangeAsync(
            string.Empty, r => r.Outputs = new() { ["plan"] = "$.text" }, respond: () => Respond(stream));

        var result = await RunAsync(action, tenant, request, content);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain(ResponseOutputs.MaxBodyBytes.ToString());
        stream.TotalRead.Should().Be(0, "the length was in the headers, so no byte of the body was needed");
    }

    [Fact]
    public async Task The_success_rule_read_is_capped_too()
    {
        var stream = new EndlessContent(declaredLength: null);
        var (action, request, content, tenant) = await ArrangeAsync(
            string.Empty,
            r =>
            {
                r.Success = SuccessRule.TwoHundredAndJsonPathAbsent;
                r.SuccessJsonPath = "error";
            },
            respond: () => Respond(stream));

        var result = await RunAsync(action, tenant, request, content);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain(ResponseOutputs.MaxBodyBytes.ToString());
        stream.TotalRead.Should().BeLessThanOrEqualTo(ResponseOutputs.MaxBodyBytes + 16384);
    }

    [Fact]
    public async Task A_sender_that_succeeds_without_the_outputs_fails_the_step()
    {
        var (_, request, content, tenant) = await ArrangeAsync(Reply, r =>
            r.Outputs = new() { ["id"] = "$.id", ["plan"] = "$.choices[0].message.content" });

        foreach (var returned in new Dictionary<string, string>?[] { null, new() { ["id"] = "r1" }, new() { ["other"] = "x" } })
        {
            await using var session = Store.QuerySession(tenant);
            var action = new RequestAction(
                session,
                new RequestComposer(session, _factory.Services.GetRequiredService<IConfiguration>(), new QueryRunner(session)),
                new IgnoringSender(returned),
                NullLogger<RequestAction>.Instance);

            var result = await action.RunAsync(new() { ["Request"] = request.Slug }, content, Ct);

            result.Succeeded.Should().BeFalse("the sender said it worked but the declared outputs are not all there");
            result.Retryable.Should().BeFalse();
            result.Error.Should().Contain("outputs");
        }
    }

    [Fact]
    public async Task A_connector_bearer_token_echoed_in_the_response_fails_the_step_and_is_not_quoted()
    {
        const string token = "tok-live-9f8e7d6c5b4a";
        var (action, request, content, tenant) = await ArrangeAsync(
            "{\"echo\":\"Bearer " + token + "\"}", r => r.Outputs = new() { ["echo"] = "$.echo" }, bearerToken: token);

        var result = await RunAsync(action, tenant, request, content);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("'echo'").And.NotContain(token);
    }

    private static Task<HttpResponseMessage> Respond(HttpContent content) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

    private async Task<WorkflowActionResult> RunAsync(
        RequestAction action, string tenant, RequestDefinition request, barakoCMS.Models.Content content) =>
        await action.RunAsync(new Dictionary<string, string>
        {
            ["Request"] = request.Slug,
            ["RunId"] = Guid.NewGuid().ToString(),
            ["WorkflowId"] = Guid.NewGuid().ToString(),
            ["TriggerEvent"] = "Published",
        }, content, Ct);

    /// <summary>
    /// Runs a CreateEntry step the way the runner does: the reference is masked before the entry's
    /// placeholders resolve, and the kept output is put in after.
    /// </summary>
    private static async Task<ContentCreateRequest?> RunCreateEntryReadingAsync(
        string template, Dictionary<string, string> kept)
    {
        var parameters = new Dictionary<string, string>
        {
            ["ContentType"] = "draft",
            ["Data.Body"] = template,
        };

        var (masked, slots) = ActionOutputs.Mask("CreateEntry", parameters);
        var resolved = new Dictionary<string, string>(masked);
        var missing = ActionOutputs.Substitute(
            "CreateEntry", resolved, slots,
            new Dictionary<int, IReadOnlyDictionary<string, string>> { [0] = kept });
        missing.Should().BeNull();

        ContentCreateRequest? staged = null;
        var creator = Substitute.For<IContentCreator>();
        creator.CheckAsync(default!, default, default!, default, default).ReturnsForAnyArgs(Array.Empty<string>());
        creator.StageAsync(default!, default, default, default).ReturnsForAnyArgs(call =>
        {
            staged = (ContentCreateRequest)call[0];
            return new barakoCMS.Models.Content { Id = Guid.NewGuid(), ContentType = "draft" };
        });

        var create = new CreateEntryAction(
            Substitute.For<IDocumentSession>(), creator, NullLogger<CreateEntryAction>.Instance);

        var result = await create.RunAsync(
            resolved, new barakoCMS.Models.Content { Id = Guid.NewGuid(), ContentType = "order" }, Ct);
        result.Succeeded.Should().BeTrue(result.Error);

        return staged;
    }

    private async Task<(RequestAction Action, RequestDefinition Request, barakoCMS.Models.Content Content, string Tenant)> ArrangeAsync(
        string responseBody, Action<RequestDefinition> configure,
        HttpStatusCode status = HttpStatusCode.OK, (string Name, string Value)? responseHeader = null,
        Func<Task<HttpResponseMessage>>? respond = null, string? bearerToken = null)
    {
        var tenant = "rout-" + Guid.NewGuid().ToString("n")[..8];
        var host = $"rout{Guid.NewGuid().ToString("n")[..10]}.example";

        OAuthProviderStub.Route(host, (_, _) =>
        {
            if (respond is not null) return respond();

            var response = OAuthProviderStub.Json(status, responseBody);
            if (responseHeader is { } header) response.Headers.TryAddWithoutValidation(header.Name, header.Value);
            return Task.FromResult(response);
        });
        _hosts.Add(host);

        var type = "rout" + Guid.NewGuid().ToString("n")[..10];

        var connector = new Connector
        {
            Id = Guid.NewGuid(),
            Name = "Outputs subject",
            Slug = "rout" + Guid.NewGuid().ToString("n")[..10],
            BaseUrl = $"https://{host}",
            Auth = bearerToken is null ? ConnectorAuth.None : ConnectorAuth.BearerToken,
            SecretKeys = bearerToken is null ? [] : [ConnectorSecretKeys.Token],
        };

        var request = new RequestDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Ask it",
            Slug = "req" + Guid.NewGuid().ToString("n")[..10],
            ConnectorSlug = connector.Slug,
            Method = "POST",
            PathTemplate = "/chat",
            BodyTemplate = "{\"title\":\"{{Title}}\"}",
        };
        configure(request);

        var content = new barakoCMS.Models.Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Status = ContentStatus.Published,
            Sensitivity = SensitivityLevel.Public,
            Data = new Dictionary<string, object> { ["Title"] = "hello" },
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        await using (var session = Store.LightweightSession(tenant))
        {
            session.Store(connector);
            session.Store(request);

            if (bearerToken is not null)
            {
                session.Store(new ConnectorSecret
                {
                    Id = Guid.NewGuid(),
                    ConnectorId = connector.Id,
                    Key = ConnectorSecretKeys.Token,
                    ProtectedValue = _factory.Services.GetRequiredService<IConnectorSecretProtector>().Protect(bearerToken),
                });
            }

            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = "Outputs subject",
                Fields = [new FieldDefinition { Name = "Title", Type = "string" }],
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await session.SaveChangesAsync(Ct);
        }

        _seeded.Add((tenant, connector.Id));

        var read = Store.QuerySession(tenant);
        _lastSender = new RecordingSender(OAuthConnectors.Sender(Host.Services, read, new ConnectorTokenCache(new TestClock())));
        var action = new RequestAction(
            read,
            new RequestComposer(read, _factory.Services.GetRequiredService<IConfiguration>(), new QueryRunner(read)),
            _lastSender,
            NullLogger<RequestAction>.Instance);

        return (action, request, content, tenant);
    }

    /// <summary>The real sender, remembering what it last answered.</summary>
    private sealed class RecordingSender(ConnectorSender inner) : IConnectorSender, IConnectorDeliverySender
    {
        public ConnectorCallResult? Last { get; private set; }

        public Task<ConnectorCallResult> ProbeAsync(Connector connector, CancellationToken ct) =>
            inner.ProbeAsync(connector, ct);

        public async Task<ConnectorCallResult> SendAsync(
            Connector connector, ComposedRequest request, SuccessRule rule, string? successJsonPath, CancellationToken ct) =>
            Last = await inner.SendAsync(connector, request, rule, successJsonPath, ct);

        public async Task<ConnectorCallResult> SendAsync(
            Connector connector, ComposedRequest request, SuccessRule rule, string? successJsonPath,
            ConnectorDeliveryContext delivery, CancellationToken ct) =>
            Last = await inner.SendAsync(connector, request, rule, successJsonPath, delivery, ct);
    }

    /// <summary>A sender that reports success and ignores the capture it was handed.</summary>
    private sealed class IgnoringSender(Dictionary<string, string>? outputs) : IConnectorSender, IConnectorDeliverySender
    {
        public Task<ConnectorCallResult> ProbeAsync(Connector connector, CancellationToken ct) =>
            Task.FromResult(new ConnectorCallResult(true, 200, 1, null));

        public Task<ConnectorCallResult> SendAsync(
            Connector connector, ComposedRequest request, SuccessRule rule, string? successJsonPath, CancellationToken ct) =>
            Task.FromResult(new ConnectorCallResult(true, 200, 1, null) { Outputs = outputs });

        public Task<ConnectorCallResult> SendAsync(
            Connector connector, ComposedRequest request, SuccessRule rule, string? successJsonPath,
            ConnectorDeliveryContext delivery, CancellationToken ct) =>
            SendAsync(connector, request, rule, successJsonPath, ct);
    }

    /// <summary>
    /// A response body that never ends and throws if it is read far past the cap, so a sender that
    /// buffers the whole body before checking its size goes red.
    /// </summary>
    private sealed class EndlessContent(long? declaredLength) : HttpContent
    {
        private readonly CountingStream _stream = new(ResponseOutputs.MaxBodyBytes * 4L);

        public long TotalRead => _stream.Total;

        protected override bool TryComputeLength(out long length)
        {
            length = declaredLength ?? 0;
            return declaredLength is not null;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            _stream.CopyToAsync(stream);

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(_stream);
    }

    private sealed class CountingStream(long failAfter) : Stream
    {
        public long Total { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Total + count > failAfter) throw new IOException("read far past the cap");
            Array.Fill(buffer, (byte)'x', offset, count);
            Total += count;
            return count;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class HostSender : IConnectorSender
    {
        public int Sent { get; private set; }

        public Task<ConnectorCallResult> ProbeAsync(Connector connector, CancellationToken ct) =>
            Task.FromResult(new ConnectorCallResult(true, 200, 1, null));

        public Task<ConnectorCallResult> SendAsync(
            Connector connector, ComposedRequest request, SuccessRule rule, string? successJsonPath, CancellationToken ct)
        {
            Sent++;
            return Task.FromResult(new ConnectorCallResult(true, 200, 1, null));
        }
    }
}
