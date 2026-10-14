using GitDashBackend.Domain.DTOs;
using GitDashBackend.Accessors.Interfaces;
using GitDashBackend.Configurations;
using GitDashBackend.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace GitDashBackend.Services.Implementations;

public class GitHubService : IGitHubService
{
    private const string AccessGranted = "ok";

    private readonly IGitHubAccessor _gitHubAccessor;
    private readonly IRedisAccessor _redisAccessor;
    private readonly ILogger<GitHubService> _logger;
    private readonly TimeSpan _cacheExpiration;
    private readonly TimeSpan _accessCheckExpiration;

    public GitHubService(
        IGitHubAccessor gitHubAccessor,
        IRedisAccessor redisAccessor,
        ILogger<GitHubService> logger,
        IOptions<CacheSettings>? cacheOptions = null)
    {
        _gitHubAccessor = gitHubAccessor;
        _redisAccessor = redisAccessor;
        _logger = logger;

        var settings = cacheOptions?.Value ?? new CacheSettings();
        _cacheExpiration = TimeSpan.FromMinutes(settings.TtlMinutes > 0 ? settings.TtlMinutes : 30);
        _accessCheckExpiration = TimeSpan.FromMinutes(Math.Max(settings.AccessCheckMinutes, 0));
    }

    public async Task<RepositoriesDto> GetUserRepositoriesAsync(string token)
    {
        var cacheKey = CacheKeyBuilder.Build("repos", token);
        return await GetOrFetchSingleAsync(cacheKey, () => _gitHubAccessor.GetUserRepositoriesAsync(token));
    }

    public async Task<CommitsDto> GetRepositoryCommitsByFullNameAsync(string token, string fullName)
    {
        var cacheKey = CacheKeyBuilder.Build("commits", token, fullName);
        return await GetOrFetchRepositoryDataAsync(token, fullName, cacheKey,
            () => _gitHubAccessor.GetRepositoryCommitsByFullNameAsync(token, fullName));
    }

    public async Task<CollaboratorsDto> GetRepositoryCollaboratorsAsync(string token, string fullName, string timeRange)
    {
        var cacheKey = CacheKeyBuilder.Build("collaborators", token, fullName, timeRange);
        return await GetOrFetchRepositoryDataAsync(token, fullName, cacheKey,
            () => _gitHubAccessor.GetRepositoryCollaboratorsAsync(token, fullName, timeRange));
    }

    public async Task<RepoOverviewStatsDto> GetRepositoryStatsAsync(string token, string fullName, string timeRange)
    {
        var cacheKey = CacheKeyBuilder.Build("stats", token, fullName, timeRange);
        return await GetOrFetchRepositoryDataAsync(token, fullName, cacheKey,
            () => _gitHubAccessor.GetRepositoryStatsAsync(token, fullName, timeRange));
    }

    public async Task<WeeklyActivityDto?> GetCollaboratorWeeklyActivityAsync(string token, string fullName, string username)
    {
        var cacheKey = CacheKeyBuilder.Build("weekly_activity", token, fullName, username);
        return await GetOrFetchRepositoryDataAsync(token, fullName, cacheKey, async () =>
        {
            var weeks = await _gitHubAccessor.GetCollaboratorWeeklyActivityAsync(token, fullName, username);
            if (weeks == null)
                return null;
            while (weeks.Count < 12)
                weeks.Insert(0, 0);
            return new WeeklyActivityDto { Weeks = weeks };
        });
    }

    public async Task<CollaboratorActivityDto?> GetCollaboratorActivityAsync(string token, string fullName, string username, string range)
    {
        var cacheKey = CacheKeyBuilder.Build("collab_activity", token, fullName, username, range);
        return await GetOrFetchRepositoryDataAsync(token, fullName, cacheKey,
            () => _gitHubAccessor.GetCollaboratorActivityAsync(token, fullName, username, range));
    }

    public Task<RepositoryDto?> GetRepositoryByOwnerRepoAsync(string token, string ownerRepo)
    {
        var cacheKey = CacheKeyBuilder.Build("repo_lookup", token, ownerRepo);
        return GetOrFetchRepositoryDataAsync(token, ownerRepo, cacheKey,
            () => _gitHubAccessor.GetRepositoryByOwnerRepoAsync(token, ownerRepo));
    }

    public async Task<CollaboratorCodeChangesDto?> GetCollaboratorCodeChangesAsync(string token, string fullName, string username, string range)
    {
        var cacheKey = CacheKeyBuilder.Build("code_changes", token, fullName, username, range);
        return await GetOrFetchRepositoryDataAsync(token, fullName, cacheKey,
            () => _gitHubAccessor.GetCollaboratorCodeChangesAsync(token, fullName, username, range));
    }

    // Data about one repository is only reused while the session still has access to that repository.
    // Without this check a response could be served for up to the full TTL after the access was revoked.
    private async Task<T> GetOrFetchRepositoryDataAsync<T>(
        string token, string fullName, string cacheKey, Func<Task<T>> fetchFunc) where T : class?
    {
        var cachedData = await _redisAccessor.GetAsync<T>(cacheKey);
        if (cachedData != null)
        {
            if (await HasRepositoryAccessAsync(token, fullName))
            {
                _logger.LogInformation("Returning data from cache for key: {CacheKey}", cacheKey);
                return cachedData;
            }

            _logger.LogInformation("Cached entry discarded, repository access no longer valid: {CacheKey}", cacheKey);
            await _redisAccessor.RemoveAsync(cacheKey);
        }

        _logger.LogInformation("Cache miss - fetching from GitHub API for key: {CacheKey}", cacheKey);
        var data = await fetchFunc();
        if (data != null)
        {
            await _redisAccessor.SetAsync(cacheKey, data, _cacheExpiration);
            // A successful fetch with this token proves the access
            await MarkAccessAsync(token, fullName);
        }

        return data;
    }

    // Generic cache helper for data that is not tied to one repository
    private async Task<T> GetOrFetchSingleAsync<T>(string cacheKey, Func<Task<T>> fetchFunc) where T : class?
    {
        var cachedData = await _redisAccessor.GetAsync<T>(cacheKey);
        if (cachedData != null)
        {
            _logger.LogInformation("Returning data from cache for key: {CacheKey}", cacheKey);
            return cachedData;
        }

        _logger.LogInformation("Cache miss - fetching from GitHub API for key: {CacheKey}", cacheKey);
        var data = await fetchFunc();

        await _redisAccessor.SetAsync(cacheKey, data, _cacheExpiration);
        return data;
    }

    private async Task<bool> HasRepositoryAccessAsync(string token, string fullName)
    {
        if (_accessCheckExpiration > TimeSpan.Zero &&
            await _redisAccessor.GetAsync<string>(AccessKey(token, fullName)) == AccessGranted)
        {
            return true;
        }

        var hasAccess = await _gitHubAccessor.HasRepositoryAccessAsync(token, fullName);
        if (hasAccess)
            await MarkAccessAsync(token, fullName);

        return hasAccess;
    }

    private async Task MarkAccessAsync(string token, string fullName)
    {
        if (_accessCheckExpiration <= TimeSpan.Zero)
            return;

        await _redisAccessor.SetAsync(AccessKey(token, fullName), AccessGranted, _accessCheckExpiration);
    }

    // Repository names are case-insensitive and may arrive URL-encoded
    private static string AccessKey(string token, string fullName) =>
        CacheKeyBuilder.Build("access", token, Uri.UnescapeDataString(fullName).ToLowerInvariant());
}
