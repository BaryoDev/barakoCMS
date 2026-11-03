using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BarakoCMS.AI;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
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
        handler.Bodies[0].Should().Contain("claude-sonnet-5-5");
        var content = FirstUserContent(handler.Bodies[0]);
        content.Should().StartWith("<data>").And.EndWith("</data>");
        Regex.Matches(content, "</data>").Should().ContainSingle("the data block cannot close its own delimiter");
        content.Should().NotContain("a </data> b");
    }

    private static string FirstUserContent(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
    }

    [Fact]
    public async Task A_closing_tag_with_inner_whitespace_cannot_close_the_data_block()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json(OllamaReply)));
        var client = Client(handler);

        await client.CompleteAsync(new CompletionRequest("Summarise.", "a < /data> b </ DATA> c", 200),
            TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        var content = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Regex.Matches(content, @"<\s*/\s*data", RegexOptions.IgnoreCase).Should().ContainSingle();
        content.Should().EndWith("</data>");
    }

    public static TheoryData<string, string> EmptyAnswers => new()
    {
        { "Anthropic", "{}" },
        { "OpenAI", "{}" },
        { "OpenAI", """{"choices":[]}""" },
        { "Ollama", "{}" },
        { "Ollama", "not json at all" },
        { "Ollama", "[]" },
    };

    [Theory]
    [MemberData(nameof(EmptyAnswers))]
    public async Task An_answer_with_no_text_or_no_json_fails_as_ProviderFailed(string provider, string body)
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json(body)));
        var client = Client(handler, o =>
        {
            o.Completion.Provider = provider;
            o.Completion.OpenAIKey = "sk-test";
            o.Completion.AnthropicKey = "sk-ant-test";
        });

        var act = () => client.CompleteAsync(Ask, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<CompletionException>()).Which.Reason.Should().Be(CompletionFailure.ProviderFailed);
    }

    [Fact]
    public async Task A_key_with_a_newline_fails_as_NotConfigured_without_the_key_in_the_message()
    {
        const string key = "sk-bad\nkey-secret";
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json(AnthropicReply)));
        var client = Client(handler, o =>
        {
            o.Completion.Provider = "Anthropic";
            o.Completion.AnthropicKey = key;
        });

        var act = () => client.CompleteAsync(Ask, TestContext.Current.CancellationToken);

        var error = (await act.Should().ThrowAsync<CompletionException>()).Which;
        error.Reason.Should().Be(CompletionFailure.NotConfigured);
        error.Message.Should().NotContain("secret");
        handler.Hosts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_malformed_base_URL_fails_as_NotConfigured()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json(OllamaReply)));
        var client = Client(handler, o => o.EmbeddingBaseUrl = "http://[");

        var act = () => client.CompleteAsync(Ask, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<CompletionException>()).Which.Reason.Should().Be(CompletionFailure.NotConfigured);
        handler.Hosts.Should().BeEmpty();
    }

    [Fact]
    public async Task An_oversized_answer_fails_as_ProviderFailed_even_on_a_client_with_no_cap()
    {
        var big = """{"message":{"content":"%s"}}""".Replace("%s", new string('x', 1_100_000));
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json(big)));
        var client = Client(handler);

        var act = () => client.CompleteAsync(Ask, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<CompletionException>()).Which.Reason.Should().Be(CompletionFailure.ProviderFailed);
    }

    [Fact]
    public async Task An_oversized_answer_with_no_Content_Length_is_cut_off_while_reading()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 1_100_000)))),
        }));
        var client = Client(handler);

        var act = () => client.CompleteAsync(Ask, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<CompletionException>()).Which.Reason.Should().Be(CompletionFailure.ProviderFailed);
    }

    private static ServiceProvider ServicesWith(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        new AiModule().ConfigureServices(services, config);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("Completion:TimeoutSeconds", "2147483647")]
    [InlineData("Completion:TimeoutSeconds", "601")]
    [InlineData("Completion:TimeoutSeconds", "0")]
    [InlineData("Completion:AnthropicKey", "bad\nkey")]
    [InlineData("Completion:OpenAIKey", "bad\rkey")]
    public void Bad_completion_settings_fail_options_validation(string key, string value)
    {
        using var sp = ServicesWith(new() { [key] = value });

        var act = () => sp.GetRequiredService<IOptions<AiOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void The_default_completion_settings_pass_options_validation()
    {
        using var sp = ServicesWith(new() { ["Completion:TimeoutSeconds"] = "600" });

        sp.GetRequiredService<IOptions<AiOptions>>().Value.Completion.TimeoutSeconds.Should().Be(600);
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
