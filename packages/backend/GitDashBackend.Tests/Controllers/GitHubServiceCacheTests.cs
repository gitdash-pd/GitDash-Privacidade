using GitDashBackend.Accessors.Interfaces;
using GitDashBackend.Configurations;
using GitDashBackend.Domain.DTOs;
using GitDashBackend.Services.Implementations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace GitDashBackend.Tests.Services
{
    // R4: a cached response is tied to the session and to its current access to the repository
    public class GitHubServiceCacheTests
    {
        private const string Token = "token-b";
        private const string Repo = "ana/private";
        private const string Range = "1 week";

        private readonly Mock<IGitHubAccessor> _accessor = new();
        private readonly Mock<IRedisAccessor> _redis = new();

        private GitHubService CreateService(double ttlMinutes = 30, double accessCheckMinutes = 5) =>
            new(_accessor.Object, _redis.Object, Mock.Of<ILogger<GitHubService>>(),
                Options.Create(new CacheSettings { TtlMinutes = ttlMinutes, AccessCheckMinutes = accessCheckMinutes }));

        [Fact]
        public void CacheKeys_AreStablePerSession_AndHideTokenAndNames()
        {
            var key = CacheKeyBuilder.Build("stats", Token, Repo, Range);

            Assert.Equal(key, CacheKeyBuilder.Build("stats", Token, Repo, Range));
            Assert.NotEqual(key, CacheKeyBuilder.Build("stats", "token-a", Repo, Range));
            Assert.StartsWith("stats:", key);
            Assert.DoesNotContain(Token, key);
            Assert.DoesNotContain("ana", key);
        }

        [Fact]
        public async Task CachedRepositoryData_IsReturned_WhileAccessIsValid()
        {
            var cached = new RepoOverviewStatsDto();
            _redis.Setup(r => r.GetAsync<RepoOverviewStatsDto>(It.IsAny<string>())).ReturnsAsync(cached);
            _accessor.Setup(a => a.HasRepositoryAccessAsync(Token, Repo)).ReturnsAsync(true);

            var result = await CreateService().GetRepositoryStatsAsync(Token, Repo, Range);

            Assert.Same(cached, result);
            _accessor.Verify(a => a.GetRepositoryStatsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            _redis.Verify(r => r.SetAsync(It.Is<string>(k => k.StartsWith("access:")), "ok", TimeSpan.FromMinutes(5)), Times.Once);
        }

        [Fact]
        public async Task CachedRepositoryData_IsDiscarded_WhenAccessWasRevoked()
        {
            var cached = new RepoOverviewStatsDto();
            var fresh = new RepoOverviewStatsDto();
            _redis.Setup(r => r.GetAsync<RepoOverviewStatsDto>(It.IsAny<string>())).ReturnsAsync(cached);
            _accessor.Setup(a => a.HasRepositoryAccessAsync(Token, Repo)).ReturnsAsync(false);
            // In production GitHub answers 404 here and the controller returns 404
            _accessor.Setup(a => a.GetRepositoryStatsAsync(Token, Repo, Range)).ReturnsAsync(fresh);

            var result = await CreateService().GetRepositoryStatsAsync(Token, Repo, Range);

            Assert.NotSame(cached, result);
            _redis.Verify(r => r.RemoveAsync(It.Is<string>(k => k.StartsWith("stats:"))), Times.Once);
            _accessor.Verify(a => a.GetRepositoryStatsAsync(Token, Repo, Range), Times.Once);
        }

        [Fact]
        public async Task AccessCheck_UsesShortLivedMarker_InsteadOfAskingGitHubEveryTime()
        {
            _redis.Setup(r => r.GetAsync<RepoOverviewStatsDto>(It.IsAny<string>())).ReturnsAsync(new RepoOverviewStatsDto());
            _redis.Setup(r => r.GetAsync<string>(It.Is<string>(k => k.StartsWith("access:")))).ReturnsAsync("ok");

            await CreateService().GetRepositoryStatsAsync(Token, Repo, Range);

            _accessor.Verify(a => a.HasRepositoryAccessAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task AccessCheck_AlwaysAsksGitHub_WhenWindowIsZero()
        {
            _redis.Setup(r => r.GetAsync<RepoOverviewStatsDto>(It.IsAny<string>())).ReturnsAsync(new RepoOverviewStatsDto());
            _redis.Setup(r => r.GetAsync<string>(It.IsAny<string>())).ReturnsAsync("ok");
            _accessor.Setup(a => a.HasRepositoryAccessAsync(Token, Repo)).ReturnsAsync(true);

            await CreateService(accessCheckMinutes: 0).GetRepositoryStatsAsync(Token, Repo, Range);

            _accessor.Verify(a => a.HasRepositoryAccessAsync(Token, Repo), Times.Once);
            _redis.Verify(r => r.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>()), Times.Never);
        }

        [Fact]
        public async Task FreshData_IsCachedWithConfiguredTtl()
        {
            var fresh = new RepoOverviewStatsDto();
            _accessor.Setup(a => a.GetRepositoryStatsAsync(Token, Repo, Range)).ReturnsAsync(fresh);

            await CreateService(ttlMinutes: 1).GetRepositoryStatsAsync(Token, Repo, Range);

            _redis.Verify(r => r.SetAsync(It.Is<string>(k => k.StartsWith("stats:")), fresh, TimeSpan.FromMinutes(1)), Times.Once);
        }
    }
}
