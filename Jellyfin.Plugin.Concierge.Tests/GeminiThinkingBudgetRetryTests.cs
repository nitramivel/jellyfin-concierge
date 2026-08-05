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

        private static int? Budget(string body)
        {
            using var document = JsonDocument.Parse(body);
            var config = document.RootElement.GetProperty("generationConfig");
            return config.TryGetProperty("thinkingConfig", out var thinking)
                ? thinking.GetProperty("thinkingBudget").GetInt32()
                : null;
        }

        private static bool SendsBudget(string body)
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.GetProperty("generationConfig")
                .TryGetProperty("thinkingConfig", out _);
        }

        [Fact]
        public async Task AModelThatRefusesAZeroBudget_StillAnswers()
        {
            var handler = new RecordingHandler(
                (HttpStatusCode.BadRequest, Refusal),
                (HttpStatusCode.OK, Ok));

            using var client = new HttpClient(handler);
            var provider = new GoogleProvider(client, "gemini-3.6-refuses-zero", "key", null, false, NoDelay);

            var result = await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal("answer", result.Text);
            Assert.Equal(2, handler.Bodies.Count);
            Assert.Equal(0, Budget(handler.Bodies[0]));

            // The retry concedes as little as it can: the smallest budget the model
            // might take, not an open invitation to reason.
            Assert.True(Budget(handler.Bodies[1]) > 0);
        }

        [Fact]
        public async Task OnceItHasRefused_LaterCallsDoNotPayToAskAgain()
        {
            var handler = new RecordingHandler(
                (HttpStatusCode.BadRequest, Refusal),
                (HttpStatusCode.OK, Ok));

            using var client = new HttpClient(handler);
            var provider = new GoogleProvider(client, "gemini-t-once", "key", null, false, NoDelay);

            await provider.CompleteAsync(Request, CancellationToken.None);
            handler.Bodies.Clear();

            await provider.CompleteAsync(Request, CancellationToken.None);

            // One call, and it does not go back to asking for zero. Otherwise every
            // query for the life of the process pays for a failure already known about.
            Assert.Single(handler.Bodies);
            Assert.True(Budget(handler.Bodies[0]) > 0);
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
            var provider = new GoogleProvider(client, "gemini-t-unrelated", "key", null, false, NoDelay);

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
            var provider = new GoogleProvider(client, "gemini-t-thinkingon", "key", null, true, NoDelay);

            await Assert.ThrowsAsync<HttpRequestException>(
                () => provider.CompleteAsync(Request, CancellationToken.None));

            Assert.Single(handler.Bodies);
            Assert.False(SendsBudget(handler.Bodies[0]));
        }

        [Fact]
        public async Task AModelThatRefusesZero_IsOfferedTheSmallestBudgetBeforeGivingUp()
        {
            // The step that was missing. Dropping thinkingConfig entirely is not
            // "thinking off", it is "think as much as you like" — measured on
            // gemini-3.6-flash as 1,178 of 1,445 output tokens and 12.5s a re-rank.
            var handler = new RecordingHandler(
                (HttpStatusCode.BadRequest, Refusal),
                (HttpStatusCode.OK, Ok));

            using var client = new HttpClient(handler);
            var provider = new GoogleProvider(client, "gemini-t-working", "key", null, false, NoDelay);

            await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal(2, handler.Bodies.Count);
            Assert.Equal(0, Budget(handler.Bodies[0]));
            Assert.NotNull(Budget(handler.Bodies[1]));
            Assert.True(Budget(handler.Bodies[1]) > 0, "the retry should ask for the least, not for none of the limit");
        }

        [Fact]
        public async Task OnlyWhenTheSmallestIsAlsoRefused_IsTheFieldDropped()
        {
            var handler = new RecordingHandler(
                (HttpStatusCode.BadRequest, Refusal),
                (HttpStatusCode.BadRequest, Refusal),
                (HttpStatusCode.OK, Ok));

            using var client = new HttpClient(handler);
            var provider = new GoogleProvider(client, "gemini-3.6-flash", "key", null, false, NoDelay);

            await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal(3, handler.Bodies.Count);
            Assert.Equal(0, Budget(handler.Bodies[0]));
            Assert.True(Budget(handler.Bodies[1]) > 0);
            Assert.Null(Budget(handler.Bodies[2]));
        }

        [Fact]
        public async Task AConfiguredBudget_IsSentFirstSoZeroIsNeverOffered()
        {
            // The point of the setting: a model known to reject zero should never be
            // asked for it, so there is no rejected round trip to pay for at all.
            var handler = new RecordingHandler((HttpStatusCode.OK, Ok));

            using var client = new HttpClient(handler);
            var provider = new GoogleProvider(
                client, "gemini-3.6-configured", "key", null, false, NoDelay, configuredThinkingBudget: 512);

            await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Single(handler.Bodies);
            Assert.Equal(512, Budget(handler.Bodies[0]));
        }

        [Fact]
        public async Task AConfiguredBudgetThatIsAlsoRefused_StillFallsAllTheWayBack()
        {
            var handler = new RecordingHandler(
                (HttpStatusCode.BadRequest, Refusal),
                (HttpStatusCode.OK, Ok));

            using var client = new HttpClient(handler);
            var provider = new GoogleProvider(
                client, "gemini-3.6-refuses-512", "key", null, false, NoDelay, configuredThinkingBudget: 512);

            await provider.CompleteAsync(Request, CancellationToken.None);

            Assert.Equal(2, handler.Bodies.Count);
            Assert.Equal(512, Budget(handler.Bodies[0]));
            Assert.Null(Budget(handler.Bodies[1]));
        }

        [Fact]
        public async Task WhatAModelAccepts_SurvivesTheProviderThatLearnedIt()
        {
            // The factory builds a fresh provider for every query, so an instance field
            // learned nothing and the discovery repeated on every single search —
            // two rejected round trips per re-rank, permanently.
            const string model = "gemini-3.6-shared-latch";

            var first = new RecordingHandler(
                (HttpStatusCode.BadRequest, Refusal),
                (HttpStatusCode.OK, Ok));
            using (var clientA = new HttpClient(first))
            {
                await new GoogleProvider(clientA, model, "key", null, false, NoDelay)
                    .CompleteAsync(Request, CancellationToken.None);
            }

            Assert.Equal(2, first.Bodies.Count);

            // A different provider instance, as a later search would get.
            var second = new RecordingHandler((HttpStatusCode.OK, Ok));
            using (var clientB = new HttpClient(second))
            {
                await new GoogleProvider(clientB, model, "key", null, false, NoDelay)
                    .CompleteAsync(Request, CancellationToken.None);
            }

            Assert.Single(second.Bodies);
            Assert.NotEqual(0, Budget(second.Bodies[0]) ?? -1);
        }

        [Fact]
        public async Task ChangingTheConfiguredBudget_DoesNotInheritTheOldRungsMeaning()
        {
            // Observed live. 128 was accepted from the ladder [0, 128, omit]; a budget
            // of 100 was then configured, making it [100, omit]; and a remembered
            // *index* of 1 silently became "omit" — so the new setting was skipped on
            // its first call and the model reasoned without a limit.
            const string model = "gemini-t-rungshift";

            var discover = new RecordingHandler(
                (HttpStatusCode.BadRequest, Refusal),
                (HttpStatusCode.OK, Ok));
            using (var a = new HttpClient(discover))
            {
                await new GoogleProvider(a, model, "key", null, false, NoDelay)
                    .CompleteAsync(Request, CancellationToken.None);
            }

            Assert.Equal(128, Budget(discover.Bodies[1]));

            // Same model, now with a configured budget: the ladder is a different shape.
            var configured = new RecordingHandler((HttpStatusCode.OK, Ok));
            using (var b = new HttpClient(configured))
            {
                await new GoogleProvider(b, model, "key", null, false, NoDelay, configuredThinkingBudget: 100)
                    .CompleteAsync(Request, CancellationToken.None);
            }

            // The configured number is honoured, not skipped for "no limit".
            Assert.Equal(100, Budget(configured.Bodies[0]));
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
