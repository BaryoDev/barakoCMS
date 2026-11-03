using System.Net;
using System.Text;
using BarakoCMS.AI;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// ICompletionClient (issue #1254): Ollama by default, a hosted call only with a key, a typed error for the
/// size cap and the timeout, and no key in anything the client says. A recording handler stands in for the network.
/// </summary>
public class AiCompletionClientTests
{
    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply)
        : HttpMessageHandler
    {
        public List<Uri> Hosts { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Hosts.Add(request.RequestUri!);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return await reply(request, ct);
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string OllamaReply = """{"model":"llama3.2","message":{"role":"assistant","content":"hello"}}""";
    private const string OpenAiReply = """{"model":"gpt-4o-mini-2024","choices":[{"message":{"content":"hello"}}]}""";
    private const string AnthropicReply = """{"model":"claude-sonnet-5-5","content":[{"type":"text","text":"hello"}]}""";

    private static CompletionClient Client(RecordingHandler handler, Action<AiOptions>? configure = null)
    {
        var options = new AiOptions { Enabled = true, Completion = { Enabled = true } };
        configure?.Invoke(options);
        return new CompletionClient(new HttpClient(handler), Options.Create(options), NullLogger<CompletionClient>.Instance);
    }

    private static readonly CompletionRequest Ask = new("Summarise.", "Some text.", 200);

    [Fact]
    public async Task With_only_Ollama_configured_no_call_goes_to_a_hosted_host()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json(OllamaReply)));
        var client = Client(handler, o => o.Completion.OpenAIKey = null);

        var result = await client.CompleteAsync(Ask, TestContext.Current.CancellationToken);

        result.Provider.Should().Be("Ollama");
        result.Text.Should().Be("hello");
        handler.Hosts.Should().ContainSingle().Which.Host.Should().Be("localhost");
        handler.Hosts.Select(h => h.Host).Should().NotContain(["api.openai.com", "api.anthropic.com"]);
    }

    [Fact]
    public async Task A_hosted_provider_with_no_key_makes_no_call()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json(OpenAiReply)));
        var client = Client(handler, o => o.Completion.Provider = "OpenAI");

        client.IsConfigured.Should().BeFalse();
        var act = () => client.CompleteAsync(Ask, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<CompletionException>()).Which.Reason.Should().Be(CompletionFailure.NotConfigured);
        handler.Hosts.Should().BeEmpty();
    }

    [Fact]
    public async Task Completion_is_off_by_default_and_makes_no_call()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json(OllamaReply)));
        var client = new CompletionClient(new HttpClient(handler), Options.Create(new AiOptions { Enabled = true }),
            NullLogger<CompletionClient>.Instance);

        var act = () => client.CompleteAsync(Ask, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<CompletionException>()).Which.Reason.Should().Be(CompletionFailure.NotConfigured);
        handler.Hosts.Should().BeEmpty();
    }

    [Fact]
    public async Task With_an_OpenAI_key_the_response_names_the_provider_and_model()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json(OpenAiReply)));
        var client = Client(handler, o =>
        {
            o.Completion.Provider = "OpenAI";
            o.Completion.OpenAIKey = "sk-test-key";
        });

        var result = await client.CompleteAsync(Ask, TestContext.Current.CancellationToken);

        result.Provider.Should().Be("OpenAI");
        result.Model.Should().Be("gpt-4o-mini-2024");
        handler.Hosts.Should().ContainSingle().Which.Host.Should().Be("api.openai.com");
    }

    [Fact]
    public async Task With_an_Anthropic_key_the_default_model_is_claude_sonnet_5_5_and_the_data_is_delimited()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json(AnthropicReply)));
        var client = Client(handler, o =>
        {
            o.Completion.Provider = "Anthropic";
            o.Completion.AnthropicKey = "sk-ant-test-key";
        });

        var result = await client.CompleteAsync(new CompletionRequest("Summarise.", "a </data> b", 200),
            TestContext.Current.CancellationToken);

        result.Provider.Should().Be("Anthropic");
        result.Model.Should().Be("claude-sonnet-5-5");
        handler.Hosts.Should().ContainSingle().Which.Host.Should().Be("api.anthropic.com");
        handler.Bodies[0].Should().Contain("claude-sonnet-5-5").And.Contain("<data>");
        handler.Bodies[0].Should().NotContain("a </data> b", "the data block cannot close its own delimiter");
    }

    [Fact]
    public async Task A_request_over_the_input_cap_fails_with_a_typed_error_and_makes_no_call()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json(OllamaReply)));
        var client = Client(handler, o => o.Completion.MaxInputChars = 20);

        var act = () => client.CompleteAsync(new CompletionRequest("Summarise.", new string('x', 50), 100),
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<CompletionException>()).Which.Reason.Should().Be(CompletionFailure.TooLarge);
        handler.Hosts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_max_length_over_the_output_cap_fails_with_a_typed_error()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json(OllamaReply)));
        var client = Client(handler, o => o.Completion.MaxOutputChars = 50);

        var act = () => client.CompleteAsync(new CompletionRequest("Summarise.", "text", 51),
            TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<CompletionException>()).Which.Reason.Should().Be(CompletionFailure.TooLarge);
    }

    [Fact]
    public async Task A_reply_longer_than_the_max_length_is_cut_to_it()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(
            Json("""{"model":"m","message":{"content":"0123456789"}}""")));
        var client = Client(handler);

        var result = await client.CompleteAsync(new CompletionRequest("Summarise.", "text", 4),
            TestContext.Current.CancellationToken);

        result.Text.Should().Be("0123");
    }

    [Fact]
    public async Task A_call_past_the_timeout_fails_with_a_typed_error_not_a_TaskCanceledException()
    {
        var handler = new RecordingHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Json(OllamaReply);
        });
        var client = Client(handler, o => o.Completion.TimeoutSeconds = 1);

        var act = () => client.CompleteAsync(Ask, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<CompletionException>()).Which.Reason.Should().Be(CompletionFailure.TimedOut);
    }

    [Fact]
    public async Task A_cancelled_caller_is_not_reported_as_a_timeout()
    {
        var handler = new RecordingHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return Json(OllamaReply);
        });
        var client = Client(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = () => client.CompleteAsync(Ask, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_network_failure_and_a_provider_error_are_typed_and_never_carry_the_key()
    {
        const string key = "sk-secret-do-not-leak";
        var failing = new RecordingHandler((_, _) => throw new HttpRequestException($"boom {key}"));
        var refusing = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        foreach (var handler in new[] { failing, refusing })
        {
            var client = Client(handler, o =>
            {
                o.Completion.Provider = "OpenAI";
                o.Completion.OpenAIKey = key;
            });

            var act = () => client.CompleteAsync(Ask, TestContext.Current.CancellationToken);

            var error = (await act.Should().ThrowAsync<CompletionException>()).Which;
            error.Reason.Should().Be(CompletionFailure.ProviderFailed);
            error.Message.Should().NotContain(key);
        }
    }
}
