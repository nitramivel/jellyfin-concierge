using Jellyfin.Plugin.Concierge.Services.Indexing;
using Xunit;

namespace Jellyfin.Plugin.Concierge.Tests
{
    public class IndexBuildRequestTests
    {
        [Fact]
        public void AFullRegenerationRequestIsConsumedExactlyOnce()
        {
            var request = new IndexBuildRequest();

            Assert.False(request.ConsumeFullRegeneration());

            request.RequestFullRegeneration();

            Assert.True(request.ConsumeFullRegeneration());
            Assert.False(request.ConsumeFullRegeneration());

            request.CompleteFullRegeneration();
            request.RequestFullRegeneration();

            Assert.True(request.ConsumeFullRegeneration());
        }

        [Fact]
        public void RepeatedRequestsBeforeTheTaskStartsRemainOnePendingBuild()
        {
            var request = new IndexBuildRequest();

            request.RequestFullRegeneration();
            request.RequestFullRegeneration();

            Assert.True(request.ConsumeFullRegeneration());
            Assert.False(request.ConsumeFullRegeneration());
        }

        [Fact]
        public void AClickDuringTheActiveRegenerationCannotQueueASecondPaidRun()
        {
            var request = new IndexBuildRequest();
            request.RequestFullRegeneration();
            Assert.True(request.ConsumeFullRegeneration());

            request.RequestFullRegeneration();
            request.CompleteFullRegeneration();

            Assert.False(request.ConsumeFullRegeneration());
        }
    }
}
