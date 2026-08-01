using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Concierge.Services.Embeddings;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    public class EmbeddingProviderTests
    {
        /// <summary>Two vectors, deliberately returned out of request order.</summary>
        private const string OutOfOrderBody =
            """
            {"data":[{"index":1,"embedding":[9,9]},{"index":0,"embedding":[1,1]}],
             "usage":{"prompt_tokens":42}}
            """;

        private const string TwoVectorBody =
            """
            {"data":[{"index":0,"embedding":[1,0]},{"index":1,"embedding":[0,1]}],
             "usage":{"prompt_tokens":10}}
            """;

        private static OpenAiCompatibleEmbeddings Local(
            HttpMessageHandler handler,
            string query = "query: ",
            string document = "passage: ",
            int dimensions = 0,
            bool sendDimensions = false)
            => new(
                new HttpClient(handler),
                "bge-m3",
                null,
                "http://localhost:11434/v1",
                dimensions,
                query,
                document,
                sendDimensions);

        [Fact]
        public async Task OpenAiCompatible_ReordersTheResponseByIndex()
        {
            // The response carries an explicit index and is not promised to be in
            // request order. Trusting position attaches the wrong vector to the wrong
            // film — no exception, and every result quietly wrong.
            var handler = new StubHandler(OutOfOrderBody);
            var provider = Local(handler);

            var result = await provider.EmbedAsync(["first", "second"], EmbeddingPurpose.Document, CancellationToken.None);

            Assert.Equal([1f, 1f], result.Vectors[0]);
            Assert.Equal([9f, 9f], result.Vectors[1]);
            Assert.Equal(42, result.InputTokens);
        }

        [Fact]
        public async Task OpenAiCompatible_AppliesTheQueryPrefixToAQuery()
        {
            var handler = new StubHandler(TwoVectorBody);
            var provider = Local(handler);

            await provider.EmbedAsync(["memory tattoos", "dog"], EmbeddingPurpose.Query, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var input = body.RootElement.GetProperty("input");
            Assert.Equal("query: memory tattoos", input[0].GetString());
            Assert.Equal("query: dog", input[1].GetString());
        }

        [Fact]
        public async Task OpenAiCompatible_AppliesTheDocumentPrefixToADocument()
        {
            var handler = new StubHandler(TwoVectorBody);
            var provider = Local(handler);

            await provider.EmbedAsync(["Memento", "John Wick"], EmbeddingPurpose.Document, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var input = body.RootElement.GetProperty("input");
            Assert.Equal("passage: Memento", input[0].GetString());
            Assert.Equal("passage: John Wick", input[1].GetString());
        }

        [Fact]
        public async Task OpenAiCompatible_WithNoPrefixes_SendsTheTextUnchanged()
        {
            var handler = new StubHandler(TwoVectorBody);
            var provider = Local(handler, query: string.Empty, document: string.Empty);

            await provider.EmbedAsync(["a", "b"], EmbeddingPurpose.Query, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.Equal("a", body.RootElement.GetProperty("input")[0].GetString());
        }

        [Fact]
        public async Task OpenAiCompatible_OmitsDimensionsForALocalServer()
        {
            // `dimensions` is an OpenAI extension; a server that does not know it is
            // entitled to reject the whole request.
            var handler = new StubHandler(TwoVectorBody);
            var provider = Local(handler, dimensions: 512, sendDimensions: false);

            await provider.EmbedAsync(["a", "b"], EmbeddingPurpose.Document, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.False(body.RootElement.TryGetProperty("dimensions", out _));
        }

        [Fact]
        public async Task OpenAiCompatible_SendsDimensionsForOpenAiItself()
        {
            var handler = new StubHandler(TwoVectorBody);
            var provider = Local(handler, dimensions: 512, sendDimensions: true);

            await provider.EmbedAsync(["a", "b"], EmbeddingPurpose.Document, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.Equal(512, body.RootElement.GetProperty("dimensions").GetInt32());
        }

        [Fact]
        public async Task OpenAiCompatible_EmptyInput_MakesNoCall()
        {
            var handler = new StubHandler(TwoVectorBody);
            var provider = Local(handler);

            var result = await provider.EmbedAsync([], EmbeddingPurpose.Document, CancellationToken.None);

            Assert.Empty(result.Vectors);
            Assert.Null(handler.RequestBody);
        }

        [Fact]
        public async Task OpenAiCompatible_MissingVector_Throws()
        {
            // A short response would otherwise leave a null in the array and fail
            // much later, somewhere that cannot say what went wrong.
            var handler = new StubHandler("""{"data":[{"index":0,"embedding":[1,1]}]}""");
            var provider = Local(handler);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.EmbedAsync(["a", "b"], EmbeddingPurpose.Document, CancellationToken.None));
        }

        // ── Google ───────────────────────────────────────────────────────────────

        private const string GoogleBody =
            """{"embeddings":[{"values":[1,0]},{"values":[0,1]}]}""";

        [Theory]
        [InlineData(EmbeddingPurpose.Query, "RETRIEVAL_QUERY")]
        [InlineData(EmbeddingPurpose.Document, "RETRIEVAL_DOCUMENT")]
        public async Task Google_CarriesThePurposeAsTaskTypeRatherThanAPrefix(EmbeddingPurpose purpose, string expected)
        {
            var handler = new StubHandler(GoogleBody);
            var provider = new GoogleEmbeddings(new HttpClient(handler), "gemini-embedding-001", "key");

            await provider.EmbedAsync(["Memento", "Heat"], purpose, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            var first = body.RootElement.GetProperty("requests")[0];
            Assert.Equal(expected, first.GetProperty("taskType").GetString());

            // The text is sent untouched — applying a prefix as well would express
            // the same distinction twice and match neither training convention.
            Assert.Equal("Memento", first.GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString());
        }

        [Fact]
        public async Task Google_ShortResponse_Throws()
        {
            // batchEmbedContents answers positionally with no index to sort by, so a
            // short array cannot be recovered — only rejected.
            var handler = new StubHandler("""{"embeddings":[{"values":[1,0]}]}""");
            var provider = new GoogleEmbeddings(new HttpClient(handler), "gemini-embedding-001", "key");

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.EmbedAsync(["a", "b"], EmbeddingPurpose.Document, CancellationToken.None));
        }

        // ── Voyage ───────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(EmbeddingPurpose.Query, "query")]
        [InlineData(EmbeddingPurpose.Document, "document")]
        public async Task Voyage_CarriesThePurposeAsInputType(EmbeddingPurpose purpose, string expected)
        {
            var handler = new StubHandler(
                """{"data":[{"index":0,"embedding":[1,0]}],"usage":{"total_tokens":7}}""");
            var provider = new VoyageEmbeddings(new HttpClient(handler), "voyage-3", "key");

            var result = await provider.EmbedAsync(["Memento"], purpose, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.Equal(expected, body.RootElement.GetProperty("input_type").GetString());

            // Voyage reports total_tokens where OpenAI reports prompt_tokens.
            Assert.Equal(7, result.InputTokens);
        }

        [Fact]
        public async Task Voyage_UsesOutputDimensionNotDimensions()
        {
            var handler = new StubHandler(
                """{"data":[{"index":0,"embedding":[1,0]}],"usage":{"total_tokens":1}}""");
            var provider = new VoyageEmbeddings(new HttpClient(handler), "voyage-3", "key", null, 512);

            await provider.EmbedAsync(["a"], EmbeddingPurpose.Document, CancellationToken.None);

            using var body = JsonDocument.Parse(handler.RequestBody!);
            Assert.Equal(512, body.RootElement.GetProperty("output_dimension").GetInt32());
            Assert.False(body.RootElement.TryGetProperty("dimensions", out _));
        }
    }
}
