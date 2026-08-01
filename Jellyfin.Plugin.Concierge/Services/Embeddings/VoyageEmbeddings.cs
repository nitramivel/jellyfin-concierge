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
    /// Voyage AI embeddings (POST {base}/embeddings) — what Anthropic itself points
    /// to for embeddings, and strong on retrieval quality.
    /// </summary>
    /// <remarks>
    /// Close enough to the OpenAI shape to be tempting to fold into
    /// <see cref="OpenAiCompatibleEmbeddings"/>, and kept apart for two concrete
    /// differences: the query/document distinction is the native
    /// <c>input_type</c> field rather than a text prefix, and the vector width is
    /// <c>output_dimension</c> rather than <c>dimensions</c>. Sharing the class
    /// would mean a prefix and an input_type both applied, or neither.
    /// </remarks>
    public sealed class VoyageEmbeddings : IEmbeddingProvider
    {
        /// <summary>The default API base, including the version segment.</summary>
        public const string DefaultBaseUrl = "https://api.voyageai.com/v1";

        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly Uri _endpoint;
        private readonly TimeSpan? _initialRetryDelay;

        /// <param name="httpClient">The HTTP client.</param>
        /// <param name="model">The embedding model identifier.</param>
        /// <param name="apiKey">The Voyage API key.</param>
        /// <param name="baseUrl">Optional base URL override.</param>
        /// <param name="dimensions">Requested vector width, or 0 for the model's native width.</param>
        /// <param name="initialRetryDelay">First backoff step; overridden only by tests.</param>
        public VoyageEmbeddings(
            HttpClient httpClient,
            string model,
            string apiKey,
            string? baseUrl = null,
            int dimensions = 0,
            TimeSpan? initialRetryDelay = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
            _httpClient = httpClient;
            ModelId = model;
            _apiKey = apiKey;
            Dimensions = dimensions;
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

            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model"] = ModelId,
                ["input"] = texts,
                ["input_type"] = purpose == EmbeddingPurpose.Query ? "query" : "document",
            };

            if (Dimensions > 0)
            {
                payload["output_dimension"] = Dimensions;
            }

            var body = await TransientHttpRetry.SendAsync(
                _httpClient,
                () =>
                {
                    var message = new HttpRequestMessage(HttpMethod.Post, _endpoint);
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                    message.Content = JsonContent.Create(payload);
                    return message;
                },
                (status, failure) => $"Voyage returned {(int)status}: {Truncate(failure)}",
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
                        $"Concierge: Voyage returned no vector for input {i} of {texts.Count}.");
                }
            }

            // Voyage reports "total_tokens" where OpenAI reports "prompt_tokens";
            // there is no output half to separate it from.
            long inputTokens = 0;
            if (root.TryGetProperty("usage", out var usage)
                && usage.TryGetProperty("total_tokens", out var total)
                && total.ValueKind == JsonValueKind.Number)
            {
                inputTokens = total.GetInt64();
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
