namespace Jellyfin.Plugin.Concierge.Core.Query
{
    /// <summary>
    /// Builds the plan pass: one cheap call that reads a sentence.
    /// </summary>
    /// <remarks>
    /// Runs on its own profile and should be a small fast model — this is the pass
    /// where Haiku-tier is the right answer rather than a compromise. It also sits
    /// directly inside the latency budget, so the prompt is kept short deliberately.
    /// </remarks>
    public static class PlanPromptBuilder
    {
        /// <summary>
        /// The system prompt.
        /// </summary>
        /// <remarks>
        /// The instruction that earns its place is the one about <em>not</em>
        /// guessing. A model asked to fill in a filter object will fill it in, and a
        /// confidently invented year range quietly deletes the right answer — the
        /// searcher never learns that "90s" excluded a 1989 film they were thinking
        /// of. Empty is the correct answer far more often than it feels like it.
        /// </remarks>
        public const string SystemPrompt =
            """
            You read one sentence from someone searching their own media library and
            split it into two things: what they are describing, and any hard
            constraints they mentioned.

            "semantic" is what they are describing, with the constraint words removed.
            For "90s sci-fi under two hours I haven't seen" the semantic text is just
            "science fiction". For "the one where they kill the guy's dog" it is the
            whole sentence, because none of that is a constraint. Never leave it
            empty: if there is nothing but constraints, repeat the useful nouns.

            Constraints go in "filters", and ONLY when the searcher actually said
            them. Do not infer, do not round out, do not fill fields in to be helpful:

            - years: only for an explicit period. "90s" is 1990-1999. "before 2000" is
              an upper bound with no lower one. A film's vibe is not a year.
            - runtimeMaxMinutes: only for an explicit length. "short" is not a number.
            - genres: only genre words they actually used. "dark" is a mood, not a
              genre. "funny" is a mood; "comedy" is a genre.
            - people: only names of real actors, directors or writers. Character names
              are not people — "michael scott" is a character, not crew.
            - types: "movie", "film", "show", "series", "episode" only when said.
            - watchState: "unwatched" for "I haven't seen", "watched" for "I saw it",
              "favorite" for "one of my favourites". Otherwise "any".

            An empty filter object is a good answer and the most common correct one.
            A wrong filter is worse than no filter, because it removes the right
            result and nobody ever finds out.

            Set "quote" only when they are reciting dialogue — words a character says
            out loud — rather than describing what happens. Otherwise null.
            """;

        /// <summary>
        /// The user prompt for one query.
        /// </summary>
        /// <param name="query">The raw search text.</param>
        /// <returns>The prompt.</returns>
        public static string Build(string query)
        {
            return "Search: " + (query ?? string.Empty).Trim() + "\n\n" + ResponseTemplate;
        }

        /// <summary>
        /// The shape asked for in prose, for providers with no structured output.
        /// </summary>
        /// <remarks>
        /// Must declare exactly the fields the schemas declare. Curator learned that
        /// the expensive way: a prompt asking for a field the schema forbade left the
        /// model writing it into the previous string.
        /// </remarks>
        public const string ResponseTemplate =
            """
            Respond with JSON only:
            {"semantic":"...","filters":{"types":[],"yearFrom":null,"yearTo":null,
            "genres":[],"people":[],"runtimeMaxMinutes":null,"watchState":"any"},
            "quote":null}
            """;
    }
}
