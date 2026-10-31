using GitDashBackend.Domain.DTOs;
using GitDashBackend.Models;
using GitDashBackend.Extensions;
using GitDashBackend.Services.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GitDashBackend.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class GitHubController : ControllerBase
{
    private readonly IGitHubService _gitHubService;
    private readonly IDbService _dbService;
    private readonly ILogger<GitHubController> _logger;

    public GitHubController(IGitHubService gitHubService,
        IDbService dbService,
        ILogger<GitHubController> logger)
    {
        _gitHubService = gitHubService;
        _dbService = dbService;
        _logger = logger;
    }


    /// <summary>
    /// Retrieves the authenticated user's GitHub repositories.
    /// Uses the GitHub OAuth access token stored in the authentication cookie
    /// (saved automatically during the OAuth login flow).
    /// </summary>
    /// <returns>List of user repositories.</returns>

    [Authorize]
    [HttpGet("repositories")]
    [ProducesResponseType(typeof(RepositoriesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetRepositories()
    {
        // Take token from the same place as /api/github/test
        var accessToken = await HttpContext.GetTokenAsync("access_token");
        if (string.IsNullOrEmpty(accessToken))
        {
            // user is not authenticated via GitHub or token not saved
            return Unauthorized(new { error = "GitHub access token not found. Please login via GitHub first." });
        }

        try
        {
            // Fetch repositories
            // Listing is not a visit: the history is only updated in the overview (stats) endpoint,
            // and only for the session user. The previous lookup by name changed other users' history.
            RepositoriesDto repositories = await _gitHubService.GetUserRepositoriesAsync(accessToken);

            return Ok(repositories);
        }
        catch (Octokit.AuthorizationException)
        {
            return Unauthorized(new { error = "Invalid or expired GitHub token" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching repositories");
            return StatusCode(500, new { error = "An error occurred while fetching repositories" });
        }
    }
    
/// <summary>
/// Retrieves the authenticated user's recently visited GitHub repositories.
/// Filters the user's GitHub repositories to show only those present in the local database history,
/// sorted by the most recently visited.
/// </summary>
/// <returns>A filtered and sorted list of recently visited repositories.</returns>
[Authorize]
[HttpGet("recentRepositories")]
[ProducesResponseType(typeof(RepositoriesDto), StatusCodes.Status200OK)]
[ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
[ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)] // Added 404 documentation
[ProducesResponseType(typeof(object), StatusCodes.Status500InternalServerError)]
public async Task<IActionResult> GetRecentRepositories()
{
    // Take token from the same place as /api/github/test
    var accessToken = await HttpContext.GetTokenAsync("access_token");
    if (string.IsNullOrEmpty(accessToken))
    {
        // user is not authenticated via GitHub or token not saved
        return Unauthorized(new { error = "GitHub access token not found. Please login via GitHub first." });
    }

    try
    {
        var username = User.GetGitHubLogin();

        // Fetch repositories from GitHub
        RepositoriesDto repositories = await _gitHubService.GetUserRepositoriesAsync(accessToken);

        if (!string.IsNullOrEmpty(username))
        {
            // 1. Await the database results to get the actual list
            List<Repository> localRepos = await _dbService.GetRecentRepositories(username);

            // CHECK: If no visited repositories exist in the DB, return 404
            if (localRepos == null || !localRepos.Any())
            {
                return NotFound(new { error = "No visited repositories found." });
            }

            // 2. Create a Dictionary for fast lookup (O(1))
            // Key: Repository Name (matches FullName), Value: LastVisited Date
            var visitedMap = localRepos.ToDictionary(
                r => r.Name, 
                r => r.LastVisited, 
                StringComparer.OrdinalIgnoreCase // Case-insensitive matching
            );

            // 3. Filter and Sort
            repositories.repositories = repositories.repositories
                .Where(r => visitedMap.ContainsKey(r.FullName)) // KEEP only if it exists in DB
                .OrderByDescending(r => visitedMap[r.FullName]) // SORT by DB LastVisited date
                .ToList();

            // 4. Update the Count
            // Since we filtered items out, the original count is no longer valid.
            repositories.count = repositories.repositories.Count;
        }

        return Ok(repositories);
    }
    catch (Octokit.AuthorizationException)
    {
        return Unauthorized(new { error = "Invalid or expired GitHub token" });
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error fetching repositories");
        return StatusCode(500, new { error = "An error occurred while fetching repositories" });
    }
}


    /// <summary>
    /// Get commits for a specific repository using fullName as a query parameter.
    /// Uses the GitHub token of the authenticated session, like every other endpoint.
    /// </summary>
    /// <param name="fullName">Repository full name in format: owner/repo (e.g., 'acavieira/ADS-Grupo_PosLaboral-A')</param>
    /// <returns>List of commits with caching</returns>
    /// <example>GET /api/github/commits?fullName=acavieira/ADS-Grupo_PosLaboral-A</example>
    [Authorize]
    [HttpGet("commits")]
    [ProducesResponseType(typeof(CommitsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(object), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetRepositoryCommits([FromQuery] string fullName)
    {
        var accessToken = await HttpContext.GetTokenAsync("access_token");
        if (string.IsNullOrEmpty(accessToken))
        {
            return Unauthorized(new { error = "GitHub access token not found. Please login via GitHub first." });
        }

        if (string.IsNullOrEmpty(fullName) || !fullName.Contains('/'))
        {
            return BadRequest(new { error = "Invalid repository fullName format. Expected format: 'owner/repo'" });
        }

        try
        {
            CommitsDto commits = await _gitHubService.GetRepositoryCommitsByFullNameAsync(accessToken, fullName);
            return Ok(new 
            { 
                repository = fullName,
                count = commits.count, 
                commits = commits.commits
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Octokit.AuthorizationException)
        {
            return Unauthorized(new { error = "Invalid GitHub token" });
        }
        catch (Octokit.NotFoundException)
        {
            return NotFound(new { error = $"Repository '{fullName}' not found" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching commits for {FullName}", fullName);
            return StatusCode(500, new { error = "An error occurred while fetching commits" });
        }
    }

    /// <summary>
    /// Get collaborators for a specific repository using fullName and timeRange as part of the route.
    /// </summary>
    /// <param name="authorization">GitHub Personal Access Token</param>
    /// <param name="fullName">Repository full name in format: owner/repo (e.g., 'acavieira/ADS-Grupo_PosLaboral-A')</param>
    /// <param name="timeRange">Time range for filtering statistics ('1 week', '1 month', '3 months').</param>
    /// <returns>List of collaborators for the repository with their respective statistics.</returns>
    [Authorize]
    [HttpGet("collaborators/{fullName}/{timeRange}")]
    [ProducesResponseType(typeof(CollaboratorsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetRepositoryCollaborators(
        [FromRoute] string fullName,
        [FromRoute] string timeRange)
    {
        try
        {
            var accessToken = await HttpContext.GetTokenAsync("access_token");
            if (string.IsNullOrEmpty(accessToken))
            {
                return Unauthorized(new { error = "GitHub access token not found. Please login via GitHub first." });
            }

            var decodedFullName = Uri.UnescapeDataString(fullName);
            CollaboratorsDto collaborators = await _gitHubService.GetRepositoryCollaboratorsAsync(accessToken, decodedFullName, timeRange);
            if (collaborators == null)
            {
                return NotFound(new { error = $"Repository '{fullName}' not found or invalid." });
            }
            return Ok(new
            {
                repository = decodedFullName,
                count = collaborators.count,
                collaborators = collaborators.collaborators
            });
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Bad request when querying collaborators: {Message}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Octokit.AuthorizationException)
        {
            return Unauthorized(new { error = "Invalid GitHub token" });
        }
        catch (Octokit.NotFoundException)
        {
            return NotFound(new { error = $"Repository '{fullName}' not found or invalid." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching collaborators for {FullName}", fullName);
            return StatusCode(500, new { error = "An error occurred while fetching collaborators." });
        }
    }

    /// <summary>
    /// Get all the view with general repository statistics.
    /// </summary>
    /// <param name="fullName">Repository full name in format: owner/repo (e.g., 'acavieira/ADS-Grupo_PosLaboral-A')</param>
    /// <param name="timeRange">Time range for filtering statistics ('1 week', '1 month', '3 months').</param>
    /// <returns>All the view with general repository statistics.</returns>   
    [Authorize]
    [HttpGet("stats/{fullName}/{timeRange}")]
    [ProducesResponseType(typeof(CollaboratorsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetRepoOverviewStats(
        [FromRoute] string fullName,
        [FromRoute] string timeRange)
    {
        try
        {
            var accessToken = await HttpContext.GetTokenAsync("access_token");
            if (string.IsNullOrEmpty(accessToken))
            {
                return Unauthorized(new { error = "GitHub access token not found. Please login via GitHub first." });
            }

            var decodedFullName = Uri.UnescapeDataString(fullName);
        
            var username = User.GetGitHubLogin();
            if (!string.IsNullOrEmpty(username))
            {
                var user = await _dbService.GetUserbyUsername(username);
                if (user != null)
                    await _dbService.UpsertVisitedRepository(decodedFullName, user.Id);
            }

            RepoOverviewStatsDto statsOverview = await _gitHubService.GetRepositoryStatsAsync(accessToken, decodedFullName, timeRange);
            if (statsOverview == null)
            {
                return NotFound(new { error = $"Repository '{fullName}' not found or invalid." });
            }
            return Ok(statsOverview);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Bad request when querying statistics: {Message}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Octokit.AuthorizationException)
        {
            return Unauthorized(new { error = "Invalid GitHub token" });
        }
        catch (Octokit.NotFoundException)
        {
            return NotFound(new { error = $"Repository '{fullName}' not found or invalid." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching statistics for {FullName}", fullName);
            return StatusCode(500, new { error = "An error occurred while fetching statistics." });
        }
    }

    /// <summary>
    /// Get weekly activity (commits per week for the last 12 weeks) for a specific collaborator in a repository.
    /// </summary>
    /// <param name="fullName">Full name of the repository (owner/repo)</param>
    /// <param name="username">Name of the collaborator</param>
    /// <returns>List of commits per week (12 weeks)</returns>
        
    [Authorize]
    [HttpGet("repositories/{fullName}/collaborators/{username}/weekly-activity")]
    [ProducesResponseType(typeof(CollaboratorDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetCollaboratorWeeklyActivity(
        [FromRoute] string fullName,
        [FromRoute] string username)
    {
        var accessToken = await HttpContext.GetTokenAsync("access_token");
        if (string.IsNullOrEmpty(accessToken))
        {
            return Unauthorized(new { error = "GitHub access token not found. Please login via GitHub first." });
        }
        try
        {
            var decodedFullName = Uri.UnescapeDataString(fullName);
            var decodedUsername = Uri.UnescapeDataString(username);

            // Get weekly activity
            var collaborator = await _gitHubService.GetCollaboratorWeeklyActivityAsync(accessToken, decodedFullName, decodedUsername);

            if (collaborator == null)
            {
                return NotFound(new { error = $"Repository '{fullName}' or collaborator '{username}' not found or invalid." });
            }

            var weeklyActivityDto = new WeeklyActivityDto
            {
                Weeks = collaborator.Weeks
            };
            return Ok(weeklyActivityDto);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Bad request when querying weekly activity: {Message}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Octokit.AuthorizationException)
        {
            return Unauthorized(new { error = "Invalid GitHub token" });
        }
        catch (Octokit.NotFoundException)
        {
            return NotFound(new { error = $"Repository '{fullName}' or collaborator '{username}' not found or invalid." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching weekly activity for {FullName} and {Username}", fullName, username);
            return StatusCode(500, new { error = "An error occurred while fetching weekly activity." });
        }
    }

        /// <summary>
        /// Get detailed activity statistics for a specific collaborator in a repository for a given time range.
        /// </summary>
        /// <param name="fullName">Repository full name (owner/repo)</param>
        /// <param name="username">Collaborator username</param>
        /// <param name="range">Time range (1w, 1m, 3m)</param>
        /// <returns>Activity statistics</returns>
        [Authorize]
        [HttpGet("repositories/{fullName}/collaborators/{username}/activity")]
        [ProducesResponseType(typeof(CollaboratorActivityDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetCollaboratorActivity(
            [FromRoute] string fullName,
            [FromRoute] string username,
            [FromQuery] string range)
        {
            var accessToken = await HttpContext.GetTokenAsync("access_token");
            if (string.IsNullOrEmpty(accessToken))
            {
                return Unauthorized(new { error = "GitHub access token not found. Please login via GitHub first." });
            }
            try
            {
                var decodedFullName = Uri.UnescapeDataString(fullName);
                var decodedUsername = Uri.UnescapeDataString(username);
                var activity = await _gitHubService.GetCollaboratorActivityAsync(accessToken, decodedFullName, decodedUsername, range);
                if (activity == null)
                {
                    return NotFound(new { error = $"Repository '{fullName}' or collaborator '{username}' not found or invalid." });
                }
                return Ok(activity);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching collaborator activity for {FullName} and {Username}", fullName, username);
                return StatusCode(500, new { error = "An error occurred while fetching collaborator activity." });
            }
        }    

    /// <summary>
    /// Fetches normalized metadata of a repository by URL or owner/repo shorthand.
    /// </summary>
    /// <param name="url">Full repository URL or owner/repo shorthand</param>
    /// <returns>Normalized metadata or 404 error</returns>
    [Authorize]
    [HttpGet("repository-by-url")]
    [ProducesResponseType(typeof(RepositoryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRepositoryByUrl([FromQuery] string url)
    {
        var accessToken = await HttpContext.GetTokenAsync("access_token");
        if (string.IsNullOrEmpty(accessToken))
            return Unauthorized(new { error = "GitHub access token not found. Please login via GitHub first." });

        // Extract owner/repo from URL or shorthand
        string? ownerRepo = ParseOwnerRepoFromUrl(url);
        if (string.IsNullOrEmpty(ownerRepo))
            return BadRequest(new { error = "Invalid repository URL or format. Use full GitHub URL or 'owner/repo'." });

        try
        {
            var repoDto = await _gitHubService.GetRepositoryByOwnerRepoAsync(accessToken, ownerRepo);
            if (repoDto == null)
                return NotFound(new { error = "Repository not found or access denied." });
            return Ok(repoDto);
        }
        catch (Octokit.NotFoundException)
        {
            return NotFound(new { error = "Repository not found or access denied." });
        }
        catch (Octokit.AuthorizationException)
        {
            return NotFound(new { error = "Repository not found or access denied." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching repository by url");
            return StatusCode(500, new { error = "An error occurred while fetching repository metadata." });
        }
    }

    /// <summary>
    /// Get code changes (additions and deletions) for a specific collaborator in a repository over a given time range.
    /// </summary>
    /// <param name="fullName">owner/repo</param>
    /// <param name="username">login of collaborator</param>
    /// <param name="range">1w, 1m, 3m</param>
    /// <returns>{ additions, deletions }</returns>
    [Authorize]
    [HttpGet("repositories/{fullName}/collaborators/{username}/code-changes")]
    [ProducesResponseType(typeof(CollaboratorCodeChangesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetCollaboratorCodeChanges([FromRoute] string fullName, [FromRoute] string username, [FromQuery] string range)
    {
        var accessToken = await HttpContext.GetTokenAsync("access_token");
        if (string.IsNullOrEmpty(accessToken))
            return Unauthorized(new { error = "GitHub access token not found. Please login via GitHub first." });
        if (string.IsNullOrEmpty(fullName) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(range))
            return BadRequest(new { error = "Missing required parameters." });
        try
        {
            var result = await _gitHubService.GetCollaboratorCodeChangesAsync(accessToken, fullName, username, range);
            if (result == null)
                return NotFound(new { error = "Repository or collaborator not found." });
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Octokit.NotFoundException)
        {
            return NotFound(new { error = "Repository or collaborator not found." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching code changes for collaborator");
            return StatusCode(500, new { error = "An error occurred while fetching code changes." });
        }
    }


    // Helper to parse owner/repo from full URL or shorthand
    private string? ParseOwnerRepoFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        // if for shorthand owner/repo
        if (!url.Contains("github.com") && url.Contains("/")) return url;
        // if for full URL
        try
        {
            var uri = new Uri(url);
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            if (segments.Length >= 2)
                return segments[0] + "/" + segments[1];
        }
        catch { }
        return null;
    }

}