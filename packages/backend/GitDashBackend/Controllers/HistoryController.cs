using GitDashBackend.Domain.DTOs;
using GitDashBackend.Extensions;
using GitDashBackend.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GitDashBackend.Controllers;

/// <summary>
/// Data produced by each person's use of GitDash: repository history and access logs.
/// The owner always comes from the authenticated session. Names or ids sent in the request are ignored.
/// </summary>
[ApiController]
[Authorize]
[Route("api/me")]
[Produces("application/json")]
public class HistoryController : ControllerBase
{
    private readonly IDbService _dbService;

    public HistoryController(IDbService dbService)
    {
        _dbService = dbService;
    }

    /// <summary>
    /// Repositories visited by the session user, most recent first.
    /// </summary>
    [HttpGet("history")]
    [ProducesResponseType(typeof(List<HistoryEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetHistory()
    {
        var userId = await GetSessionUserIdAsync();
        if (userId == null)
            return Unauthorized();

        return Ok(await _dbService.GetHistoryAsync(userId.Value));
    }

    /// <summary>
    /// Access logs of the session user, most recent first.
    /// </summary>
    [HttpGet("logs")]
    [ProducesResponseType(typeof(List<AccessLogDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetLogs()
    {
        var userId = await GetSessionUserIdAsync();
        if (userId == null)
            return Unauthorized();

        return Ok(await _dbService.GetAccessLogsAsync(userId.Value));
    }

    private async Task<int?> GetSessionUserIdAsync()
    {
        var login = User.GetGitHubLogin();
        if (string.IsNullOrEmpty(login))
            return null;

        var user = await _dbService.GetUserbyUsername(login);
        return user?.Id;
    }
}
