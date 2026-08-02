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
                    "function", "requestAnimationFrame",
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

        /// <summary>
        /// Card text in the same three lines the native rows use.
        /// </summary>
        /// <remarks>
        /// Title tight under the poster, year beneath it at 86% via
        /// <c>.cardText-secondary</c>, then the match reason. Not
        /// <c>cardTextCentered</c>: <c>.cardText</c> is already left-aligned for ltr,
        /// so adding the centring class and overriding it again with
        /// <c>!important</c> was arguing with the stylesheet over something it had
        /// right — the same mistake that hid the posters for four releases.
        /// </remarks>
        [Fact]
        public void CardTextIsLaidOutLikeTheNativeMoviesAndShowsRows()
        {
            // Comments stripped: this file explains in prose which classes it does
            // NOT use, and a test that reads its own rationale as a violation is a
            // test that punishes documentation.
            var card = WithoutComments(Between(Script, "function card(", "function section("));

            Assert.Contains("cardText cardText-first", card, StringComparison.Ordinal);
            Assert.Contains("cardText cardText-secondary", card, StringComparison.Ordinal);
            Assert.Contains("cardText concierge-why", card, StringComparison.Ordinal);

            Assert.DoesNotContain("cardTextCentered", card, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "text-align:left!important", WithoutComments(Script), StringComparison.Ordinal);
        }

        /// <summary>
        /// The client must not be what pins the result count.
        /// </summary>
        /// <remarks>
        /// Slicing to twelve in the browser meant every search returned exactly
        /// twelve whatever the server decided, which is the thing being fixed. How
        /// many results there are is a judgement the re-rank pass makes; the client's
        /// job is to draw them.
        /// </remarks>
        /// <summary>
        /// Something real on screen while the ranked answer is still being written.
        /// </summary>
        /// <remarks>
        /// The two requests are independent and the cheap one can land second, so the
        /// preview must check that the full answer for the same query has not already
        /// arrived. Without that check, a slow preview overwrites the good answer with
        /// the worse one and the row visibly gets worse a second after it got better.
        /// </remarks>
        [Fact]
        public void AFreePreviewPaintsFirstAndNeverOverwritesTheRankedAnswer()
        {
            var code = WithoutComments(Script);

            Assert.Contains("Preview: true", code, StringComparison.Ordinal);
            Assert.Contains("settledQuery = query", code, StringComparison.Ordinal);
            Assert.Contains("settledQuery === query", code, StringComparison.Ordinal);

            var preview = Between(Script, "function runPreview(", "function run(");

            Assert.Contains("lastQuery !== query", preview, StringComparison.Ordinal);
            Assert.Contains("render(result, true)", preview, StringComparison.Ordinal);
        }

        /// <summary>
        /// The row shows that work is happening, and stops when it stops.
        /// </summary>
        /// <remarks>
        /// A sweep still running over a finished answer claims the model is thinking
        /// when it has already replied, which is worse than no animation at all — it
        /// makes a fast search look like a hung one.
        /// </remarks>
        [Fact]
        public void TheWorkingAnimationIsClearedOnEveryPathThatEndsTheSearch()
        {
            var code = WithoutComments(Script);

            Assert.Contains("@keyframes concierge-sweep", Script, StringComparison.Ordinal);
            Assert.Contains("concierge-working", code, StringComparison.Ordinal);

            // Added in exactly two places — the waiting row and a provisional render —
            // and removed on all three endings: emptied, no results, and answered.
            var added = Regex.Matches(code, @"classList\.add\('concierge-working'\)").Count;
            var removed = Regex.Matches(code, @"classList\.remove\('concierge-working'\)").Count;

            Assert.True(
                removed >= added,
                $"concierge-working is added {added} time(s) and removed {removed}");
            Assert.Contains("classList.toggle('concierge-working'", code, StringComparison.Ordinal);
        }

        /// <summary>
        /// Motion is how the state is delivered, never the only copy of it.
        /// </summary>
        [Fact]
        public void AnyoneWhoAskedForLessMotionGetsTheStateWithoutIt()
        {
            Assert.Contains("prefers-reduced-motion:reduce", Script, StringComparison.Ordinal);
            Assert.Contains(
                "prefers-reduced-motion: reduce", WithoutComments(Script), StringComparison.Ordinal);
        }

        /// <summary>
        /// Cards need a stable identity or the reorder cannot be animated.
        /// </summary>
        [Fact]
        public void EveryCardCarriesTheIdTheSlideTracksItBy()
        {
            var card = Between(Script, "function card(", "function skeletonCards(");

            Assert.Contains("data-concierge-id=", card, StringComparison.Ordinal);
            Assert.Contains(
                ".concierge-card[data-concierge-id]", Script, StringComparison.Ordinal);
        }

        [Fact]
        public void ThePreviewFiresLongBeforeThePaidSearch()
        {
            var preview = Regex.Match(Script, @"var PREVIEW_MS = (\d+);");
            var paid = Regex.Match(Script, @"var DEBOUNCE_MS = (\d+);");

            Assert.True(preview.Success && paid.Success);

            var previewMs = int.Parse(preview.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var paidMs = int.Parse(paid.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

            Assert.True(
                previewMs < paidMs / 2,
                "the free preview must land well before the paid search, not just before it");
        }

        [Fact]
        public void TheClientDrawsHoweverManyResultsItIsGiven()
        {
            Assert.Contains("(result.Hits || []).map(", Script, StringComparison.Ordinal);
            Assert.DoesNotContain("Hits || []).slice(", Script, StringComparison.Ordinal);
        }

        /// <summary>
        /// The poster fix, and the two things that broke it.
        /// </summary>
        /// <remarks>
        /// The posters were blank for four releases because of one line of our own
        /// CSS: <c>#concierge-results .cardImageContainer{position:relative}</c>.
        /// Jellyfin's <c>.cardContent</c> is <c>position:absolute</c> with
        /// <c>contain:strict</c> and <c>height:100%</c> — inset to fill
        /// <c>.cardScalable</c>, whose own height comes from the sibling
        /// <c>.cardPadder</c>. Our rule was more specific, so it put the box back in
        /// normal flow, where a percentage height has no definite parent to resolve
        /// against and <c>contain:size</c> makes the result zero. The card kept its
        /// full portrait height from the padder while the image area collapsed,
        /// which is exactly the reported symptom.
        /// <para>
        /// The token went the same way: it was added after a bare image request
        /// answered 403, but that request used an all-zeros GUID. A real one returns
        /// 200 image/jpeg with no credentials at all.
        /// </para>
        /// </remarks>
        [Fact]
        public void PostersDoNotFightJellyfinsOwnCardLayout()
        {
            Assert.Contains("window.ApiClient.getScaledImageUrl", Script, StringComparison.Ordinal);

            Assert.DoesNotContain("cardImageContainer{position", Script, StringComparison.Ordinal);
            Assert.DoesNotContain("api_key", Script, StringComparison.Ordinal);
        }

        [Fact]
        public void PostersArePaintedTheWayThoseClassesExpect()
        {
            // ".cardImageContainer" and ".coveredImage" are background-image classes
            // in Jellyfin's stylesheet — background-size:cover, background-position:
            // 50%, background-clip:content-box. Filling them any other way means
            // reimplementing what they already do.
            var card = Between(Script, "function card(", "function section(");

            Assert.Contains("background-image:url(", card, StringComparison.Ordinal);
        }

        [Fact]
        public void TheHeadingUsesJellyfinsOwnIndentRatherThanOneOfOurs()
        {
            // Native section headings indent by ".padded-left", which is 3.3%.
            // Overriding it with a hand-picked vw value is what put the heading out
            // of line with every other row on the page.
            Assert.Contains("padded-left", Script, StringComparison.Ordinal);
            Assert.DoesNotContain("concierge-heading{padding-left", Script, StringComparison.Ordinal);
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
        }

        /// <summary>
        /// A settling query dims the row; it does not empty it.
        /// </summary>
        /// <remarks>
        /// Blanking on every keystroke left the section empty for the two-second
        /// settle plus a five-to-nine-second search — the best part of ten seconds
        /// of nothing, which reads as a broken feature rather than a busy one. The
        /// row is only emptied when the query falls below the minimum length, where
        /// no answer is coming at all.
        /// </remarks>
        [Fact]
        public void ASettlingQueryDimsTheRowInsteadOfEmptyingIt()
        {
            var onInput = Between(Script, "function onInput(", "function attach(");

            Assert.Contains("markPending()", onInput, StringComparison.Ordinal);

            var beforeMinCheck = onInput[..onInput.IndexOf("markPending()", StringComparison.Ordinal)];
            var clears = Regex.Matches(beforeMinCheck, @"clearResults\(\)").Count;

            Assert.True(
                clears == 1,
                "clearResults() may only run on the below-minimum path, not on every keystroke");
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
                // These are complete markup fragments/attributes assembled above;
                // every server value inside them has already passed escapeHtml.
                .Where(name => name != "href" && name != "image")
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
            => Regex.Replace(WithoutComments(script), @"'(?:\\.|[^'\\])*'", "''");

        private static string WithoutComments(string script)
        {
            var code = Regex.Replace(script, @"/\*.*?\*/", " ", RegexOptions.Singleline);

            return Regex.Replace(code, @"//[^\n]*", " ");
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
