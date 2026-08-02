using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Concierge.Configuration;
using Jellyfin.Plugin.Concierge.Core.Llm;
using Jellyfin.Plugin.Concierge.Services.Llm;
using Jellyfin.Plugin.Concierge.Services;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// How many results a search returns.
    /// </summary>
    public class ResultCountTests
    {
        private static PluginConfiguration Config(int maxResults = 20)
            => new() { MaxResults = maxResults };

        [Fact]
        public void WithoutARerankTheConfiguredMaximumStands()
        {
            // No model opinion means nothing to honour. The fused order is a ranking,
            // not a judgement about where the good answers stop.
            Assert.Equal(20, SearchService.HowManyToShow(Config(), rankedByModel: 0));
        }

        [Fact]
        public void TheModelsCountIsTheAnswerWhenItGaveOne()
        {
            Assert.Equal(9, SearchService.HowManyToShow(Config(), rankedByModel: 9));
            Assert.Equal(4, SearchService.HowManyToShow(Config(), rankedByModel: 4));
        }

        [Fact]
        public void ADegenerateAnswerStillLeavesSomethingBesideIt()
        {
            Assert.Equal(3, SearchService.HowManyToShow(Config(), rankedByModel: 1));
        }

        [Fact]
        public void TheCallersCeilingIsNeverExceeded()
        {
            Assert.Equal(12, SearchService.HowManyToShow(Config(maxResults: 12), rankedByModel: 40));

            // …including when the ceiling is below the floor, which a caller asking
            // for two results is entitled to do.
            Assert.Equal(2, SearchService.HowManyToShow(Config(maxResults: 2), rankedByModel: 1));
        }
    }

    /// <summary>
    /// Thinking, on the provider that was quietly ignoring it.
    /// </summary>
    /// <remarks>
    /// No live calls — hard rule 5. These drive a stub handler and read the request
    /// body that would have gone out.
    /// </remarks>
    public class OpenAiThinkingTests
    {
        private static LlmRequest Request()
            => new("system", "user", string.Empty, 1000, ResponseShape.Rerank);

        [Fact]
        public async Task ThinkingOffAsksTheModelToStopReasoning()
        {
            var handler = new RecordingHandler();
            var provider = OpenAiChatProvider.CreateOpenAi(
                new HttpClient(handler), "gpt-5.6-luna", "k", enableThinking: false);

            await provider.CompleteAsync(Request(), CancellationToken.None);

            Assert.Equal("minimal", handler.Bodies[0].RootElement
                .GetProperty("reasoning_effort").GetString());
        }

        /// <summary>
        /// Thinking on must not change a request shape that already works.
        /// </summary>
        [Fact]
        public async Task ThinkingOnSaysNothingAtAll()
        {
            var handler = new RecordingHandler();
            var provider = OpenAiChatProvider.CreateOpenAi(
                new HttpClient(handler), "gpt-5.6-luna", "k", enableThinking: true);

            await provider.CompleteAsync(Request(), CancellationToken.None);

            Assert.False(handler.Bodies[0].RootElement.TryGetProperty("reasoning_effort", out _));
        }

        /// <summary>
        /// A model that will not take the parameter gets its answer anyway.
        /// </summary>
        /// <remarks>
        /// Every paid path degrades free, and this one degrades to exactly today's
        /// behaviour: the same request without the field. The alternative — shipping
        /// a parameter that 400s on an untested model — is a dead search box.
        /// </remarks>
        [Fact]
        public async Task AModelThatRejectsTheParameterIsAskedAgainWithoutIt()
        {
            var handler = new RecordingHandler
            {
                RejectReasoningEffort = "Unrecognized request argument supplied: reasoning_effort",
            };

            var provider = OpenAiChatProvider.CreateOpenAi(
                new HttpClient(handler), "some-older-model", "k", enableThinking: false);

            var result = await provider.CompleteAsync(Request(), CancellationToken.None);

            Assert.Equal(2, handler.Bodies.Count);
            Assert.True(handler.Bodies[0].RootElement.TryGetProperty("reasoning_effort", out _));
            Assert.False(handler.Bodies[1].RootElement.TryGetProperty("reasoning_effort", out _));
            Assert.NotNull(result);
        }

        [Fact]
        public async Task ItOnlyEverAsksOnce()
        {
            var handler = new RecordingHandler
            {
                RejectReasoningEffort = "Unrecognized request argument supplied: reasoning_effort",
            };

            var provider = OpenAiChatProvider.CreateOpenAi(
                new HttpClient(handler), "some-older-model", "k", enableThinking: false);

            await provider.CompleteAsync(Request(), CancellationToken.None);
            await provider.CompleteAsync(Request(), CancellationToken.None);

            // Three calls, not four: the second query never asks again.
            Assert.Equal(3, handler.Bodies.Count);
            Assert.False(handler.Bodies[2].RootElement.TryGetProperty("reasoning_effort", out _));
        }

        /// <summary>
        /// A 400 that is genuinely ours must not be retried into two failures.
        /// </summary>
        [Fact]
        public async Task AnUnrelatedFailureIsNotRetried()
        {
            var handler = new RecordingHandler { FailWith = "context_length_exceeded" };

            var provider = OpenAiChatProvider.CreateOpenAi(
                new HttpClient(handler), "gpt-5.6-luna", "k", enableThinking: false);

            await Assert.ThrowsAsync<HttpRequestException>(
                () => provider.CompleteAsync(Request(), CancellationToken.None));

            Assert.Single(handler.Bodies);
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            public List<JsonDocument> Bodies { get; } = [];

            public string? RejectReasoningEffort { get; init; }

            public string? FailWith { get; init; }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var text = await request.Content!.ReadAsStringAsync(cancellationToken);
                var body = JsonDocument.Parse(text);
                Bodies.Add(body);

                if (FailWith is not null)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent(FailWith),
                    };
                }

                if (RejectReasoningEffort is not null
                    && body.RootElement.TryGetProperty("reasoning_effort", out _))
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent(RejectReasoningEffort),
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {"choices":[{"message":{"content":"{\"order\":[]}"},"finish_reason":"stop"}],
                         "usage":{"prompt_tokens":1,"completion_tokens":1}}
                        """),
                };
            }
        }
    }
}
