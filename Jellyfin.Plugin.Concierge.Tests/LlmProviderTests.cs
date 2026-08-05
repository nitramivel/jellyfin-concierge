using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Concierge.Services.Llm;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    public class LlmProviderTests
    {
        private static readonly TimeSpan NoDelay = TimeSpan.FromMilliseconds(1);

        private static readonly LlmRequest Request = new("SYSTEM", "PREFIX", "SUFFIX", 4096);

        private const string AnthropicBody =
            """
            {"id":"msg_1","type":"message","role":"assistant","model":"claude-haiku-4-5-20251001",
             "content":[{"type":"text","text":"answer"}],
             "stop_reason":"end_turn",
             "usage":{"input_tokens":100,"output_tokens":20,
                      "cache_creation_input_tokens":7,"cache_read_input_tokens":900}}
            """;

        private const string OpenAiBody =
            """
            {"choices":[{"message":{"content":"answer"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":1000,"completion_tokens":50,
                      "prompt_tokens_details":{"cached_tokens":400},
                      "completion_tokens_details":{"reasoning_tokens":30}}}
            """;

        private const string GoogleBody =
            """
            {"candidates":[{"finishReason":"STOP",
                            "content":{"parts":[{"text":"answer"},{"thought":true,"text":"ignored"}]}}],
             "usageMetadata":{"promptTokenCount":1000,"candidatesTokenCount":50,
                              "thoughtsTokenCount":12,"cachedContentTokenCount":400}}
            """;

        // ── Anthropic ────────────────────────────────────────────────────────────

        [Fact]
        public async Task Anthropic_AlwaysSendsThinkingExplicitly()
        {
            // Omitting `thinking` on Opus 5 runs adaptive thinking, unlike Opus 4.8
            // where omitting it meant none. Inside a 2.5-second search budget that is
            // a silent latency and cost regression, so the field is never left off.
            foreach (var enabled in new[] { true, false })
            {
                var handler = new StubHandler(AnthropicBody);
                using var client = new HttpClient(handler);
                var provider = new AnthropicProvider(client, "claude-haiku-4-5-20251001", "key", null, enabled);

                await provider.CompleteAsync(Request, CancellationToken.None);

                using var body = JsonDocument.Parse(handler.RequestBody!);
                var thinking = body.RootElement.GetProperty("thinking").GetProperty("type").GetString();
                Assert.Equal(enabled ? "adaptive" : "disabled", thinking);
            }
        }

        [Fact]
        public async Task Anthropic_SendsNoSamplingParameters()
        {
            // temperature, top_p and top_k are rejected outright by the current
            // models. Steering happens in the prompt.
            var handler = new StubHandler(AnthropicBody);
            using var client = new HttpClient(handler);
            var provider = new AnthropicProvider(client, "claude-sonnet-5", "key");

            await provider.CompleteAsync(Request, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.False(body.RootElement.TryGetProperty("temperature", out _));
            Assert.False(body.RootElement.TryGetProperty("top_p", out _));
            Assert.False(body.RootElement.TryGetProperty("top_k", out _));

            // budget_tokens is gone from the API; thinking is adaptive-or-disabled.
            Assert.False(body.RootElement.GetProperty("thinking").TryGetProperty("budget_tokens", out _));
        }

        [Fact]
        public async Task Anthropic_MarksThePrefixCacheableOnlyWhenThereIsAlsoASuffix()
        {
            var handler = new StubHandler(AnthropicBody);
            using var client = new HttpClient(handler);
            var provider = new AnthropicProvider(client, "m", "key");

            await provider.CompleteAsync(Request, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");

            Assert.Equal(2, content.GetArrayLength());
            Assert.Equal("1h", content[0].GetProperty("cache_control").GetProperty("ttl").GetString());
            Assert.False(content[1].TryGetProperty("cache_control", out _));
        }

        [Fact]
        public async Task Anthropic_EmptySuffix_SendsOneUnmarkedBlockRatherThanAnEmptyOne()
        {
            // An empty text block is a 400 that takes the whole call down, and a pass
            // that puts its entire prompt in the prefix hands this an empty suffix by
            // design. Marking a prefix with nothing to reuse would also pay the cache
            // write premium for a read that can never happen.
            var handler = new StubHandler(AnthropicBody);
            using var client = new HttpClient(handler);
            var provider = new AnthropicProvider(client, "m", "key");

            await provider.CompleteAsync(new LlmRequest("SYSTEM", "EVERYTHING", string.Empty, 1024), CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");

            Assert.Equal(1, content.GetArrayLength());
            Assert.Equal("EVERYTHING", content[0].GetProperty("text").GetString());
            Assert.False(content[0].TryGetProperty("cache_control", out _));
        }

        [Fact]
        public async Task Anthropic_RequestWithNoPromptAtAll_Throws()
        {
            var handler = new StubHandler(AnthropicBody);
            using var client = new HttpClient(handler);
            var provider = new AnthropicProvider(client, "m", "key");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.CompleteAsync(new LlmRequest("S", string.Empty, string.Empty, 1024), CancellationToken.None));
        }

        [Fact]
        public async Task Anthropic_ReportsUncachedInputAndCacheCountsSeparately()
        {
            // Anthropic's input_tokens is already the uncached remainder — the
            // opposite convention to OpenAI and Gemini.
            var handler = new StubHandler(AnthropicBody);
            using var client = new HttpClient(handler);
            var provider = new AnthropicProvider(client, "m", "key");

            var result = await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal(100, result.InputTokens);
            Assert.Equal(20, result.OutputTokens);
            Assert.Equal(7, result.CacheWriteTokens);
            Assert.Equal(900, result.CacheReadTokens);
            Assert.False(result.Truncated);
        }

        [Fact]
        public async Task Anthropic_Refusal_Throws()
        {
            var handler = new StubHandler("""{"stop_reason":"refusal","content":[]}""");
            using var client = new HttpClient(handler);
            var provider = new AnthropicProvider(client, "m", "key");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.CompleteAsync(Request, CancellationToken.None));
        }

        // ── OpenAI-shaped ────────────────────────────────────────────────────────

        [Fact]
        public async Task OpenAi_SubtractsCachedTokensFromTheReportedInput()
        {
            // prompt_tokens is the TOTAL including the cached span. Leaving it whole
            // while also charging the cached span double-bills it, which is how a
            // cost line quietly runs ~25% high.
            var handler = new StubHandler(OpenAiBody);
            using var client = new HttpClient(handler);
            var provider = OpenAiChatProvider.CreateOpenAi(client, "gpt-x", "key");

            var result = await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal(600, result.InputTokens);
            Assert.Equal(400, result.CacheReadTokens);
            Assert.Equal(50, result.OutputTokens);
            Assert.Equal(30, result.ThinkingTokens);
        }

        [Fact]
        public async Task OpenAi_FinishReasonLength_MarksTheResultTruncated()
        {
            var handler = new StubHandler(
                """{"choices":[{"message":{"content":"cut"},"finish_reason":"length"}],"usage":{}}""");
            using var client = new HttpClient(handler);
            var provider = OpenAiChatProvider.CreateOpenAi(client, "gpt-x", "key");

            var result = await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.True(result.Truncated);
        }

        [Fact]
        public async Task OpenAi_SendsThePromptCacheKeyButNoGrokHeader()
        {
            var handler = new StubHandler(OpenAiBody);
            using var client = new HttpClient(handler);
            var provider = OpenAiChatProvider.CreateOpenAi(client, "gpt-x", "key");

            await provider.CompleteAsync(Request with { ConversationId = "run-1" }, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.Equal("run-1", body.RootElement.GetProperty("prompt_cache_key").GetString());
            Assert.False(handler.Request!.Headers.Contains("x-grok-conv-id"));
        }

        [Fact]
        public async Task Grok_SendsTheConversationHeaderButNoPromptCacheKey()
        {
            // xAI holds cache entries per server; without the header a run's calls
            // scatter across the fleet and each lands somewhere cold.
            var handler = new StubHandler(OpenAiBody);
            using var client = new HttpClient(handler);
            var provider = OpenAiChatProvider.CreateGrok(client, "grok-4", "key", null, NoDelay);

            await provider.CompleteAsync(Request with { ConversationId = "run-1" }, CancellationToken.None);

            Assert.Equal("run-1", string.Join(string.Empty, handler.Request!.Headers.GetValues("x-grok-conv-id")));
            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.False(body.RootElement.TryGetProperty("prompt_cache_key", out _));
        }

        [Fact]
        public async Task Compatible_UsesLegacyMaxTokensAndSendsNoResponseFormat()
        {
            // A local server that has never heard of response_format rejects the
            // whole request rather than ignoring the field.
            var handler = new StubHandler(OpenAiBody);
            using var client = new HttpClient(handler);
            var provider = OpenAiChatProvider.CreateCompatible(client, "llama", "http://localhost:11434/v1");

            await provider.CompleteAsync(Request, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.True(body.RootElement.TryGetProperty("max_tokens", out _));
            Assert.False(body.RootElement.TryGetProperty("max_completion_tokens", out _));
            Assert.False(body.RootElement.TryGetProperty("response_format", out _));
        }

        [Fact]
        public async Task NoShapeRequested_SendsNoResponseFormatEvenOnAStructuredProvider()
        {
            // Phase 0 constrains nothing. A schema-capable provider must send no
            // response_format rather than an empty one.
            var handler = new StubHandler(OpenAiBody);
            using var client = new HttpClient(handler);
            var provider = OpenAiChatProvider.CreateOpenAi(client, "gpt-x", "key");

            await provider.CompleteAsync(Request with { Shape = ResponseShape.None }, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.False(body.RootElement.TryGetProperty("response_format", out _));
        }

        // ── Google ───────────────────────────────────────────────────────────────

        [Fact]
        public async Task Google_SubtractsCachedInputAndCountsThinkingAsOutput()
        {
            var handler = new StubHandler(GoogleBody);
            using var client = new HttpClient(handler);
            var provider = new GoogleProvider(client, "gemini-flash", "key", null, true, NoDelay);

            var result = await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal(600, result.InputTokens);
            Assert.Equal(400, result.CacheReadTokens);
            Assert.Equal(62, result.OutputTokens);          // 50 answer + 12 thinking
            Assert.Equal(12, result.ThinkingTokens);
            Assert.Equal("answer", result.Text);            // the thought part is dropped
        }

        [Fact]
        public async Task Google_ThinkingDisabled_SendsAZeroBudget()
        {
            var handler = new StubHandler(GoogleBody);
            using var client = new HttpClient(handler);
            var provider = new GoogleProvider(client, "gemini-flash", "key", null, false, NoDelay);

            await provider.CompleteAsync(Request, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var budget = body.RootElement.GetProperty("generationConfig")
                .GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32();
            Assert.Equal(0, budget);
        }

        [Fact]
        public async Task Google_ThinkingEnabled_LeavesTheBudgetToTheModel()
        {
            var handler = new StubHandler(GoogleBody);
            using var client = new HttpClient(handler);
            var provider = new GoogleProvider(client, "gemini-pro", "key", null, true, NoDelay);

            await provider.CompleteAsync(Request, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.False(body.RootElement.GetProperty("generationConfig").TryGetProperty("thinkingConfig", out _));
        }

        [Fact]
        public async Task Google_TurnsSafetyFilteringOff()
        {
            // The prompt is a list of the user's own films. A library with horror or
            // true crime in it will trip a filter sooner or later, and a blocked
            // response loses a call that was already paid for.
            var handler = new StubHandler(GoogleBody);
            using var client = new HttpClient(handler);
            var provider = new GoogleProvider(client, "gemini-flash", "key", null, true, NoDelay);

            await provider.CompleteAsync(Request, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var settings = body.RootElement.GetProperty("safetySettings");
            Assert.Equal(4, settings.GetArrayLength());
            foreach (var setting in settings.EnumerateArray())
            {
                Assert.Equal("OFF", setting.GetProperty("threshold").GetString());
            }
        }

        [Fact]
        public async Task Google_NoShapeRequested_SendsNoResponseSchema()
        {
            var handler = new StubHandler(GoogleBody);
            using var client = new HttpClient(handler);
            var provider = new GoogleProvider(client, "gemini-flash", "key", null, true, NoDelay);

            await provider.CompleteAsync(Request, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var config = body.RootElement.GetProperty("generationConfig");
            Assert.False(config.TryGetProperty("responseSchema", out _));
            Assert.False(config.TryGetProperty("responseMimeType", out _));
        }

        [Fact]
        public async Task Google_BlockedPrompt_Throws()
        {
            var handler = new StubHandler("""{"promptFeedback":{"blockReason":"SAFETY"}}""");
            using var client = new HttpClient(handler);
            var provider = new GoogleProvider(client, "gemini-flash", "key", null, true, NoDelay);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.CompleteAsync(Request, CancellationToken.None));
        }

        [Fact]
        public void Google_AcceptsAModelIdWithOrWithoutTheModelsPrefix()
        {
            using var client = new HttpClient(new StubHandler(GoogleBody));

            Assert.Equal(
                new GoogleProvider(client, "gemini-flash", "k").ModelId,
                new GoogleProvider(client, "models/gemini-flash", "k").ModelId);
        }

        // ── Transient retry ──────────────────────────────────────────────────────

        [Fact]
        public async Task RateLimited_IsRetried()
        {
            // Every hosted provider meters per minute and an index build fires
            // requests back to back, so 429 is ordinary operation, not an error.
            var handler = new SequenceHandler(
                new Reply(HttpStatusCode.TooManyRequests, "slow down", RetryAfterSeconds: 0),
                new Reply(HttpStatusCode.OK, OpenAiBody));
            using var client = new HttpClient(handler);
            var provider = OpenAiChatProvider.CreateGrok(client, "grok-4", "key", null, NoDelay);

            var result = await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal(2, handler.SendCount);
            Assert.Equal("answer", result.Text);
        }

        [Fact]
        public async Task BadRequest_FailsImmediately()
        {
            // A wrong key or a bad model id must fail fast rather than take a minute
            // of backoff to say so.
            var handler = new SequenceHandler(new Reply(HttpStatusCode.BadRequest, "nope"));
            using var client = new HttpClient(handler);
            var provider = OpenAiChatProvider.CreateGrok(client, "grok-4", "key", null, NoDelay);

            await Assert.ThrowsAsync<HttpRequestException>(() =>
                provider.CompleteAsync(Request, CancellationToken.None));

            Assert.Equal(1, handler.SendCount);
        }

        [Fact]
        public async Task PersistentServerError_GivesUpAfterTheAttemptCap()
        {
            var handler = new SequenceHandler(new Reply(HttpStatusCode.ServiceUnavailable, "down"));
            using var client = new HttpClient(handler);
            var provider = OpenAiChatProvider.CreateGrok(client, "grok-4", "key", null, NoDelay);

            await Assert.ThrowsAsync<HttpRequestException>(() =>
                provider.CompleteAsync(Request, CancellationToken.None));

            Assert.Equal(TransientHttpRetry.MaxAttempts, handler.SendCount);
        }
    }
}

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// A throttled call must say so, because its waiting is billed to the caller's
    /// clock and looks exactly like a slow model.
    /// </summary>
    /// <remarks>
    /// Observed on the owner's server: a re-rank returned 85 output tokens in 83
    /// seconds — one token per second, against 145 a moment earlier. Nothing in any log
    /// said whether that was backoff or a slow response, because a retry that
    /// eventually succeeded wrote nothing at all and only an exhausted one ever threw.
    /// </remarks>
    public class TransientRetryIsAudibleTests
    {
        private sealed class CountingLogger : Microsoft.Extensions.Logging.ILogger
        {
            public System.Collections.Generic.List<string> Lines { get; } = [];

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(
                Microsoft.Extensions.Logging.LogLevel logLevel,
                Microsoft.Extensions.Logging.EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => Lines.Add(formatter(state, exception));
        }

        [Fact]
        public async Task ARetriedCall_SaysWhyItTookSoLong()
        {
            var handler = new SequenceHandler(
                new Reply(System.Net.HttpStatusCode.TooManyRequests, "{}", RetryAfterSeconds: 1),
                new Reply(System.Net.HttpStatusCode.OK, GoogleOkBody));

            using var client = new HttpClient(handler);
            var logger = new CountingLogger();
            var provider = new GoogleProvider(
                client, "gemini-throttled", "key", null, true,
                TimeSpan.FromMilliseconds(1), logger: null);

            // Routed through the retry helper directly, since the provider only takes a
            // typed logger — the behaviour under test belongs to the helper.
            var body = await Jellyfin.Plugin.Concierge.Services.Llm.TransientHttpRetry.SendAsync(
                client,
                () => new HttpRequestMessage(HttpMethod.Post, "http://localhost/x"),
                (status, b) => $"failed {(int)status}",
                TimeSpan.FromMilliseconds(1),
                System.Threading.CancellationToken.None,
                logger,
                "gemini-throttled");

            Assert.NotNull(body);
            var line = Assert.Single(logger.Lines);
            Assert.Contains("gemini-throttled", line, StringComparison.Ordinal);
            Assert.Contains("429", line, StringComparison.Ordinal);
            Assert.Contains("retrying", line, StringComparison.Ordinal);
        }

        private const string GoogleOkBody =
            """
            {"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"ok"}]}}],
             "usageMetadata":{"promptTokenCount":1,"candidatesTokenCount":1}}
            """;
    }
}
