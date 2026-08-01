using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Concierge.Services.Web;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// Structural checks on the client script.
    /// </summary>
    /// <remarks>
    /// There is no browser here and these deliberately do not pretend otherwise —
    /// they cannot tell you the cards look right. What they can do is catch the
    /// class of mistake that ships a script which throws on the first search, and
    /// hold the one rule the script is built on to something stronger than a
    /// comment. Both have already happened once: an edit removed
    /// <c>render()</c> while leaving its call site, and the only thing standing
    /// between that and a silent no-op in the browser was reading the file again.
    /// </remarks>
    public class ClientScriptTests
    {
        private static readonly string Script = ScriptInjector.ReadScript();

        [Fact]
        public void TheScriptIsEmbeddedAndServed()
        {
            Assert.NotEqual(0, Script.Length);
            Assert.Contains("concierge-results", Script, StringComparison.Ordinal);
        }

        /// <summary>
        /// The upgrade path, which silently did nothing once already.
        /// </summary>
        /// <remarks>
        /// 0.10.0.0 installed and loaded on the server while the browser went on
        /// running 1.0.0.0's script, because the URL was identical between the two
        /// and the response carried no cache headers. The version had to be in the
        /// URL for the new file to ever be asked for.
        /// </remarks>
        [Fact]
        public void TheScriptUrlChangesWhenTheScriptDoes()
        {
            var url = ScriptInjector.VersionedScriptPath;

            Assert.StartsWith(ScriptInjector.ScriptPath + "?v=", url, StringComparison.Ordinal);
            Assert.DoesNotContain("?v=0", url, StringComparison.Ordinal);
        }

        [Fact]
        public void ThePatchedPageAsksForThatVersionedUrl()
        {
            var patched = ScriptInjector.Patch("<html><body><div></div></body></html>");

            Assert.NotNull(patched);
            Assert.Contains(
                "src=\"" + ScriptInjector.VersionedScriptPath + "\"",
                patched,
                StringComparison.Ordinal);
            Assert.EndsWith("</body></html>", patched, StringComparison.Ordinal);
        }

        [Fact]
        public void APageAlreadyCarryingTheTagIsLeftExactlyAsItIs()
        {
            var once = ScriptInjector.Patch("<html><body></body></html>");

            Assert.NotNull(once);
            Assert.Null(ScriptInjector.Patch(once));
        }

        [Fact]
        public void AnythingThatIsNotTheClientShellIsLeftAlone()
        {
            Assert.Null(ScriptInjector.Patch("{\"not\":\"html\"}"));
        }

        [Fact]
        public void EveryFunctionItCallsIsOneItDefines()
        {
            var defined = new HashSet<string>(
                Regex.Matches(Script, @"function\s+([A-Za-z_$][\w$]*)\s*\(")
                    .Select(m => m.Groups[1].Value),
                StringComparer.Ordinal);

            // Everything the script is allowed to reach for that it does not define
            // itself. Anything called and not on this list is a typo or a deletion.
            var globals = new HashSet<string>(
                new[]
                {
                    "setTimeout", "clearTimeout", "String", "Math", "Array",
                    "JSON", "MutationObserver", "encodeURIComponent", "if",
                    "for", "while", "switch", "catch", "return", "typeof",
                    "function",
                },
                StringComparer.Ordinal);

            // Bare calls only, and only in code: anything preceded by "." is a
            // method on a value we did not create, and the string literals are full
            // of CSS — "rgba(", ":not(", "url(" — which are not calls at all.
            var code = WithoutStringLiterals(Script);

            var called = Regex.Matches(code, @"(?<![.\w$])([A-Za-z_$][\w$]*)\s*\(")
                .Select(m => m.Groups[1].Value)
                .Where(name => !globals.Contains(name))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var missing = called.Where(name => !defined.Contains(name)).ToList();

            Assert.True(
                missing.Count == 0,
                "the client script calls functions it never defines: " + string.Join(", ", missing));
        }

        /// <summary>
        /// The one rule, as an assertion rather than a comment.
        /// </summary>
        /// <remarks>
        /// The search page is shared with Jellyfin Enhanced's Jellyseerr sections.
        /// Writing <c>innerHTML</c> on anything but our own container is how one
        /// script silently erases another's work, so the only permitted targets are
        /// the two local names that can only hold our own element.
        /// </remarks>
        [Fact]
        public void ItOnlyEverWritesIntoItsOwnContainer()
        {
            var targets = Regex.Matches(Script, @"([\w$.]+)\.innerHTML\s*=")
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            Assert.All(targets, target => Assert.Equal("el", target));
        }

        [Fact]
        public void ItNeverRemovesOrReplacesAnythingAtAll()
        {
            // insertBefore moves OUR node and is expected; remove/replace/clearing a
            // parent are not, at any point, on any element.
            Assert.DoesNotContain(".remove()", Script, StringComparison.Ordinal);
            Assert.DoesNotContain("removeChild", Script, StringComparison.Ordinal);
            Assert.DoesNotContain("replaceChild", Script, StringComparison.Ordinal);
            Assert.DoesNotContain("replaceWith", Script, StringComparison.Ordinal);
        }

        [Fact]
        public void ItAnchorsToTheJellyseerrSectionByItsRealClassName()
        {
            // Read out of Jellyfin Enhanced 12.0.0.0 rather than guessed. If this
            // ever fails, the placement silently falls back to appending — which is
            // the bad placement the owner reported — so it fails loudly here first.
            Assert.Contains(".jellyseerr-section", Script, StringComparison.Ordinal);
        }

        [Fact]
        public void ResultsUseTheNativeHorizontalSearchRowShape()
        {
            Assert.Contains(
                "verticalSection emby-scroller-container concierge-section",
                Script,
                StringComparison.Ordinal);
            Assert.Contains("is=\"emby-scroller\"", Script, StringComparison.Ordinal);
            Assert.Contains("data-horizontal=\"true\"", Script, StringComparison.Ordinal);
            Assert.Contains(
                "padded-bottom-focusscale emby-scroller",
                Script,
                StringComparison.Ordinal);
            Assert.Contains("itemsContainer scrollSlider concierge-row", Script, StringComparison.Ordinal);
            Assert.Contains("card-withuserdata concierge-card", Script, StringComparison.Ordinal);
            Assert.DoesNotContain("vertical-wrap concierge-row", Script, StringComparison.Ordinal);
        }

        [Fact]
        public void EmptyNativeSearchUsesTheSameLandmarkAsDiscoverOnSeerr()
        {
            Assert.Contains("page.querySelector('.noItemsMessage')", Script, StringComparison.Ordinal);
            Assert.Contains(
                "noResults.parentNode.insertBefore(el, noResults.nextSibling)",
                Script,
                StringComparison.Ordinal);
        }

        [Fact]
        public void ADecisivelyNativeQueryDoesNotGetADuplicateRow()
        {
            Assert.Contains("result.Route === 'Native'", Script, StringComparison.Ordinal);
        }

        [Fact]
        public void PaidSearchWaitsForASettledQueryButEnterRunsImmediately()
        {
            var match = Regex.Match(Script, @"var DEBOUNCE_MS = (\d+);");

            Assert.True(match.Success, "the client script no longer declares its debounce");
            Assert.True(
                int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) >= 1500,
                "a paid search must wait substantially longer than Jellyfin's free native search");
            Assert.Contains("e.key === 'Enter'", Script, StringComparison.Ordinal);
            Assert.Contains("runNow(e.target.value)", Script, StringComparison.Ordinal);
            Assert.Contains("inFlightQuery === query", Script, StringComparison.Ordinal);
        }

        [Fact]
        public void ChangingTheQueryInvalidatesAnyOlderResponseImmediately()
        {
            var onInput = Between(Script, "function onInput(", "function attach(");

            Assert.Contains("inFlight = null", onInput, StringComparison.Ordinal);
            Assert.Contains("clearResults()", onInput, StringComparison.Ordinal);
        }

        [Fact]
        public void EveryValueItInterpolatesIntoMarkupIsEscaped()
        {
            // Every card field is server data, and the library's titles are whatever
            // the filenames said. One unescaped quote in a title is a broken card at
            // best, so the card builder must not concatenate a raw value.
            var card = Between(Script, "function card(", "function section(");

            var raw = Regex.Matches(card, @"\+\s*(?!escapeHtml|'|\()([A-Za-z_$][\w$]*)")
                .Select(m => m.Groups[1].Value)
                .Where(name => name != "image" && name != "href")
                .ToList();

            Assert.True(
                raw.Count == 0,
                "the card builder interpolates unescaped values: " + string.Join(", ", raw));
        }

        /// <summary>
        /// Reduces the script to executable code: no comments, no string literals.
        /// </summary>
        /// <remarks>
        /// Comments go first and it matters. The prose in this script is full of
        /// apostrophes — "the client's rules", "it's" — and each one reads as an
        /// opening quote, which desynchronises literal matching for the rest of the
        /// file and leaves the CSS in <c>STYLES</c> looking like calls to
        /// <c>rgba()</c> and <c>url()</c>.
        /// <para>
        /// The script quotes exclusively with <c>'</c> and contains no <c>//</c>
        /// inside a literal, so this stays a few lines rather than becoming a
        /// JavaScript lexer.
        /// </para>
        /// </remarks>
        private static string WithoutStringLiterals(string script)
        {
            var code = Regex.Replace(script, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            code = Regex.Replace(code, @"//[^\n]*", " ");

            return Regex.Replace(code, @"'(?:\\.|[^'\\])*'", "''");
        }

        private static string Between(string text, string start, string end)
        {
            var from = text.IndexOf(start, StringComparison.Ordinal);
            var to = text.IndexOf(end, StringComparison.Ordinal);

            Assert.InRange(from, 0, int.MaxValue);
            Assert.InRange(to, from, int.MaxValue);

            return text[from..to];
        }
    }
}
