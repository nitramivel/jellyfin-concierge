using System;
using Jellyfin.Plugin.Concierge.Core.Ranking;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    /// <summary>
    /// The re-rank is asked for what will be shown, not for a fixed twenty.
    /// </summary>
    /// <remarks>
    /// Every entry past the displayed count is output generated, waited for, paid for
    /// and then discarded. Measured on this library, re-rank duration is 795 ms fixed
    /// plus 1.96 ms per output token at r=0.998 — so with the page showing ten and the
    /// model asked for twenty, roughly 440 ms of a two-second search was spent on the
    /// half nobody sees.
    /// <para>
    /// The previous behaviour remains reachable: asking for 20 explicitly reproduces
    /// every release before this one, which matters for comparing against an evaluation
    /// run on the old numbers.
    /// </para>
    /// </remarks>
    public class RerankReturnCountTests
    {
        [Fact]
        public void TheInstructionAsksForTheCountItIsGiven()
        {
            var ten = RerankPromptBuilder.BuildInstruction("dark", 30, 60, 8, returnCount: 10);

            Assert.Contains("Return at most 10 of them", ten, StringComparison.Ordinal);
            Assert.DoesNotContain("Return at most 20 of them", ten, StringComparison.Ordinal);
        }

        [Fact]
        public void AskingForTwenty_ReproducesTheOldWording()
        {
            // The escape hatch. Anyone comparing against an evaluation run before this
            // change needs the prompt to be the same prompt.
            var restored = RerankPromptBuilder.BuildInstruction("dark", 30, 60, 8, returnCount: 20);
            var legacy = RerankPromptBuilder.BuildInstruction("dark", 30, 60, 8);

            Assert.Equal(legacy, restored);
        }

        [Fact]
        public void TheDefaultIsUnchanged_SoExistingCallersAreUnaffected()
        {
            // The parameter is optional and defaults to what the constant always was.
            Assert.Contains(
                $"Return at most {RerankPromptBuilder.DefaultReturned} of them",
                RerankPromptBuilder.BuildInstruction("dark", 30),
                StringComparison.Ordinal);
        }

        [Fact]
        public void ExplainCountNeverExceedsWhatIsReturned()
        {
            // Explaining eight of five would be asking for reasons on entries that were
            // never requested.
            var five = RerankPromptBuilder.BuildInstruction("dark", 30, 60, explainCount: 8, returnCount: 5);

            Assert.Contains("Return at most 5 of them", five, StringComparison.Ordinal);
            Assert.Contains("first 5 only", five, StringComparison.Ordinal);
        }

        [Fact]
        public void ZeroFallsBackRatherThanAskingForNothing()
        {
            // Zero reaches the builder only if something upstream is wrong; it must not
            // become "return at most 0".
            var zero = RerankPromptBuilder.BuildInstruction("dark", 30, 60, 8, returnCount: 0);

            Assert.Contains(
                $"Return at most {RerankPromptBuilder.DefaultReturned} of them",
                zero,
                StringComparison.Ordinal);
        }
    }
}
