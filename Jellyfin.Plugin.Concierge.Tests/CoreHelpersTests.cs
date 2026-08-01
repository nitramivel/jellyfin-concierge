using System;
using Jellyfin.Plugin.Concierge.Core;
using Jellyfin.Plugin.Concierge.Core.Llm;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    public class LibraryPathFilterTests
    {
        private static readonly string[] Roots = ["/data/Movies", "/data/TV"];

        [Theory]
        [InlineData("/data/Movies/Memento (2000)/Memento.mkv", true)]
        [InlineData("/data/TV/Show/S01E01.mkv", true)]
        [InlineData("/data/Movies", true)]
        [InlineData("/storage/Movies/Ghost.mkv", false)]
        public void IsInsideLibrary_MatchesOnlyRealRoots(string path, bool expected)
        {
            Assert.Equal(expected, LibraryPathFilter.IsInsideLibrary(path, Roots));
        }

        [Fact]
        public void IsInsideLibrary_ComparesOnASeparatorBoundary()
        {
            // "/data/Movies" must not swallow "/data/Movies2".
            Assert.False(LibraryPathFilter.IsInsideLibrary("/data/Movies2/Other.mkv", Roots));
        }

        [Fact]
        public void IsInsideLibrary_IsCaseInsensitive()
        {
            // Jellyfin runs on Windows too, where a drive-letter case difference
            // would otherwise reject an entire library.
            Assert.True(LibraryPathFilter.IsInsideLibrary("/DATA/movies/Film.mkv", Roots));
        }

        [Fact]
        public void IsInsideLibrary_WithNoKnownRoots_KeepsEverything()
        {
            // Fails open. Excluding the whole library because the folder list could
            // not be read is far worse than indexing a few dead rows.
            Assert.True(LibraryPathFilter.IsInsideLibrary("/anywhere/at/all.mkv", []));
            Assert.True(LibraryPathFilter.IsInsideLibrary("/anywhere/at/all.mkv", null));
        }

        [Fact]
        public void IsInsideLibrary_ItemWithNoPath_IsExcluded()
        {
            Assert.False(LibraryPathFilter.IsInsideLibrary(null, Roots));
            Assert.False(LibraryPathFilter.IsInsideLibrary("   ", Roots));
        }

        [Fact]
        public void IsInsideLibrary_RootOfSlash_ContainsEverything()
        {
            Assert.True(LibraryPathFilter.IsInsideLibrary("/anything", ["/"]));
        }
    }

    public class JsonResponseTests
    {
        [Fact]
        public void ExtractObject_PlainObject_IsReturnedWhole()
        {
            Assert.Equal("""{"a":1}""", JsonResponse.ExtractObject("""{"a":1}"""));
        }

        [Fact]
        public void ExtractObject_StripsACodeFenceAndTrailingProse()
        {
            // Taking the last '}' in the buffer would swallow the trailing sentence
            // and fail to parse partway through otherwise-valid output.
            var raw = "Here you go:\n```json\n{\"a\":1}\n```\nHope that helps! {not json}";

            Assert.Equal("""{"a":1}""", JsonResponse.ExtractObject(raw));
        }

        [Fact]
        public void ExtractObject_IgnoresBracesInsideStrings()
        {
            var raw = """{"title":"a } brace","n":1}""";

            Assert.Equal(raw, JsonResponse.ExtractObject(raw));
        }

        [Fact]
        public void ExtractObject_IgnoresEscapedQuotes()
        {
            var raw = """{"title":"he said \"hi\" }","n":1}""";

            Assert.Equal(raw, JsonResponse.ExtractObject(raw));
        }

        [Fact]
        public void ExtractObject_HandlesNesting()
        {
            var raw = """{"outer":{"inner":{"deep":1}}}""";

            Assert.Equal(raw, JsonResponse.ExtractObject(raw));
        }

        [Fact]
        public void ExtractObject_NoObject_Throws()
        {
            Assert.Throws<FormatException>(() => JsonResponse.ExtractObject("I could not answer that."));
        }

        [Fact]
        public void ExtractObject_TruncatedObject_Throws()
        {
            // The usual cause is the response being cut off by the output cap.
            Assert.Throws<FormatException>(() => JsonResponse.ExtractObject("""{"a":1,"b":"""));
        }
    }
}
