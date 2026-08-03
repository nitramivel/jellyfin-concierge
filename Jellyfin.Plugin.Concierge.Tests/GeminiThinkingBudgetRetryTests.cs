using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Concierge.Services.Llm;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// A Gemini model that will not be told to stop thinking must still answer.
    /// </summary>
    /// <remarks>
    /// Not every Gemini model accepts <c>thinkingBudget: 0</c>. The ones that do not
    /// reject the entire request rather than ignoring the field, so a profile pointed
    /// at such a model fails on every single call, permanently, from the moment it is
    /// selected.
    /// <para>
    /// That is not hypothetical: a re-rank profile on this install returned HTTP 400
    /// forty-one times over three hours. Nothing surfaced it — the plan and re-rank
    /// passes both degrade to the free answer — and the only symptom anyone could
    /// describe was "the results seem worse". Asking once more without the budget
    /// turns that outage into a single wasted call.
    /// </para>
    /// </remarks>
    public class GeminiThinkingBudgetRetryTests
    {
        private static readonly TimeSpan NoDelay = TimeSpan.FromMilliseconds(1);

        private const string Ok =
            """
            {"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"answer"}]}}],
             "usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5}}
            """;

        private const string Refusal =
            """
            {"error":{"code":400,"message":"Request contains an invalid argument.",
                      "status":"INVALID_ARGUMENT"}}
            """;

        /// <summary>Plays a scripted sequence and keeps every request body sent.</summary>
        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly Queue<(HttpStatusCode Status, string Body)> _replies;
            private (HttpStatusCode Status, string Body) _last;

            public RecordingHandler(params (HttpStatusCode Status, string Body)[] replies)
                => _replies = new Queue<(HttpStatusCode, string)>(replies);

            public List<string> Bodies { get; } = [];

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Bodies.Add(request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken));

                if (_replies.Count > 0)
                {
                    _last = _replies.Dequeue();
                }

                return new HttpResponseMessage(_last.Status) { Content = new StringContent(_last.Body) };
            }
        }

        private static readonly LlmRequest Request =
            new("system", "prefix", "suffix", 800, ResponseShape.Rerank);

        private static bool SendsBudget(string body)
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.GetProperty("generationConfig")
                .TryGetProperty("thinkingConfig", out _);
        }

        [Fact]
        public async Task AModelThatRefusesAZeroBudget_IsAskedAgainWithoutOne()
        {
            var handler = new RecordingHandler(
                (HttpStatusCode.BadRequest, Refusal),
                (HttpStatusCode.OK, Ok));

            using var client = new HttpClient(handler);
            var provider = new GoogleProvider(client, "gemini-3.6-flash", "key", null, false, NoDelay);

            var result = await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal("answer", result.Text);
            Assert.Equal(2, handler.Bodies.Count);
            Assert.True(SendsBudget(handler.Bodies[0]), "the first attempt should still ask for no thinking");
            Assert.False(SendsBudget(handler.Bodies[1]), "the retry must drop thinkingConfig entirely");
        }

        [Fact]
        public async Task OnceItHasRefused_LaterCallsDoNotPayToAskAgain()
        {
            var handler = new RecordingHandler(
                (HttpStatusCode.BadRequest, Refusal),
                (HttpStatusCode.OK, Ok));

            using var client = new HttpClient(handler);
            var provider = new GoogleProvider(client, "gemini-3.6-flash", "key", null, false, NoDelay);

            await provider.CompleteAsync(Request, CancellationToken.None);
            handler.Bodies.Clear();

            await provider.CompleteAsync(Request, CancellationToken.None);

            // One call, and it never mentions the budget again. Otherwise every query
            // for the life of the process pays for a failure already known about.
            Assert.Single(handler.Bodies);
            Assert.False(SendsBudget(handler.Bodies[0]));
        }

        [Fact]
        public async Task AnUnrelatedBadRequest_DoesNotSilentlyTurnThinkingBackOn()
        {
            // Gemini's 400 names no field, so the retry cannot know why it failed. If
            // the second attempt fails too, the budget was never the problem — and
            // latching on that would quietly disable a setting the owner chose, then
            // bill them for the reasoning it lets through.
            var handler = new RecordingHandler((HttpStatusCode.BadRequest, Refusal));

            using var client = new HttpClient(handler);
            var provider = new GoogleProvider(client, "gemini-3.6-flash", "key", null, false, NoDelay);

            await Assert.ThrowsAsync<HttpRequestException>(
                () => provider.CompleteAsync(Request, CancellationToken.None));

            handler.Bodies.Clear();
            await Assert.ThrowsAsync<HttpRequestException>(
                () => provider.CompleteAsync(Request, CancellationToken.None));

            // Still asking for no thinking, because nothing ever proved it was refused.
            Assert.True(SendsBudget(handler.Bodies[0]));
        }

        [Fact]
        public async Task WithThinkingOn_A400IsNotRetried()
        {
            // No budget was sent, so there is nothing to drop and a second identical
            // request would just be a second failure.
            var handler = new RecordingHandler((HttpStatusCode.BadRequest, Refusal));

            using var client = new HttpClient(handler);
            var provider = new GoogleProvider(client, "gemini-3.6-flash", "key", null, true, NoDelay);

            await Assert.ThrowsAsync<HttpRequestException>(
                () => provider.CompleteAsync(Request, CancellationToken.None));

            Assert.Single(handler.Bodies);
            Assert.False(SendsBudget(handler.Bodies[0]));
        }

        [Fact]
        public async Task AWorkingModel_IsUnaffected()
        {
            // gemini-3.5-flash accepts the budget. Nothing about this path may change
            // for it: one call, thinking genuinely off.
            var handler = new RecordingHandler((HttpStatusCode.OK, Ok));

            using var client = new HttpClient(handler);
            var provider = new GoogleProvider(client, "gemini-3.5-flash", "key", null, false, NoDelay);

            var result = await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal("answer", result.Text);
            Assert.Single(handler.Bodies);
            Assert.True(SendsBudget(handler.Bodies[0]));
        }
    }
}
