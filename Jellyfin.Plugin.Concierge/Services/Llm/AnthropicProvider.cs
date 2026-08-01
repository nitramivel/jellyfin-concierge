using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Concierge.Services.Llm
{
    /// <summary>
    /// Anthropic Messages API provider (POST {base}/messages).
    /// </summary>
    public sealed class AnthropicProvider : ILlmProvider, IBatchLlmProvider
    {
        /// <summary>The default API base, including the version segment.</summary>
        public const string DefaultBaseUrl = "https://api.anthropic.com/v1";

        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly Uri _endpoint;
        private readonly Uri _batchEndpoint;
        private readonly bool _enableThinking;

        public AnthropicProvider(
            HttpClient httpClient,
            string model,
            string apiKey,
            string? baseUrl = null,
            bool enableThinking = false)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
            _httpClient = httpClient;
            ModelId = model;
            _apiKey = apiKey;
            _enableThinking = enableThinking;
            var basePart = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.TrimEnd('/');
            _endpoint = new Uri(basePart + "/messages");
            _batchEndpoint = new Uri(basePart + "/messages/batches");
        }

        /// <inheritdoc />
        public string ModelId { get; }

        /// <inheritdoc />
        public async Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            AddAuthHeaders(message);
            message.Content = JsonContent.Create(BuildMessageParams(request));

            using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Anthropic API returned {(int)response.StatusCode}: {Truncate(body)}");
            }

            using var document = JsonDocument.Parse(body);
            return ParseMessage(document.RootElement);
        }

        /// <summary>
        /// Turns one Messages API response object into a result. Shared by the direct
        /// and batch paths — a batch result embeds exactly this shape under
        /// <c>result.message</c>.
        /// </summary>
        private static LlmResult ParseMessage(JsonElement root)
        {
            var stopReason = root.TryGetProperty("stop_reason", out var stop) ? stop.GetString() : null;
            if (stopReason == "refusal")
            {
                throw new InvalidOperationException("Anthropic declined the request (stop_reason: refusal).");
            }

            var text = string.Empty;
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var type)
                        && type.ValueEquals("text")
                        && block.TryGetProperty("text", out var textElement))
                    {
                        text += textElement.GetString();
                    }
                }
            }

            long inputTokens = 0;
            long outputTokens = 0;
            long cacheWrite = 0;
            long cacheRead = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("input_tokens", out var input))
                {
                    inputTokens = input.GetInt64();
                }

                if (usage.TryGetProperty("output_tokens", out var output))
                {
                    outputTokens = output.GetInt64();
                }

                // input_tokens counts only the uncached remainder; the cached span is
                // reported separately and billed at a different rate.
                if (usage.TryGetProperty("cache_creation_input_tokens", out var written))
                {
                    cacheWrite = written.GetInt64();
                }

                if (usage.TryGetProperty("cache_read_input_tokens", out var read))
                {
                    cacheRead = read.GetInt64();
                }
            }

            return new LlmResult(
                text,
                inputTokens,
                outputTokens,
                stopReason == "max_tokens",
                cacheWrite,
                cacheRead);
        }

        /// <summary>
        /// Builds the Messages API request body. Used verbatim as a batch request's
        /// <c>params</c>, so the two paths cannot drift apart.
        /// </summary>
        /// <remarks>
        /// Three things here are load-bearing and were all true as of July 2026:
        /// <list type="bullet">
        /// <item><description>
        /// <c>thinking</c> is always sent explicitly. Omitting it on Opus 5 runs
        /// adaptive thinking — unlike Opus 4.8/4.7, where omitting it meant none —
        /// which is a silent latency and cost regression inside a 2.5-second search
        /// budget. Note that disabling is only legal at effort <c>high</c> or below.
        /// </description></item>
        /// <item><description>
        /// It maps to adaptive-or-disabled and never to a token budget:
        /// <c>budget_tokens</c> is gone.
        /// </description></item>
        /// <item><description>
        /// No <c>temperature</c>, <c>top_p</c> or <c>top_k</c> is sent at all. The
        /// current models reject them outright. Steer with the prompt instead.
        /// </description></item>
        /// </list>
        /// </remarks>
        private object BuildMessageParams(LlmRequest request)
        {
            return new
            {
                model = ModelId,
                max_tokens = request.MaxOutputTokens,
                // max_tokens caps thinking AND visible text together, so a tight cap
                // with thinking on truncates the JSON before it closes.
                thinking = _enableThinking
                    ? new { type = "adaptive" }
                    : new { type = "disabled" },
                system = request.SystemPrompt,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = BuildUserContent(request),
                    },
                },
            };
        }

        /// <summary>
        /// Splits the user prompt into two content blocks and marks the first as
        /// cacheable.
        /// </summary>
        /// <remarks>
        /// The 1-hour TTL costs 2x on the write instead of 1.25x, but the default
        /// 5-minute window is too short to survive the gap between the calls of a run.
        /// <para>
        /// <b>An empty block is never sent.</b> Anthropic rejects the whole request
        /// with "text content blocks must be non-empty", and a pass that puts its
        /// whole prompt in the prefix hands this an empty suffix by design. In
        /// Curator that exact hole 400'd every batch of a 195-item pass and distilled
        /// nothing, on a model that had simply never been used for that pass before.
        /// </para>
        /// <para>
        /// The cache marker goes on only when there is <em>also</em> a suffix. The
        /// split exists to mark the part that repeats across calls, so a caller that
        /// puts everything in the prefix is telling us there is nothing to reuse —
        /// and marking it anyway would pay the write premium on every call of a pass
        /// whose prompt is different every time.
        /// </para>
        /// </remarks>
        private static object[] BuildUserContent(LlmRequest request)
        {
            var hasPrefix = !string.IsNullOrEmpty(request.CacheablePrefix);
            var hasSuffix = !string.IsNullOrEmpty(request.VariableSuffix);

            if (!hasPrefix && !hasSuffix)
            {
                throw new InvalidOperationException(
                    "Concierge: an LLM request must carry some user prompt; both the cacheable prefix and the variable suffix are empty.");
            }

            var blocks = new List<object>(2);

            if (hasPrefix)
            {
                blocks.Add(hasSuffix
                    ? new
                    {
                        type = "text",
                        text = request.CacheablePrefix,
                        cache_control = new { type = "ephemeral", ttl = "1h" },
                    }
                    : (object)new { type = "text", text = request.CacheablePrefix });
            }

            if (hasSuffix)
            {
                blocks.Add(new { type = "text", text = request.VariableSuffix });
            }

            return [.. blocks];
        }

        /// <inheritdoc />
        /// <remarks>
        /// Submits one job, polls until it ends, then reads the results file. Requests
        /// inside a job run in parallel and finish in arbitrary order, so results are
        /// keyed by custom id and never by position.
        /// </remarks>
        public async Task<IReadOnlyList<BatchLlmResult>> CompleteBatchAsync(
            IReadOnlyList<BatchLlmRequest> requests,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(requests);
            if (requests.Count == 0)
            {
                return [];
            }

            var batchId = await SubmitBatchAsync(requests, cancellationToken).ConfigureAwait(false);
            var resultsUrl = await AwaitBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
            return await ReadBatchResultsAsync(resultsUrl, cancellationToken).ConfigureAwait(false);
        }

        private async Task<string> SubmitBatchAsync(
            IReadOnlyList<BatchLlmRequest> requests,
            CancellationToken cancellationToken)
        {
            var payload = new
            {
                requests = requests
                    .Select(r => new { custom_id = r.CustomId, @params = BuildMessageParams(r.Request) })
                    .ToArray(),
            };

            using var message = new HttpRequestMessage(HttpMethod.Post, _batchEndpoint);
            AddAuthHeaders(message);
            message.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Anthropic batch submit returned {(int)response.StatusCode}: {Truncate(body)}");
            }

            using var document = JsonDocument.Parse(body);
            return document.RootElement.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("Anthropic batch submit returned no batch id.");
        }

        private async Task<string> AwaitBatchAsync(string batchId, CancellationToken cancellationToken)
        {
            var pollUri = new Uri(_batchEndpoint + "/" + batchId);
            var delay = TimeSpan.FromSeconds(5);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var message = new HttpRequestMessage(HttpMethod.Get, pollUri);
                AddAuthHeaders(message);

                using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Anthropic batch poll returned {(int)response.StatusCode}: {Truncate(body)}");
                }

                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                var status = root.TryGetProperty("processing_status", out var s) ? s.GetString() : null;
                if (status == "ended")
                {
                    return root.TryGetProperty("results_url", out var url) && url.GetString() is { } resultsUrl
                        ? resultsUrl
                        : throw new InvalidOperationException("Anthropic batch ended without a results URL.");
                }

                if (status == "canceled" || status == "expired")
                {
                    throw new InvalidOperationException($"Anthropic batch {batchId} {status} before completing.");
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                // Back off to a minute: jobs usually land within the hour, and there is
                // no value in hammering the poll endpoint for the whole of it.
                if (delay < TimeSpan.FromMinutes(1))
                {
                    delay += TimeSpan.FromSeconds(5);
                }
            }
        }

        private async Task<IReadOnlyList<BatchLlmResult>> ReadBatchResultsAsync(
            string resultsUrl,
            CancellationToken cancellationToken)
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(resultsUrl));
            AddAuthHeaders(message);

            using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Anthropic batch results returned {(int)response.StatusCode}: {Truncate(body)}");
            }

            var results = new List<BatchLlmResult>();

            // The results body is JSONL — one result object per line.
            foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var customId = root.TryGetProperty("custom_id", out var id) ? id.GetString() : null;
                if (customId is null)
                {
                    continue;
                }

                if (!root.TryGetProperty("result", out var result))
                {
                    results.Add(new BatchLlmResult(customId, null, "result missing from batch entry"));
                    continue;
                }

                var type = result.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type != "succeeded" || !result.TryGetProperty("message", out var messageElement))
                {
                    results.Add(new BatchLlmResult(customId, null, type ?? "unknown"));
                    continue;
                }

                try
                {
                    results.Add(new BatchLlmResult(customId, ParseMessage(messageElement), null));
                }
                catch (InvalidOperationException ex)
                {
                    // A refusal on one request must not sink the whole job.
                    results.Add(new BatchLlmResult(customId, null, ex.Message));
                }
            }

            return results;
        }

        private void AddAuthHeaders(HttpRequestMessage message)
        {
            message.Headers.Add("x-api-key", _apiKey);
            message.Headers.Add("anthropic-version", "2023-06-01");
        }

        private static string Truncate(string body)
        {
            return body.Length <= 500 ? body : body[..500] + "…";
        }
    }
}
