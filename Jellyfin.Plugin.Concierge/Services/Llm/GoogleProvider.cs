using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

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

        /// <summary>A rung meaning "send no thinkingConfig at all".</summary>
        private const int Omit = -1;

        /// <summary>The fallback budget tried when a model refuses zero and none was configured.</summary>
        private const int DefaultFallbackBudget = 128;

        /// <summary>
        /// What this model has agreed to be asked for, remembered across providers.
        /// </summary>
        /// <remarks>
        /// <b>Static because a provider does not live long enough to learn anything.</b>
        /// The factory builds a fresh instance for every single query, so an instance
        /// field reset on each search and the discovery was repeated forever — two
        /// rejected round trips per re-rank, permanently, which is precisely what the
        /// comment on the old field promised would not happen. Keyed by model, because
        /// that is what the constraint belongs to.
        /// </remarks>
        private static readonly ConcurrentDictionary<string, int> AcceptedBudget = new(StringComparer.Ordinal);

        /// <summary>The budgets to try, in order. The last is always <see cref="Omit"/>.</summary>
        private readonly int[] _ladder;

        private readonly ILogger<GoogleProvider>? _logger;
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
            TimeSpan? initialRetryDelay = null,
            int configuredThinkingBudget = -1,
            ILogger<GoogleProvider>? logger = null)
        {
            _logger = logger;

            // A configured budget is a statement that this model takes that number, so
            // it is tried first and zero is never sent. Left unset, the ladder starts
            // where it always did.
            _ladder = configuredThinkingBudget > 0
                ? [configuredThinkingBudget, Omit]
                : [0, DefaultFallbackBudget, Omit];

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

            try
            {
                var body = await SendWithRetriesAsync(request, cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(body);
                return ParseResponse(document.RootElement);
            }
            catch (HttpRequestException ex)
                when (!_enableThinking && Rung() < _ladder.Length - 1 && IsBadRequest(ex.Message))
            {
                // Not every Gemini model can be told not to think, and the ones that
                // cannot reject the whole request rather than ignoring the field. That
                // is a permanent 400 on every call, which is how a re-rank profile
                // pointed at such a model produced forty-one consecutive failures over
                // three hours with no symptom except worse results.
                //
                // So: ask again without the budget and let the model reason if it must.
                // Thinking costs money and latency, but a model that answers is worth
                // more than a setting that is obeyed.
                // Escalate one step at a time rather than jumping to "reason freely".
                // Measured on gemini-3.6-flash, which refuses a zero budget: given no
                // thinkingConfig at all it spent 1,178 of 1,445 output tokens thinking
                // on a re-rank, 12.5s at the median against 2.5s for a model that
                // accepts zero. "As little as this model allows" and "as much as it
                // likes" are very different answers to the same setting.
                string? body = null;
                var reached = Rung();

                for (var rung = Rung() + 1; rung < _ladder.Length; rung++)
                {
                    _logger?.LogInformation(
                        "Concierge: {Model} refused {Refused}; trying {Next}",
                        ModelId,
                        Describe(_ladder[rung - 1]),
                        Describe(_ladder[rung]));

                    try
                    {
                        body = await SendWithRetriesAsync(request, cancellationToken, _ladder[rung])
                            .ConfigureAwait(false);
                        reached = rung;
                        break;
                    }
                    catch (HttpRequestException retry)
                        when (rung < _ladder.Length - 1 && IsBadRequest(retry.Message))
                    {
                        // Refused this rung too; try the next one down.
                    }
                }

                if (body is null)
                {
                    throw;
                }

                // Latched only now, after the retry has actually worked. A 400 has many
                // causes and Gemini's message names none of them — if the retry fails
                // too then the budget was never the problem, and quietly turning
                // thinking on for the rest of the process would be a second bug hiding
                // the first. The cost of not latching is one extra failed call per
                // query; the cost of latching wrongly is a setting that silently stops
                // meaning anything.
                // Remembered against the model, so the next query starts here instead
                // of repeating the discovery. Only after an attempt has actually
                // worked - see the note on AcceptedRung.
                AcceptedBudget[ModelId] = _ladder[reached];

                _logger?.LogInformation(
                    "Concierge: {Model} accepted {Accepted}; later calls will start there",
                    ModelId,
                    Describe(_ladder[reached]));

                using var document = JsonDocument.Parse(body);
                return ParseResponse(document.RootElement);
            }
        }

        /// <summary>
        /// Whether a failure was the API rejecting the request itself.
        /// </summary>
        /// <remarks>
        /// Broader than the equivalent check in <see cref="OpenAiChatProvider"/>, which
        /// can look for the offending field by name. Gemini's refusal is a bare
        /// <c>INVALID_ARGUMENT</c> with the message "Request contains an invalid
        /// argument" and no <c>details</c>, so there is nothing narrower to match on.
        /// The retry is bounded instead: only when a budget was actually sent, only on
        /// 400, and only once per provider.
        /// </remarks>
        /// <param name="message">The failure message.</param>
        /// <returns>Whether it was an HTTP 400.</returns>
        /// <summary>Where this model is known to have settled, or the start of the ladder.</summary>
        /// <remarks>
        /// <b>Keyed on the budget itself, never on its position.</b> Caching the rung
        /// index looked equivalent and was not: the ladder's shape depends on
        /// configuration, so changing the configured budget silently reinterprets a
        /// remembered index. Observed here — 128 was accepted as rung 1 of
        /// [0, 128, omit], a budget of 100 was then configured making the ladder
        /// [100, omit], and rung 1 now meant <em>omit</em>. The new setting was skipped
        /// on its very first call and the model went straight back to reasoning without
        /// a limit.
        /// <para>
        /// A remembered budget that is no longer on the ladder simply starts over,
        /// which costs one negotiation and is the honest answer to "you changed the
        /// question".
        /// </para>
        /// </remarks>
        private int Rung()
        {
            if (!AcceptedBudget.TryGetValue(ModelId, out var budget))
            {
                return 0;
            }

            var index = Array.IndexOf(_ladder, budget);
            return index < 0 ? 0 : index;
        }

        /// <summary>Names a rung for a log line somebody has to read at 3am.</summary>
        private static string Describe(int budget) => budget switch
        {
            Omit => "no thinking limit at all",
            0 => "a zero thinking budget",
            _ => $"a thinking budget of {budget}",
        };

        private static bool IsBadRequest(string message)
            => message.Contains("returned 400", StringComparison.Ordinal);

        /// <summary>
        /// Posts the request, retrying the transient failures Gemini produces in
        /// normal use. See <see cref="TransientHttpRetry"/> for the line between
        /// transient and permanent.
        /// </summary>
        private Task<string> SendWithRetriesAsync(
            LlmRequest request,
            CancellationToken cancellationToken,
            int? thinkingBudget = null)
        {
            var payload = BuildRequestBody(request, thinkingBudget ?? _ladder[Rung()]);

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

        private object BuildRequestBody(LlmRequest request, int thinkingBudget)
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
                generationConfig = BuildGenerationConfig(request, thinkingBudget),
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

        private object BuildGenerationConfig(LlmRequest request, int thinkingBudget)
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
            if (!_enableThinking && thinkingBudget != Omit)
            {
                config["thinkingConfig"] = new { thinkingBudget };
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
