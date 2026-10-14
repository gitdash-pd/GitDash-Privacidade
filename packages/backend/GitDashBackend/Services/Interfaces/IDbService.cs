using GitDashBackend.Domain.DTOs;
using GitDashBackend.Models;

namespace GitDashBackend.Services.Interfaces;

public interface IDbService
{
    Task<User?> GetUserbyUsername(string username);

    Task UpsertVisitedRepository(String decodedFullName, int userId);

    Task InsertNewLog(int userId, string visitedEndpoint);

    Task<List<Repository>> GetRecentRepositories (string username);

    /// <summary>History of one user, selected by the id taken from the session.</summary>
    Task<List<HistoryEntryDto>> GetHistoryAsync(int userId);

    /// <summary>Access logs of one user, selected by the id taken from the session.</summary>
    Task<List<AccessLogDto>> GetAccessLogsAsync(int userId);

    /// <summary>Most recent access logs of all users. Only for the Admin policy.</summary>
    Task<List<AdminAccessLogDto>> GetRecentAccessLogsAsync(int limit);
}
