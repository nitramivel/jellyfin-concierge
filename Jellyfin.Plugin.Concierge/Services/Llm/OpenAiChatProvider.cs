using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Concierge.Services.Llm
{
    /// <summary>
    /// Chat Completions provider (POST {base}/chat/completions) covering both the
    /// official OpenAI API and any OpenAI-compatible server (Ollama, LM Studio,
    /// vLLM, OpenRouter). The two differ only in the output-cap parameter name:
    /// the official API uses max_completion_tokens; compatible servers broadly
    /// support the legacy max_tokens.
    /// </summary>
    public sealed class OpenAiChatProvider : ILlmProvider
    {
        /// <summary>The official OpenAI API base, including the version segment.</summary>
        public const string DefaultBaseUrl = "https://api.openai.com/v1";

        /// <summary>xAI's API base. OpenAI-compatible, down to the request shape.</summary>
        public const string GrokBaseUrl = "https://api.x.ai/v1";

        private readonly HttpClient _httpClient;
        private readonly string? _apiKey;
        private readonly Uri _endpoint;
        private readonly bool _useLegacyMaxTokens;
        private readonly bool _useStructuredOutputs;
        private readonly TimeSpan? _initialRetryDelay;
        private readonly string _providerName;
        private readonly bool _useConversationRouting;
        private readonly bool _usePromptCacheKey;
        private readonly string? _reasoningEffort;

        /// <summary>
        /// Set once the endpoint has rejected <c>reasoning_effort</c>, so the penalty
        /// for asking is one failed call rather than one per query.
        /// </summary>
        private volatile bool _reasoningEffortRejected;

        private OpenAiChatProvider(
            HttpClient httpClient,
            string model,
            string? apiKey,
            string baseUrl,
            bool useLegacyMaxTokens,
            bool useStructuredOutputs,
            string providerName,
            TimeSpan? initialRetryDelay,
            bool useConversationRouting = false,
            bool usePromptCacheKey = false,
            string? reasoningEffort = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
            _httpClient = httpClient;
            ModelId = model;
            _apiKey = apiKey;
            _endpoint = new Uri(baseUrl.TrimEnd('/') + "/chat/completions");
            _useLegacyMaxTokens = useLegacyMaxTokens;
            _useStructuredOutputs = useStructuredOutputs;
            _providerName = providerName;
            _initialRetryDelay = initialRetryDelay;
            _useConversationRouting = useConversationRouting;
            _usePromptCacheKey = usePromptCacheKey;
            _reasoningEffort = reasoningEffort;
        }

        /// <inheritdoc />
        public string ModelId { get; }

        /// <summary>
        /// Creates a provider for the official OpenAI API.
        /// </summary>
        /// <param name="httpClient">The HTTP client.</param>
        /// <param name="model">The model identifier.</param>
        /// <param name="apiKey">The API key.</param>
        /// <param name="baseUrl">Optional base URL override, e.g. for a proxy.</param>
        /// <param name="enableThinking">Whether the model may reason before answering.</param>
        /// <returns>The provider.</returns>
        /// <remarks>
        /// <b>Thinking was silently ignored here for the whole life of the plugin.</b>
        /// Anthropic and Google both received the setting and acted on it; this path
        /// took no such argument, so a configuration reading
        /// <c>EnableThinking = false</c> still ran a reasoning model at its default
        /// effort. Measured on the owner's server: 473 reasoning tokens per re-rank
        /// call at the median, 39% of everything generated, on an install that had
        /// turned thinking off. Latency tracks generated tokens at +0.937, so that
        /// was 39% of the wait for something nobody asked for.
        /// </remarks>
        public static OpenAiChatProvider CreateOpenAi(
            HttpClient httpClient,
            string model,
            string apiKey,
            string? baseUrl = null,
            bool enableThinking = true)
        {
            return new OpenAiChatProvider(
                httpClient,
                model,
                apiKey,
                string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl,
                useLegacyMaxTokens: false,
                useStructuredOutputs: true,
                providerName: "OpenAI",
                initialRetryDelay: null,
                usePromptCacheKey: true,

                // Only ever sent to turn reasoning DOWN. Saying nothing leaves the
                // model on its own default, which is the behaviour every existing
                // install already has, so enabling thinking cannot change a request
                // shape that currently works.
                reasoningEffort: enableThinking ? null : "minimal");
        }

        /// <summary>
        /// Creates a provider for xAI's Grok. Same wire format as OpenAI, a different
        /// host — and structured outputs are on, which is the whole reason it gets
        /// its own entry rather than being configured as a generic compatible
        /// endpoint.
        /// </summary>
        /// <param name="httpClient">The HTTP client.</param>
        /// <param name="model">The model identifier, e.g. grok-4.</param>
        /// <param name="apiKey">The xAI API key.</param>
        /// <param name="baseUrl">Optional base URL override, e.g. for a proxy.</param>
        /// <param name="initialRetryDelay">First backoff step; overridden only by tests.</param>
        /// <returns>The provider.</returns>
        public static OpenAiChatProvider CreateGrok(
            HttpClient httpClient,
            string model,
            string apiKey,
            string? baseUrl = null,
            TimeSpan? initialRetryDelay = null)
        {
            return new OpenAiChatProvider(
                httpClient,
                model,
                apiKey,
                string.IsNullOrWhiteSpace(baseUrl) ? GrokBaseUrl : baseUrl,
                useLegacyMaxTokens: false,
                useStructuredOutputs: true,
                providerName: "Grok",
                initialRetryDelay,
                useConversationRouting: true);
        }

        /// <summary>
        /// Creates a provider for a generic OpenAI-compatible endpoint. A base URL is
        /// required (e.g. "http://localhost:11434/v1" for Ollama); the API key is
        /// optional since local servers often need none.
        /// </summary>
        /// <param name="httpClient">The HTTP client.</param>
        /// <param name="model">The model identifier.</param>
        /// <param name="baseUrl">The endpoint base, including the version segment.</param>
        /// <param name="apiKey">Optional API key.</param>
        /// <returns>The provider.</returns>
        public static OpenAiChatProvider CreateCompatible(HttpClient httpClient, string model, string baseUrl, string? apiKey = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

            // Structured outputs stay OFF here. This path exists for Ollama, LM
            // Studio, vLLM and anything else speaking the dialect, and a server that
            // does not understand response_format rejects the whole request rather
            // than ignoring the field. Support varies by server AND by model, so it
            // is the owner's job to verify before trusting a schema on this path.
            return new OpenAiChatProvider(
                httpClient,
                model,
                apiKey,
                baseUrl,
                useLegacyMaxTokens: true,
                useStructuredOutputs: false,
                providerName: "Chat completions endpoint",
                initialRetryDelay: null);
        }

        /// <inheritdoc />
        public async Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            try
            {
                return await SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
                when (_reasoningEffort is not null
                    && !_reasoningEffortRejected
                    && MentionsReasoningEffort(ex.Message))
            {
                // This model does not take the parameter. Remember that, so the cost
                // of having asked is one failed call rather than one per query, and
                // answer the request the way we always did — slower, and correct.
                _reasoningEffortRejected = true;

                return await SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Whether a failure is the endpoint refusing <c>reasoning_effort</c>.
        /// </summary>
        /// <remarks>
        /// Deliberately narrow. A 400 has many causes and most of them are ours; only
        /// one names this field, and retrying anything else without it would turn a
        /// real error into two real errors.
        /// </remarks>
        private static bool MentionsReasoningEffort(string message)
        {
            return message.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<LlmResult> SendAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            var payload = BuildRequestBody(request);

            var body = await TransientHttpRetry.SendAsync(
                _httpClient,
                () =>
                {
                    var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                    if (!string.IsNullOrEmpty(_apiKey))
                    {
                        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                    }

                    // xAI stores cache entries PER SERVER, so without a routing hint
                    // the calls of one run scatter across the fleet and each lands on
                    // a machine that has never seen the prefix. Measured in Curator
                    // before this header: 16 of 18 calls reported 128 cached tokens
                    // against a ~28k identical prefix. Grouping a run under one id
                    // pins its calls to one server.
                    if (_useConversationRouting && !string.IsNullOrEmpty(request.ConversationId))
                    {
                        message.Headers.Add("x-grok-conv-id", request.ConversationId);
                    }

                    message.Content = JsonContent.Create(payload);
                    return message;
                },
                (status, failure) => $"{_providerName} returned {(int)status}: {Truncate(failure)}",
                _initialRetryDelay,
                cancellationToken).ConfigureAwait(false);

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var text = string.Empty;
            string? finishReason = null;
            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("message", out var responseMessage)
                    && responseMessage.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String)
                {
                    text = content.GetString() ?? string.Empty;
                }

                if (choice.TryGetProperty("finish_reason", out var finish))
                {
                    finishReason = finish.GetString();
                }
            }

            long promptTokens = 0;
            long outputTokens = 0;
            long cachedTokens = 0;
            long reasoningTokens = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var prompt))
                {
                    promptTokens = prompt.GetInt64();
                }

                if (usage.TryGetProperty("completion_tokens", out var completion))
                {
                    outputTokens = completion.GetInt64();
                }

                // Optional detail blocks. Absent on plain OpenAI-compatible servers,
                // present on OpenAI and xAI.
                if (usage.TryGetProperty("prompt_tokens_details", out var promptDetails)
                    && promptDetails.TryGetProperty("cached_tokens", out var cached))
                {
                    cachedTokens = cached.GetInt64();
                }

                if (usage.TryGetProperty("completion_tokens_details", out var completionDetails)
                    && completionDetails.TryGetProperty("reasoning_tokens", out var reasoning))
                {
                    reasoningTokens = reasoning.GetInt64();
                }
            }

            // prompt_tokens is the TOTAL input including anything served from cache —
            // the same convention as Gemini and the opposite of Anthropic. The cached
            // span comes off so a cache hit does not read as more input. Hard rule 10
            // exists because getting this backwards understates a query by ~25%.
            return new LlmResult(
                text,
                Math.Max(0, promptTokens - cachedTokens),
                outputTokens,
                finishReason == "length",
                CacheWriteTokens: 0,
                CacheReadTokens: cachedTokens,
                ThinkingTokens: reasoningTokens);
        }

        private object BuildRequestBody(LlmRequest request)
        {
            var messages = new[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt },
            };

            var body = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model"] = ModelId,
                ["messages"] = messages,
            };

            body[_useLegacyMaxTokens && !_useStructuredOutputs ? "max_tokens" : "max_completion_tokens"]
                = request.MaxOutputTokens;

            // OpenAI's answer to the same problem xAI's header solves: cache entries
            // are held per server, and without a routing hint the calls of one run
            // scatter and each lands somewhere that has never seen the prefix.
            //
            // OpenAI only. xAI uses the header above, and this class also drives
            // arbitrary OpenAI-compatible servers, which are entitled to reject a
            // body field they have never heard of.
            if (_usePromptCacheKey && !string.IsNullOrEmpty(request.ConversationId))
            {
                body["prompt_cache_key"] = request.ConversationId;
            }

            if (_reasoningEffort is not null && !_reasoningEffortRejected)
            {
                body["reasoning_effort"] = _reasoningEffort;
            }

            var schema = BuildResponseSchema(request.Shape);
            if (_useStructuredOutputs && schema is not null)
            {
                body["response_format"] = new
                {
                    type = "json_schema",
                    json_schema = new
                    {
                        name = SchemaName(request.Shape),
                        strict = true,
                        schema,
                    },
                };
            }

            return body;
        }

        /// <summary>
        /// Translates the response contract into JSON Schema as OpenAI's strict mode
        /// wants it: lowercase types, <c>additionalProperties: false</c> on every
        /// object, and every property listed in <c>required</c>.
        /// </summary>
        /// <remarks>
        /// Deliberately a separate builder from the Gemini one. The two dialects look
        /// alike and are not: Gemini wants uppercase type names and rejects nothing
        /// for extra keys, while strict mode here refuses a schema that omits any
        /// property from <c>required</c>. Sharing one builder would mean one of them
        /// being subtly wrong.
        /// <para>
        /// A null schema sends no <c>response_format</c> at all, which is the correct
        /// request for a prompt that never asked for JSON.
        /// </para>
        /// </remarks>
        private static object? BuildResponseSchema(ResponseShape shape) => shape switch
        {
            ResponseShape.Enrichment => BuildEnrichmentSchema(),
            ResponseShape.SearchPlan => BuildSearchPlanSchema(),
            ResponseShape.Rerank => BuildRerankSchema(),
            _ => null,
        };

        private static string SchemaName(ResponseShape shape) => shape switch
        {
            ResponseShape.Enrichment => "concierge_enrichment",
            ResponseShape.SearchPlan => "concierge_search_plan",
            ResponseShape.Rerank => "concierge_rerank",
            _ => "concierge_response",
        };

        /// <summary>
        /// The plan contract in OpenAI strict-mode dialect.
        /// </summary>
        /// <remarks>
        /// The nullable fields are declared <c>["integer","null"]</c> rather than
        /// omitted, because strict mode requires every property in <c>required</c> —
        /// and "no year was mentioned" has to be expressible. Without an explicit null
        /// the model must invent a year to satisfy the schema, which is exactly the
        /// wrong filter the prompt spends its length warning against.
        /// </remarks>
        private static object BuildSearchPlanSchema()
        {
            var strings = new { type = "array", items = new { type = "string" } };
            var nullableInteger = new { type = new[] { "integer", "null" } };

            var filters = new
            {
                type = "object",
                properties = new
                {
                    types = strings,
                    yearFrom = nullableInteger,
                    yearTo = nullableInteger,
                    genres = strings,
                    people = strings,
                    runtimeMaxMinutes = nullableInteger,
                    watchState = new
                    {
                        type = "string",
                        description = "any, unwatched, watched or favorite.",
                    },
                },
                required = new[]
                {
                    "types", "yearFrom", "yearTo", "genres", "people", "runtimeMaxMinutes", "watchState",
                },
                additionalProperties = false,
            };

            return new
            {
                type = "object",
                properties = new
                {
                    semantic = new
                    {
                        type = "string",
                        description = "What they are describing, with constraint words removed. Never empty.",
                    },
                    filters,
                    quote = new
                    {
                        type = new[] { "string", "null" },
                        description = "Dialogue they are reciting, or null.",
                    },
                },
                required = new[] { "semantic", "filters", "quote" },
                additionalProperties = false,
            };
        }

        /// <summary>
        /// The re-rank contract in OpenAI strict-mode dialect.
        /// </summary>
        private static object BuildRerankSchema()
        {
            var entry = new
            {
                type = "object",
                properties = new
                {
                    i = new { type = "integer", description = "A number from the shortlist, used once." },
                    why = new
                    {
                        type = "string",
                        description = "One clause under twelve words. Never a twist or an ending.",
                    },
                },
                required = new[] { "i", "why" },
                additionalProperties = false,
            };

            return new
            {
                type = "object",
                properties = new
                {
                    order = new
                    {
                        type = "array",
                        description = "Every shortlist number, once each, best first.",
                        items = entry,
                    },
                },
                required = new[] { "order" },
                additionalProperties = false,
            };
        }

        /// <summary>
        /// The enrichment contract in OpenAI strict-mode dialect.
        /// </summary>
        /// <remarks>
        /// Every declared property appears in <c>required</c> because strict mode
        /// refuses a schema that omits one — including <c>known</c>, which is the
        /// field that lets the model decline. An item it has never heard of comes
        /// back as <c>known: false</c> with empty lists, and the parser stores that
        /// rather than a plausible invention (hard rule 14).
        /// </remarks>
        private static object BuildEnrichmentSchema()
        {
            var strings = new { type = "array", items = new { type = "string" } };

            var item = new
            {
                type = "object",
                properties = new
                {
                    i = new { type = "integer", description = "The item's index from the list above." },
                    known = new { type = "boolean", description = "False if you do not genuinely know this title." },
                    premise = new { type = "string", description = "What actually happens. Empty if not known." },
                    moments = strings,
                    themes = strings,
                    asks = strings,
                    spoiler = new { type = "boolean", description = "Whether the above gives away a twist." },
                },
                required = new[] { "i", "known", "premise", "moments", "themes", "asks", "spoiler" },
                additionalProperties = false,
            };

            return new
            {
                type = "object",
                properties = new { items = new { type = "array", items = item } },
                required = new[] { "items" },
                additionalProperties = false,
            };
        }

        private static string Truncate(string body)
        {
            return body.Length <= 500 ? body : body[..500] + "…";
        }
    }
}
