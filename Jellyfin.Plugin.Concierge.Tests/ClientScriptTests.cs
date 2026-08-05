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

        /// <summary>
        /// The script's brackets balance.
        /// </summary>
        /// <remarks>
        /// <b>This is the test that should have existed already.</b> A rewrite of
        /// <c>position()</c> in 0.18.0.0 deleted a function and left its closing brace
        /// behind. That is a syntax error, so the whole script died on load and the
        /// search bar silently did nothing for five releases — while the settings
        /// page's own search kept working, which is why the query log looked healthy.
        /// <para>
        /// Every other test here passed throughout. They match strings and count
        /// occurrences, and none of that can see an unbalanced brace. Comments and
        /// string literals are stripped first because the styles are full of CSS
        /// braces and the prose is full of apostrophes.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheScriptsBracketsBalance()
        {
            var code = WithoutStringLiterals(Script);
            var stack = new Stack<(char Open, int Line)>();
            var line = 1;
            var pairs = new Dictionary<char, char> { [')'] = '(', [']'] = '[', ['}'] = '{' };

            foreach (var c in code)
            {
                if (c == '\n')
                {
                    line++;
                }
                else if (c is '(' or '[' or '{')
                {
                    stack.Push((c, line));
                }
                else if (pairs.TryGetValue(c, out var open))
                {
                    Assert.True(
                        stack.Count > 0,
                        $"unmatched closing '{c}' on line {line}");

                    var top = stack.Pop();
                    Assert.True(
                        top.Open == open,
                        $"'{top.Open}' opened on line {top.Line} is closed by '{c}' on line {line}");
                }
            }

            Assert.True(
                stack.Count == 0,
                stack.Count == 0
                    ? string.Empty
                    : $"'{stack.Peek().Open}' opened on line {stack.Peek().Line} is never closed");
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
                    "function", "requestAnimationFrame", "parseInt", "isNaN",
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

        /// <summary>
        /// The row sits above every other result row, whoever owns it.
        /// </summary>
        /// <remarks>
        /// Concierge answers the question that was actually asked — a description
        /// rather than a substring — so putting it under the rows that matched on
        /// spelling makes the reader scroll past the worse answer to reach the better
        /// one. It anchors on the first result section rather than on any one
        /// plugin's, so a new row from a third plugin cannot end up above it.
        /// </remarks>
        [Fact]
        public void TheRowSitsAboveEveryOtherResultRow()
        {
            var position = Between(Script, "function position(el, page)", "function firstResultSection(");

            Assert.Contains("firstResultSection(page, el)", position, StringComparison.Ordinal);
            Assert.Contains("insertBefore(el, first)", position, StringComparison.Ordinal);

            // Above the "no results" line too: an answer underneath a notice saying
            // there is no answer reads as a contradiction.
            Assert.Contains("insertBefore(el, noResults)", position, StringComparison.Ordinal);
            Assert.DoesNotContain("noResults.nextSibling", position, StringComparison.Ordinal);

            var first = Between(Script, "function firstResultSection(", "function itemLink(");
            Assert.Contains("sections[i] !== el", first, StringComparison.Ordinal);
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

        /// <summary>
        /// Concierge mode is where the one rule is relaxed, so it is where the
        /// guardrails have to be assertions rather than intentions.
        /// </summary>
        /// <remarks>
        /// Hiding another plugin's work is the failure this whole script was written
        /// to avoid. Doing it on purpose is defensible; doing it with a class we do
        /// not own, or without remembering what we hid, is how a page ends up with
        /// sections nobody can bring back.
        /// </remarks>
        /// <summary>
        /// The status names the stage, and the stage changes without a re-render.
        /// </summary>
        /// <remarks>
        /// "searching" and "ranking" are different waits — one is free and about to
        /// end, the other is the model — and a row that says which is a row you can
        /// decide to stop waiting on.
        /// <para>
        /// It has to change while cards are on screen, so it is written as text into
        /// a node that is always present rather than by rebuilding the heading. A
        /// re-render to say "searching" would throw away the results the reader is
        /// looking at, which is the opposite of the point.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheStatusNamesTheStageAndIsWrittenAsTextNotMarkup()
        {
            var code = WithoutComments(Script);

            Assert.Contains("statusLabel('searching')", code, StringComparison.Ordinal);
            Assert.Contains("statusLabel(provisional ? 'ranking' : '')", code, StringComparison.Ordinal);
            Assert.Contains(".concierge-statustext').textContent", code, StringComparison.Ordinal);

            var setStatus = Between(Script, "function setStatus(", "function section(");
            Assert.DoesNotContain("innerHTML", setStatus, StringComparison.Ordinal);
        }

        /// <summary>
        /// The debounce is a setting, and changing it reaches the browser.
        /// </summary>
        /// <remarks>
        /// Substituted into the served file rather than fetched by the client at
        /// startup, because a second request would mean the first keystroke either
        /// races it or waits for it.
        /// <para>
        /// The URL carries a hash of what is actually served, so changing the setting
        /// changes the URL and the browser cannot keep the old number — the same
        /// mechanism that stopped 0.10.0.0 shipping invisibly.
        /// </para>
        /// </remarks>
        [Fact]
        public void TheDebounceIsSubstitutedIntoTheServedScript()
        {
            // The embedded default is what the substitution replaces, so it has to
            // still be there in the shape the replacement looks for.
            Assert.Matches(@"var DEBOUNCE_MS = \d+;", Script);

            var served = ScriptInjector.Configured();

            // No plugin instance in a test run, so this is the raw script — the point
            // is that the path exists and does not mangle the file.
            Assert.Equal(Script, served);
            Assert.Matches(@"var DEBOUNCE_MS = \d+;", served);
        }

        /// <summary>
        /// The toggle is an icon in the search box, centred on the field.
        /// </summary>
        /// <remarks>
        /// Jellyfin Enhanced positions its icon at 68% because it is a 50px image with
        /// a drop shadow; copying that number for a round button put ours visibly low.
        /// Borrowing a proven corner was right, borrowing the offset inside it was not.
        /// </remarks>
        [Fact]
        public void TheModeToggleIsAnIconCentredInTheSearchBox()
        {
            var code = WithoutComments(Script);

            Assert.Contains("right:10px;top:50%", code, StringComparison.Ordinal);
            Assert.Contains("translateY(-50%)", code, StringComparison.Ordinal);

            // A shipped font glyph, so there is no asset to host and it takes the
            // theme's colour.
            Assert.Contains("material-icons", code, StringComparison.Ordinal);
            Assert.DoesNotContain("<img", code, StringComparison.Ordinal);
        }

        /// <summary>
        /// Their icon is hidden by a rule, never by touching their element.
        /// </summary>
        /// <remarks>
        /// The only place this script affects another plugin's interface. A style rule
        /// leaves their element and its click handler intact, so nothing of theirs can
        /// break — and their re-creating the icon on every render does not fight us.
        /// It is also a setting, because that icon is their Seerr-only filter and
        /// taking a control away should not need a release to undo.
        /// </remarks>
        /// <summary>
        /// Off means off: no row, no request, nothing spent.
        /// </summary>
        /// <remarks>
        /// A toggle that leaves its results on screen is a toggle that does not appear
        /// to work. This one also decides whether a search costs money, so the quiet
        /// version would be the expensive one.
        /// </remarks>
        [Fact]
        public void TheToggleGatesTheSearchEntirelyRatherThanJustItsLayout()
        {
            var onInput = WithoutComments(Between(Script, "function onInput(", "function attach("));
            var runNow = WithoutComments(Between(Script, "function runNow(", "function onInput("));

            Assert.Contains("!modeOn()", onInput, StringComparison.Ordinal);
            Assert.Contains("clearResults()", onInput, StringComparison.Ordinal);
            Assert.Contains("!modeOn()", runNow, StringComparison.Ordinal);
        }

        /// <summary>
        /// "No results found" is hidden when Concierge has results.
        /// </summary>
        /// <remarks>
        /// That line is a statement about Jellyfin's substring search, and it is false
        /// the moment Concierge has answers on the same page. It is hidden with our own
        /// class and remembered like every other hidden section, so it returns as soon
        /// as the row empties or the toggle goes off.
        /// </remarks>
        /// <summary>
        /// Concierge is on until somebody turns it off.
        /// </summary>
        /// <remarks>
        /// This default flipped when the toggle stopped being about layout and started
        /// gating the search. Off-by-default used to mean "results appear as an
        /// ordinary row"; it would now mean a freshly installed plugin does nothing at
        /// all until somebody finds an unlabelled icon and presses it.
        /// </remarks>
        [Fact]
        public void ConciergeIsOnUnlessItHasBeenTurnedOff()
        {
            var modeOn = WithoutComments(Between(Script, "function modeOn(", "function setMode("));

            Assert.Contains("!== '0'", modeOn, StringComparison.Ordinal);
            Assert.DoesNotContain("=== '1'", modeOn, StringComparison.Ordinal);

            // Storage being unavailable must not disable the plugin either.
            Assert.Contains("return true;", modeOn, StringComparison.Ordinal);
        }

        [Fact]
        public void TheNoResultsMessageIsHiddenWhileConciergeHasAnswers()
        {
            var apply = WithoutComments(Between(Script, "function applyMode(", "function restoreSections("));

            Assert.Contains(".noItemsMessage", apply, StringComparison.Ordinal);
            Assert.Contains("concierge-hidden", apply, StringComparison.Ordinal);
            Assert.Contains("hiddenSections.push(empty)", apply, StringComparison.Ordinal);
        }

        [Fact]
        public void TheJellyseerrIconIsHiddenByStyleAndOnlyWhenAskedFor()
        {
            var code = WithoutComments(Script);

            Assert.Contains("#jellyseerr-search-icon{display:none!important;}", code, StringComparison.Ordinal);
            Assert.Contains("HIDE_SEERR_ICON", code, StringComparison.Ordinal);
            Assert.Matches(@"var HIDE_SEERR_ICON = (?:true|false);", Script);

            // Nothing of theirs is removed, moved or written to.
            Assert.DoesNotContain("jellyseerr-search-icon').remove", code, StringComparison.Ordinal);
            Assert.DoesNotContain("jellyseerr-search-icon').style", code, StringComparison.Ordinal);
        }

        /// <summary>
        /// The ask count the model is told is the one that was configured.
        /// </summary>
        /// <remarks>
        /// The rules said "6-10 ways" while the instruction said "aim for 12", so a
        /// setting of 12 produced 8 every time — the model followed the rule and the
        /// setting looked broken. A count stated twice is a count stated once, badly.
        /// </remarks>
        [Fact]
        public void TheEnrichmentPromptStatesTheAskCountInExactlyOnePlace()
        {
            var prompt = Jellyfin.Plugin.Concierge.Core.Documents
                .EnrichmentPromptBuilder.SystemPrompt;

            Assert.DoesNotContain("6-10", prompt, StringComparison.Ordinal);

            var instruction = Jellyfin.Plugin.Concierge.Core.Documents
                .EnrichmentPromptBuilder.BuildInstruction(10, 14);

            Assert.Contains("14 entries", instruction, StringComparison.Ordinal);
        }

        [Fact]
        public void TheRowIsHeadedMatches()
        {
            var code = WithoutComments(Script);

            Assert.Contains("'Matches' + statusLabel", code, StringComparison.Ordinal);
            Assert.DoesNotContain("Concierge matches", code, StringComparison.Ordinal);
        }

        [Fact]
        public void ConciergeModeHidesWithItsOwnClassAndNobodyElses()
        {
            var code = WithoutComments(Script);

            Assert.Contains("concierge-hidden", code, StringComparison.Ordinal);

            // Jellyfin Enhanced's class. Sharing it would mean its Seerr-only filter
            // un-hiding our sections and ours un-hiding its.
            Assert.DoesNotContain("section-hidden", code, StringComparison.Ordinal);
        }

        [Fact]
        public void ConciergeModeNeverHidesJellyseerrOrItself()
        {
            var apply = Between(Script, "function applyMode(", "function restoreSections(");

            Assert.Contains("jellyseerr-section", apply, StringComparison.Ordinal);
            Assert.Contains("s.id === CONTAINER_ID", apply, StringComparison.Ordinal);
        }

        [Fact]
        public void EverythingHiddenIsRememberedAndOnlyThatIsRestored()
        {
            var apply = Between(Script, "function applyMode(", "function restoreSections(");
            var restore = Between(Script, "function restoreSections(", "function toggle(");

            Assert.Contains("hiddenSections.push(s)", apply, StringComparison.Ordinal);

            // Restores from the list, never from a selector — a selector would sweep
            // up whatever else on the page happened to match.
            Assert.Contains("hiddenSections[i].classList.remove", restore, StringComparison.Ordinal);
            Assert.DoesNotContain("querySelector", restore, StringComparison.Ordinal);
        }

        /// <summary>
        /// A failed search must never leave a page with nothing on it.
        /// </summary>
        [Fact]
        public void NothingIsHiddenUnlessConciergeActuallyHasSomethingToShow()
        {
            var apply = Between(Script, "function applyMode(", "function restoreSections(");

            Assert.Contains("el.innerHTML !== ''", apply, StringComparison.Ordinal);
            Assert.Contains("modeOn() && showing", apply, StringComparison.Ordinal);
            Assert.Contains("restoreSections()", apply, StringComparison.Ordinal);
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
        public void AnEmptyNativeSearchStillHasAPlaceToPutTheRow()
        {
            Assert.Contains("page.querySelector('.noItemsMessage')", Script, StringComparison.Ordinal);
            Assert.Contains(
                "noResults.parentNode.insertBefore(el, noResults)",
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
