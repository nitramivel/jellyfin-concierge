using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Concierge.Services.Llm;

namespace Jellyfin.Plugin.Concierge.Services.Embeddings
{
    /// <summary>
    /// The OpenAI embeddings wire format (POST {base}/embeddings), which covers
    /// the official API and every server that speaks its dialect — Ollama, LM
    /// Studio, vLLM.
    /// </summary>
    /// <remarks>
    /// One class for four backends is what makes the local option free: a private,
    /// no-data-leaves-the-house index is a profile with a base URL and no new code.
    /// <para>
    /// This is also the only provider that applies the <em>text</em> prefixes.
    /// Google and Voyage carry the query/document distinction as a first-class
    /// request field, so their providers pass the purpose through natively instead.
    /// </para>
    /// </remarks>
    public sealed class OpenAiCompatibleEmbeddings : IEmbeddingProvider
    {
        /// <summary>The official OpenAI API base, including the version segment.</summary>
        public const string DefaultBaseUrl = "https://api.openai.com/v1";

        private readonly HttpClient _httpClient;
        private readonly string? _apiKey;
        private readonly Uri _endpoint;
        private readonly string _queryPrefix;
        private readonly string _documentPrefix;
        private readonly bool _sendDimensions;
        private readonly TimeSpan? _initialRetryDelay;

        /// <param name="httpClient">The HTTP client.</param>
        /// <param name="model">The embedding model identifier.</param>
        /// <param name="apiKey">The API key; optional, since local servers often need none.</param>
        /// <param name="baseUrl">The endpoint base including the version segment; null uses OpenAI's.</param>
        /// <param name="dimensions">Requested vector width, or 0 for the model's native width.</param>
        /// <param name="queryPrefix">Marker prepended when embedding a query.</param>
        /// <param name="documentPrefix">Marker prepended when embedding a document.</param>
        /// <param name="sendDimensions">
        /// Whether to send the <c>dimensions</c> field. Off for arbitrary compatible
        /// servers: it is an OpenAI extension, and a server that has never heard of
        /// it is entitled to reject the whole request rather than ignore the field.
        /// </param>
        /// <param name="initialRetryDelay">First backoff step; overridden only by tests.</param>
        public OpenAiCompatibleEmbeddings(
            HttpClient httpClient,
            string model,
            string? apiKey,
            string? baseUrl,
            int dimensions,
            string queryPrefix,
            string documentPrefix,
            bool sendDimensions,
            TimeSpan? initialRetryDelay = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
            _httpClient = httpClient;
            ModelId = model;
            _apiKey = apiKey;
            Dimensions = dimensions;
            _queryPrefix = queryPrefix ?? string.Empty;
            _documentPrefix = documentPrefix ?? string.Empty;
            _sendDimensions = sendDimensions;
            _initialRetryDelay = initialRetryDelay;

            var basePart = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.TrimEnd('/');
            _endpoint = new Uri(basePart + "/embeddings");
        }

        /// <inheritdoc />
        public string ModelId { get; }

        /// <inheritdoc />
        public int Dimensions { get; }

        /// <inheritdoc />
        public async Task<EmbeddingResult> EmbedAsync(
            IReadOnlyList<string> texts,
            EmbeddingPurpose purpose,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(texts);

            if (texts.Count == 0)
            {
                return new EmbeddingResult(Array.Empty<float[]>(), 0);
            }

            var prefix = purpose == EmbeddingPurpose.Query ? _queryPrefix : _documentPrefix;
            var input = new string[texts.Count];
            for (var i = 0; i < texts.Count; i++)
            {
                input[i] = prefix + texts[i];
            }

            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model"] = ModelId,
                ["input"] = input,
            };

            if (_sendDimensions && Dimensions > 0)
            {
                payload["dimensions"] = Dimensions;
            }

            var body = await TransientHttpRetry.SendAsync(
                _httpClient,
                () =>
                {
                    var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                    if (!string.IsNullOrEmpty(_apiKey))
                    {
                        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                    }

                    message.Content = JsonContent.Create(payload);
                    return message;
                },
                (status, failure) => $"Embeddings endpoint returned {(int)status}: {Truncate(failure)}",
                _initialRetryDelay,
                cancellationToken).ConfigureAwait(false);

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var vectors = new float[texts.Count][];
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                var fallbackIndex = 0;
                foreach (var entry in data.EnumerateArray())
                {
                    // The response carries an explicit index and the array is NOT
                    // promised to be in request order. Trusting position here would
                    // attach the wrong vector to the wrong film — an error that
                    // produces no exception and ruins every result silently.
                    var index = entry.TryGetProperty("index", out var idx) && idx.ValueKind == JsonValueKind.Number
                        ? idx.GetInt32()
                        : fallbackIndex;
                    fallbackIndex++;

                    if (index < 0 || index >= vectors.Length)
                    {
                        continue;
                    }

                    if (entry.TryGetProperty("embedding", out var embedding)
                        && embedding.ValueKind == JsonValueKind.Array)
                    {
                        vectors[index] = ReadVector(embedding);
                    }
                }
            }

            for (var i = 0; i < vectors.Length; i++)
            {
                if (vectors[i] is null)
                {
                    throw new InvalidOperationException(
                        $"Concierge: the embeddings endpoint returned no vector for input {i} of {texts.Count}.");
                }
            }

            long inputTokens = 0;
            if (root.TryGetProperty("usage", out var usage)
                && usage.TryGetProperty("prompt_tokens", out var prompt)
                && prompt.ValueKind == JsonValueKind.Number)
            {
                inputTokens = prompt.GetInt64();
            }

            return new EmbeddingResult(vectors, inputTokens);
        }

        private static float[] ReadVector(JsonElement array)
        {
            var vector = new float[array.GetArrayLength()];
            var i = 0;
            foreach (var value in array.EnumerateArray())
            {
                vector[i++] = value.GetSingle();
            }

            return vector;
        }

        private static string Truncate(string body)
        {
            return body.Length <= 500 ? body : body[..500] + "…";
        }
    }
}
