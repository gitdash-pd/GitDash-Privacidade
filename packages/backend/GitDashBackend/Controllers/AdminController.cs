using GitDashBackend.Configurations;
using GitDashBackend.Domain.DTOs;
using GitDashBackend.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GitDashBackend.Controllers;

/// <summary>
/// General listings that mix data from several users. They need their own permission (Admin policy),
/// configured through Admin:Logins.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.Admin)]
[Route("api/admin")]
[Produces("application/json")]
public class AdminController : ControllerBase
{
    private const int MaxLogs = 500;
    private readonly IDbService _dbService;

    public AdminController(IDbService dbService)
    {
        _dbService = dbService;
    }

    /// <summary>
    /// Most recent access logs of all users, for incident analysis.
    /// </summary>
    /// <param name="limit">Number of entries (1 to 500).</param>
    [HttpGet("logs")]
    [ProducesResponseType(typeof(List<AdminAccessLogDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetLogs([FromQuery] int limit = 100)
    {
        var take = Math.Clamp(limit, 1, MaxLogs);
        return Ok(await _dbService.GetRecentAccessLogsAsync(take));
    }
}
