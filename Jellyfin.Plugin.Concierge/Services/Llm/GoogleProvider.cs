using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Concierge.Services.Llm
{
    /// <summary>
    /// Google Gemini provider (POST {base}/models/{model}:generateContent).
    /// </summary>
    /// <remarks>
    /// Gemini is also reachable through the OpenAI-compatible provider, but that
    /// path gives up the one thing worth having here: <c>responseSchema</c>. The
    /// model is constrained to the exact object the parser expects, so the failure
    /// mode that costs whole passes — a stray quote or a trailing sentence making
    /// the JSON unparseable — cannot occur. Prefer this over the compatibility
    /// endpoint.
    /// </remarks>
    public sealed class GoogleProvider : ILlmProvider
    {
        /// <summary>The default API base, including the version segment.</summary>
        public const string DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta";

        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly Uri _endpoint;
        private readonly bool _enableThinking;
        private readonly TimeSpan _initialRetryDelay;

        /// <param name="httpClient">The HTTP client.</param>
        /// <param name="model">The model identifier.</param>
        /// <param name="apiKey">The API key.</param>
        /// <param name="baseUrl">Optional base URL override.</param>
        /// <param name="enableThinking">Whether the model may think before answering.</param>
        /// <param name="initialRetryDelay">
        /// First backoff step for transient failures. Overridden only by tests, so
        /// exercising the retry path does not cost them the real five seconds.
        /// </param>
        public GoogleProvider(
            HttpClient httpClient,
            string model,
            string apiKey,
            string? baseUrl = null,
            bool enableThinking = false,
            TimeSpan? initialRetryDelay = null)
        {
            _initialRetryDelay = initialRetryDelay ?? TransientHttpRetry.DefaultInitialDelay;
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
            _httpClient = httpClient;

            // Google's own docs write model ids both ways ("gemini-2.5-flash" and
            // "models/gemini-2.5-flash"); the path segment already supplies the
            // prefix, so accept either rather than 404 on the second.
            ModelId = model.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                ? model["models/".Length..]
                : model;

            _apiKey = apiKey;
            _enableThinking = enableThinking;
            var basePart = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.TrimEnd('/');
            _endpoint = new Uri(basePart + "/models/" + ModelId + ":generateContent");
        }

        /// <inheritdoc />
        public string ModelId { get; }

        /// <inheritdoc />
        public async Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            var body = await SendWithRetriesAsync(request, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            return ParseResponse(document.RootElement);
        }

        /// <summary>
        /// Posts the request, retrying the transient failures Gemini produces in
        /// normal use. See <see cref="TransientHttpRetry"/> for the line between
        /// transient and permanent.
        /// </summary>
        private Task<string> SendWithRetriesAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            var payload = BuildRequestBody(request);

            return TransientHttpRetry.SendAsync(
                _httpClient,
                () =>
                {
                    var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                    message.Headers.Add("x-goog-api-key", _apiKey);
                    message.Content = JsonContent.Create(payload);
                    return message;
                },
                (status, body) => $"Google API returned {(int)status}: {Truncate(body)}",
                _initialRetryDelay,
                cancellationToken);
        }

        private object BuildRequestBody(LlmRequest request)
        {
            return new
            {
                systemInstruction = new
                {
                    parts = new[] { new { text = request.SystemPrompt } },
                },

                // Two parts rather than one concatenated string: it keeps the reusable
                // portion at a stable prefix boundary, which is what Gemini's implicit
                // context caching keys on.
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = BuildParts(request),
                    },
                },
                safetySettings = SafetySettings,
                generationConfig = BuildGenerationConfig(request),
            };
        }

        /// <summary>
        /// Turns the content filters off for every category Gemini lets us set.
        /// </summary>
        /// <remarks>
        /// Unlike the other providers, Gemini applies safety blocking by default, and
        /// it applies it to <em>our</em> input: the prompt is a list of the user's own
        /// films and series with their synopses, and later their subtitles. A library
        /// containing horror, true crime, war films or anything with an adult
        /// certificate will trip a filter sooner or later, and when it does the whole
        /// call comes back with no candidate.
        /// <para>
        /// Nothing here is generative in the risky sense: the model is reading and
        /// ordering media the user already owns and has chosen to catalogue. Blocking
        /// it protects nobody and only makes search unreliable in proportion to how
        /// interesting the library is.
        /// </para>
        /// </remarks>
        private static readonly object[] SafetySettings =
        [
            new { category = "HARM_CATEGORY_HARASSMENT", threshold = "OFF" },
            new { category = "HARM_CATEGORY_HATE_SPEECH", threshold = "OFF" },
            new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "OFF" },
            new { category = "HARM_CATEGORY_DANGEROUS_CONTENT", threshold = "OFF" },
        ];

        /// <summary>
        /// Splits the user prompt into parts, skipping any that are empty.
        /// </summary>
        /// <remarks>
        /// Same rule as the Anthropic builder, for the same reason: a pass that puts
        /// its whole prompt in the prefix hands this an empty suffix by design, and an
        /// empty part is at best wasted and at worst rejected.
        /// </remarks>
        private static object[] BuildParts(LlmRequest request)
        {
            var hasPrefix = !string.IsNullOrEmpty(request.CacheablePrefix);
            var hasSuffix = !string.IsNullOrEmpty(request.VariableSuffix);

            if (!hasPrefix && !hasSuffix)
            {
                throw new InvalidOperationException(
                    "Concierge: an LLM request must carry some user prompt; both the cacheable prefix and the variable suffix are empty.");
            }

            var parts = new List<object>(2);

            if (hasPrefix)
            {
                parts.Add(new { text = request.CacheablePrefix });
            }

            if (hasSuffix)
            {
                parts.Add(new { text = request.VariableSuffix });
            }

            return [.. parts];
        }

        private object BuildGenerationConfig(LlmRequest request)
        {
            var config = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["maxOutputTokens"] = request.MaxOutputTokens,
            };

            // A shape of None means no pass has asked to be constrained, so the
            // response stays free text. Forcing application/json without a schema
            // would demand JSON from a prompt that never asked for it.
            var schema = BuildResponseSchema(request.Shape);
            if (schema is not null)
            {
                config["responseMimeType"] = "application/json";
                config["responseSchema"] = schema;
            }

            // Thinking is left at the model's own default when enabled rather than
            // pinned to a budget — Gemini sizes it dynamically, and the plugin's
            // setting is a yes/no. When disabled, budget 0 turns it off; note the Pro
            // models refuse a zero budget and return 400, which is the API saying the
            // same thing the plan does: leave it on for those.
            if (!_enableThinking)
            {
                config["thinkingConfig"] = new { thinkingBudget = 0 };
            }

            return config;
        }

        /// <summary>
        /// Translates the response contract into Gemini's schema dialect: an OpenAPI
        /// subset whose type names are the uppercase proto enum values, with the
        /// non-standard <c>propertyOrdering</c> fixing generation order.
        /// </summary>
        /// <remarks>
        /// Deliberately a separate builder from the OpenAI one. The two dialects look
        /// alike and are not: Gemini wants uppercase type names, rejects nothing for
        /// extra keys, and needs <c>propertyOrdering</c> that strict mode has never
        /// heard of. Sharing one builder would mean one of them being subtly wrong.
        /// <para>
        /// Whatever is added here needs its counterpart in the OpenAI builder, and
        /// both must stay in step with the parser and with what the prompt describes.
        /// </para>
        /// </remarks>
        private static object? BuildResponseSchema(ResponseShape shape) => shape switch
        {
            ResponseShape.Enrichment => BuildEnrichmentSchema(),
            ResponseShape.SearchPlan => BuildSearchPlanSchema(),
            ResponseShape.Rerank => BuildRerankSchema(),
            _ => null,
        };

        /// <summary>
        /// The plan contract in Gemini's dialect.
        /// </summary>
        /// <remarks>
        /// Gemini spells optionality as <c>nullable</c> on the field rather than as a
        /// union type, which is the single most likely thing to be copied wrongly from
        /// the OpenAI builder next door. Getting it wrong forces the model to invent a
        /// year rather than say there was not one.
        /// </remarks>
        private static object BuildSearchPlanSchema()
        {
            var strings = new { type = "ARRAY", items = new { type = "STRING" } };
            var nullableInteger = new { type = "INTEGER", nullable = true };

            var filters = new
            {
                type = "OBJECT",
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
                        type = "STRING",
                        description = "any, unwatched, watched or favorite.",
                    },
                },
                required = new[] { "types", "genres", "people", "watchState" },
                propertyOrdering = new[]
                {
                    "types", "yearFrom", "yearTo", "genres", "people", "runtimeMaxMinutes", "watchState",
                },
            };

            return new
            {
                type = "OBJECT",
                properties = new
                {
                    semantic = new
                    {
                        type = "STRING",
                        description = "What they are describing, with constraint words removed. Never empty.",
                    },
                    filters,
                    quote = new
                    {
                        type = "STRING",
                        nullable = true,
                        description = "Dialogue they are reciting, or null.",
                    },
                },
                required = new[] { "semantic", "filters" },
                propertyOrdering = new[] { "semantic", "filters", "quote" },
            };
        }

        /// <summary>
        /// The re-rank contract in Gemini's dialect.
        /// </summary>
        /// <remarks>
        /// <c>propertyOrdering</c> puts the index before the reason, so the model
        /// commits to a number before writing prose about it. A model that writes the
        /// justification first has to hold the index in mind across the whole clause,
        /// and that is where off-by-one answers come from.
        /// </remarks>
        private static object BuildRerankSchema()
        {
            var entry = new
            {
                type = "OBJECT",
                properties = new
                {
                    i = new { type = "INTEGER", description = "A number from the shortlist, used once." },
                    why = new
                    {
                        type = "STRING",
                        description = "One clause under twelve words. Never a twist or an ending.",
                    },
                },
                required = new[] { "i", "why" },
                propertyOrdering = new[] { "i", "why" },
            };

            return new
            {
                type = "OBJECT",
                properties = new
                {
                    order = new
                    {
                        type = "ARRAY",
                        description = "Every shortlist number, once each, best first.",
                        items = entry,
                    },
                },
                required = new[] { "order" },
                propertyOrdering = new[] { "order" },
            };
        }

        /// <summary>
        /// The enrichment contract in Gemini's dialect.
        /// </summary>
        /// <remarks>
        /// Uppercase type names, no <c>additionalProperties</c>, and
        /// <c>propertyOrdering</c> so <c>i</c> and <c>known</c> are generated first.
        /// The ordering is not cosmetic: a model that writes several paragraphs of
        /// premise before emitting the index has to hold that index in mind across
        /// all of it, and that is where off-by-one answers come from.
        /// </remarks>
        private static object BuildEnrichmentSchema()
        {
            var strings = new { type = "ARRAY", items = new { type = "STRING" } };

            var item = new
            {
                type = "OBJECT",
                properties = new
                {
                    i = new { type = "INTEGER", description = "The item's index from the list above." },
                    known = new { type = "BOOLEAN", description = "False if you do not genuinely know this title." },
                    premise = new { type = "STRING", description = "What actually happens. Empty if not known." },
                    moments = strings,
                    themes = strings,
                    asks = strings,
                    spoiler = new { type = "BOOLEAN", description = "Whether the above gives away a twist." },
                },
                required = new[] { "i", "known", "premise", "moments", "themes", "asks", "spoiler" },
                propertyOrdering = new[] { "i", "known", "premise", "moments", "themes", "asks", "spoiler" },
            };

            return new
            {
                type = "OBJECT",
                properties = new { items = new { type = "ARRAY", items = item } },
                required = new[] { "items" },
                propertyOrdering = new[] { "items" },
            };
        }

        private static LlmResult ParseResponse(JsonElement root)
        {
            // A prompt rejected outright never reaches a candidate.
            if (root.TryGetProperty("promptFeedback", out var feedback)
                && feedback.TryGetProperty("blockReason", out var blockReason))
            {
                throw new InvalidOperationException(
                    $"Google blocked the request (blockReason: {blockReason.GetString()}).");
            }

            var text = string.Empty;
            string? finishReason = null;

            if (root.TryGetProperty("candidates", out var candidates)
                && candidates.ValueKind == JsonValueKind.Array
                && candidates.GetArrayLength() > 0)
            {
                var candidate = candidates[0];
                if (candidate.TryGetProperty("finishReason", out var finish))
                {
                    finishReason = finish.GetString();
                }

                if (candidate.TryGetProperty("content", out var content)
                    && content.TryGetProperty("parts", out var parts)
                    && parts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        // Thought summaries come back as parts too, flagged; they are
                        // commentary, not the answer, and must not reach the parser.
                        if (part.TryGetProperty("thought", out var thought)
                            && thought.ValueKind == JsonValueKind.True)
                        {
                            continue;
                        }

                        if (part.TryGetProperty("text", out var textElement)
                            && textElement.ValueKind == JsonValueKind.String)
                        {
                            text += textElement.GetString();
                        }
                    }
                }
            }

            // SAFETY and RECITATION discard the candidate's content, so an empty
            // answer with one of those reasons is a refusal, not an empty result set.
            if (finishReason is "SAFETY" or "RECITATION" or "PROHIBITED_CONTENT" or "BLOCKLIST")
            {
                throw new InvalidOperationException($"Google declined the request (finishReason: {finishReason}).");
            }

            long promptTokens = 0;
            long outputTokens = 0;
            long thoughtTokens = 0;
            long cacheRead = 0;
            if (root.TryGetProperty("usageMetadata", out var usage))
            {
                if (usage.TryGetProperty("promptTokenCount", out var prompt))
                {
                    promptTokens = prompt.GetInt64();
                }

                if (usage.TryGetProperty("candidatesTokenCount", out var output))
                {
                    outputTokens = output.GetInt64();
                }

                // Thinking is billed as output but reported separately, and is left
                // out of candidatesTokenCount. Adding it keeps the cost line honest.
                if (usage.TryGetProperty("thoughtsTokenCount", out var thoughts))
                {
                    thoughtTokens = thoughts.GetInt64();
                }

                if (usage.TryGetProperty("cachedContentTokenCount", out var cached))
                {
                    cacheRead = cached.GetInt64();
                }
            }

            // Unlike Anthropic, promptTokenCount is the TOTAL input including the
            // cached span. LlmResult.InputTokens means the uncached remainder, so the
            // cached portion comes off here — otherwise a cache hit would read as an
            // input-token increase in the cost log.
            var uncachedInput = Math.Max(0, promptTokens - cacheRead);

            return new LlmResult(
                text,
                uncachedInput,
                outputTokens + thoughtTokens,
                finishReason == "MAX_TOKENS",
                CacheWriteTokens: 0,
                CacheReadTokens: cacheRead,
                ThinkingTokens: thoughtTokens);
        }

        private static string Truncate(string body)
        {
            return body.Length <= 500 ? body : body[..500] + "…";
        }
    }
}
